using System.Net;
using Microsoft.Extensions.Options;

namespace LdmsOutletCameraHelper;

public record SnapshotResult(bool Success, byte[]? Content, string ContentType, string? Error)
{
    public static SnapshotResult Ok(byte[] content, string contentType) => new(true, content, contentType, null);
    public static SnapshotResult Failed(string error) => new(false, null, "application/json", error);
}

// Fetches a still frame from the outlet's LAN camera over HTTP Digest auth.
// SocketsHttpHandler performs the Digest challenge/response automatically once a
// NetworkCredential is supplied. Snapshots are infrequent (one per item added to an
// order) so a fresh handler per request is fine and avoids stale-credential issues
// when the config file is edited.
public class CameraSnapshotClient(IOptionsMonitor<CameraHelperOptions> options, ILogger<CameraSnapshotClient> logger)
{
    public async Task<SnapshotResult> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        CameraOptions camera = options.CurrentValue.Camera;
        if (string.IsNullOrWhiteSpace(camera.SnapshotUrl))
        {
            return SnapshotResult.Failed("Camera snapshot URL is not configured on this outlet.");
        }

        var handler = new SocketsHttpHandler
        {
            Credentials = new NetworkCredential(camera.Username, camera.Password),
            PreAuthenticate = false
        };
        if (camera.AllowSelfSignedCert)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        using var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(camera.TimeoutSeconds <= 0 ? 10 : camera.TimeoutSeconds)
        };

        try
        {
            using HttpResponseMessage response = await httpClient.GetAsync(camera.SnapshotUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Outlet camera returned {StatusCode} for {SnapshotUrl}.", (int)response.StatusCode, camera.SnapshotUrl);
                return SnapshotResult.Failed($"Camera returned HTTP {(int)response.StatusCode}.");
            }

            byte[] content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            string contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            return SnapshotResult.Ok(content, contentType);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to fetch a snapshot from the outlet camera.");
            return SnapshotResult.Failed("Could not reach the outlet camera.");
        }
    }
}
