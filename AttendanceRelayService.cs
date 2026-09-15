using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LdmsOutletCameraHelper;

public record QueuedAttendanceEvent(string ContentType, string BodyBase64);

// The fingerprint terminal can only push plain HTTP to a bare IPv4 address (confirmed
// against real hardware — see aidlc-docs/construction/spike-s0-device-https.md), so it
// cannot reach ldms-web-api's HTTPS webhook directly. This service receives that LAN
// push, durably queues it to disk (so a restart or a network blip doesn't lose a punch),
// and relays it over HTTPS to the unchanged webhook contract. The relay is a transparent
// byte-for-byte forward — parsing stays entirely in ldms-web-api's IDeviceEventAdapter.
public class AttendanceRelayService(IOptionsMonitor<CameraHelperOptions> options, ILogger<AttendanceRelayService> logger)
    : BackgroundService
{
    private readonly SemaphoreSlim _signal = new(0);

    private static string QueueDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "LdmsOutletCameraHelper",
        "attendance-queue");

    public async Task EnqueueAsync(string contentType, byte[] body, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(QueueDirectory);
        string fileName = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json";
        var queued = new QueuedAttendanceEvent(contentType, Convert.ToBase64String(body));
        await File.WriteAllTextAsync(Path.Combine(QueueDirectory, fileName), JsonSerializer.Serialize(queued), cancellationToken);
        _signal.Release();
    }

    public int PendingCount() => Directory.Exists(QueueDirectory) ? Directory.GetFiles(QueueDirectory, "*.json").Length : 0;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await DrainOnceAsync(stoppingToken);
            try
            {
                await _signal.WaitAsync(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task DrainOnceAsync(CancellationToken cancellationToken)
    {
        AttendanceOptions attendance = options.CurrentValue.Attendance;
        if (!attendance.Enabled || string.IsNullOrWhiteSpace(attendance.LdmsApiBaseUrl) || !Directory.Exists(QueueDirectory))
        {
            return;
        }

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        string url = $"{attendance.LdmsApiBaseUrl.TrimEnd('/')}/api/attendance/device-events?deviceId={attendance.DeviceId}";

        foreach (string file in Directory.GetFiles(QueueDirectory, "*.json").OrderBy(f => f))
        {
            if (cancellationToken.IsCancellationRequested) return;

            try
            {
                QueuedAttendanceEvent? queued = JsonSerializer.Deserialize<QueuedAttendanceEvent>(await File.ReadAllTextAsync(file, cancellationToken));
                if (queued is null)
                {
                    File.Delete(file);
                    continue;
                }

                using var content = new ByteArrayContent(Convert.FromBase64String(queued.BodyBase64));
                content.Headers.ContentType = MediaTypeHeaderValue.TryParse(queued.ContentType, out var parsed)
                    ? parsed
                    : new MediaTypeHeaderValue("application/octet-stream");

                using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                request.Headers.Add("X-Device-Secret", attendance.DeviceSecret);

                using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    File.Delete(file);
                    continue;
                }

                logger.LogWarning("Attendance relay got HTTP {StatusCode} for {File}; will retry.", (int)response.StatusCode, Path.GetFileName(file));
                return; // preserve arrival order — stop here and retry from this event next tick
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Attendance relay failed for {File}; will retry.", Path.GetFileName(file));
                return;
            }
        }
    }
}
