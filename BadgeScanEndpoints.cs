using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace LdmsOutletCameraHelper;

public static class BadgeScanEndpoints
{
    public const string CorsPolicyName = "BadgeFrontend";

    private const int MaxQrValueLength = 256;
    private const int BindingHashLength = 64;

    public static bool IsBadgeRoute(PathString path) =>
        path.StartsWithSegments("/device") || path.StartsWithSegments("/badge");

    public static void MapBadgeScanEndpoints(this WebApplication app)
    {
        RouteGroupBuilder badgeRoutes = app.MapGroup(string.Empty);
        badgeRoutes.AddEndpointFilter(RejectNonLoopbackListener);

        badgeRoutes.MapPost("/device/keys", (DeviceKeyStore keyStore) =>
            Results.Ok(new
            {
                publicKey = keyStore.GetOrCreatePublicKey(),
                macAddresses = NetworkAdapterReader.ReadMacAddresses()
            }));

        badgeRoutes.MapPost("/device/identity", async (DeviceIdentityRequest request, DeviceIdentityStore identityStore, CancellationToken cancellationToken) =>
        {
            if (request.DeviceId <= 0)
            {
                return Results.BadRequest();
            }

            await identityStore.SaveDeviceIdAsync(request.DeviceId, cancellationToken);
            return Results.NoContent();
        });

        badgeRoutes.MapPost("/badge/scan", async (
            BadgeScanRequest request,
            DeviceKeyStore keyStore,
            DeviceIdentityStore identityStore,
            BadgeApiClient apiClient,
            CancellationToken cancellationToken) =>
        {
            if (!IsScanRequestWellFormed(request))
            {
                return Results.Unauthorized();
            }

            int? deviceId = await identityStore.LoadDeviceIdAsync(cancellationToken);
            if (deviceId is null)
            {
                return Results.Unauthorized();
            }

            string secretHash = HashAsLowercaseHex(request.QrValue);
            string? nonce = await apiClient.RequestNonceAsync(deviceId.Value, cancellationToken);
            if (nonce is null)
            {
                return Results.Unauthorized();
            }

            string? signature = keyStore.Sign($"{nonce}:{secretHash}");
            if (signature is null)
            {
                return Results.Unauthorized();
            }

            string? ticket = await apiClient.RequestTicketAsync(
                new TicketRequest(deviceId.Value, nonce, secretHash, signature, request.BindingHash, NetworkAdapterReader.ReadMacAddresses()),
                cancellationToken);

            return ticket is null ? Results.Unauthorized() : Results.Ok(new { ticket });
        });
    }

    private static async ValueTask<object?> RejectNonLoopbackListener(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        HttpContext httpContext = context.HttpContext;
        int loopbackPort = httpContext.RequestServices.GetRequiredService<IOptionsMonitor<CameraHelperOptions>>().CurrentValue.ListenPort;
        bool cameFromLoopbackListener =
            httpContext.Connection.LocalPort == loopbackPort
            && IPAddress.IsLoopback(httpContext.Connection.RemoteIpAddress ?? IPAddress.None);

        return cameFromLoopbackListener ? await next(context) : Results.NotFound();
    }

    private static bool IsScanRequestWellFormed(BadgeScanRequest request) =>
        !string.IsNullOrWhiteSpace(request.QrValue)
        && request.QrValue.Length <= MaxQrValueLength
        && request.BindingHash is { Length: BindingHashLength };

    private static string HashAsLowercaseHex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

public record BadgeScanRequest(string QrValue, string BindingHash);

public record DeviceIdentityRequest(int DeviceId);
