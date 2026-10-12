using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LdmsOutletCameraHelper;

public class BadgeApiClient(HttpClient httpClient, IOptionsMonitor<CameraHelperOptions> options, ILogger<BadgeApiClient> logger)
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient = httpClient;
    private readonly IOptionsMonitor<CameraHelperOptions> _options = options;
    private readonly ILogger<BadgeApiClient> _logger = logger;

    public async Task<string?> RequestNonceAsync(int deviceId, CancellationToken cancellationToken)
    {
        NonceData? data = await PostAsync<object, NonceData>("api/badge-auth/nonce", new { deviceId }, cancellationToken);
        return data?.Nonce;
    }

    public async Task<string?> RequestTicketAsync(TicketRequest request, CancellationToken cancellationToken)
    {
        TicketData? data = await PostAsync<TicketRequest, TicketData>("api/badge-auth/ticket", request, cancellationToken);
        return data?.Ticket;
    }

    private async Task<TResponse?> PostAsync<TRequest, TResponse>(string relativePath, TRequest body, CancellationToken cancellationToken)
        where TResponse : class
    {
        string baseUrl = ResolveApiBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        try
        {
            using HttpResponseMessage response = await _httpClient.PostAsJsonAsync(
                $"{baseUrl.TrimEnd('/')}/{relativePath}", body, WireJson, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            ApiEnvelope<TResponse>? envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<TResponse>>(WireJson, timeout.Token);
            return envelope?.Data;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning("Badge API call to {Path} failed: {ExceptionType}", relativePath, exception.GetType().Name);
            return null;
        }
    }

    private string ResolveApiBaseUrl()
    {
        CameraHelperOptions current = _options.CurrentValue;
        return string.IsNullOrWhiteSpace(current.BadgeLogin.ApiBaseUrl)
            ? current.Attendance.LdmsApiBaseUrl
            : current.BadgeLogin.ApiBaseUrl;
    }

    private record ApiEnvelope<T>(T? Data);

    private record NonceData(string? Nonce);

    private record TicketData(string? Ticket);
}

public record TicketRequest(
    int DeviceId,
    string Nonce,
    string SecretHash,
    string Signature,
    string BindingHash,
    IReadOnlyList<string> MacAddresses);
