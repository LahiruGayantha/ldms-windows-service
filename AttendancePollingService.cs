using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace LdmsOutletCameraHelper;

public class AttendancePollingService(
    IOptionsMonitor<CameraHelperOptions> options,
    AttendanceRelayService relay,
    ILogger<AttendancePollingService> logger) : BackgroundService
{
    private const string AcsEventPath = "/ISAPI/AccessControl/AcsEvent?format=json";
    private const int MaxSearchIdLength = 16;
    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:sszzz";

    private readonly SemaphoreSlim _pollLock = new(1, 1);

    private static string CheckpointPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "LdmsOutletCameraHelper",
        "attendance-poll-checkpoint.txt");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            AttendanceOptions attendance = options.CurrentValue.Attendance;

            if (attendance.Enabled && attendance.Polling.Enabled)
            {
                await PollOnceSafelyAsync(attendance.Polling, stoppingToken);
            }

            int intervalSeconds = Math.Max(attendance.Polling.IntervalSeconds, 10);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task<AttendanceSyncResult> SyncNowAsync(DateTimeOffset? requestedWindowStart, CancellationToken cancellationToken)
    {
        AttendancePollingOptions polling = options.CurrentValue.Attendance.Polling;
        await _pollLock.WaitAsync(cancellationToken);
        try
        {
            return await PollOnceAsync(polling, requestedWindowStart, cancellationToken);
        }
        finally
        {
            _pollLock.Release();
        }
    }

    private async Task PollOnceSafelyAsync(AttendancePollingOptions polling, CancellationToken cancellationToken)
    {
        try
        {
            await _pollLock.WaitAsync(cancellationToken);
            try
            {
                await PollOnceAsync(polling, null, cancellationToken);
            }
            finally
            {
                _pollLock.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Fingerprint terminal poll failed; will retry on the next tick.");
        }
    }

    private async Task<AttendanceSyncResult> PollOnceAsync(
        AttendancePollingOptions polling,
        DateTimeOffset? requestedWindowStart,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(polling.DeviceBaseUrl))
        {
            logger.LogWarning("Attendance polling is enabled but Attendance:Polling:DeviceBaseUrl is not configured.");
            return new AttendanceSyncResult(0, null, null, "Attendance:Polling:DeviceBaseUrl is not configured.");
        }

        DateTimeOffset? checkpoint = ReadCheckpoint();
        DateTimeOffset windowStart = requestedWindowStart
            ?? checkpoint
            ?? DateTimeOffset.Now.AddHours(-Math.Max(polling.InitialLookbackHours, 1));
        DateTimeOffset windowEnd = DateTimeOffset.Now;
        DateTimeOffset latestEventTime = windowStart;
        int enqueuedCount = 0;

        using HttpClient httpClient = CreateDeviceHttpClient(polling);
        string searchId = Guid.NewGuid().ToString("N")[..MaxSearchIdLength];
        int resultPosition = 0;
        bool hasMorePages = true;

        while (hasMorePages)
        {
            JsonNode? page = await FetchPageAsync(httpClient, polling, searchId, windowStart, windowEnd, resultPosition, cancellationToken);
            JsonArray events = page?["AcsEvent"]?["InfoList"]?.AsArray() ?? [];

            foreach (JsonNode? deviceEvent in events)
            {
                if (deviceEvent is null) continue;

                QueuedPunch? punch = ToQueuedPunch(deviceEvent, polling.DeviceBaseUrl);
                if (punch is null) continue;

                await relay.EnqueueAsync("application/json", Encoding.UTF8.GetBytes(punch.Body), cancellationToken);
                enqueuedCount++;
                if (punch.OccurredAt > latestEventTime) latestEventTime = punch.OccurredAt;
            }

            resultPosition += events.Count;
            hasMorePages = events.Count > 0
                && string.Equals(page?["AcsEvent"]?["responseStatusStrg"]?.ToString(), "MORE", StringComparison.OrdinalIgnoreCase);
        }

        if (checkpoint is null || latestEventTime > checkpoint)
        {
            WriteCheckpoint(latestEventTime);
        }

        if (enqueuedCount > 0)
        {
            logger.LogInformation("Fingerprint terminal poll queued {Count} attendance event(s).", enqueuedCount);
        }

        return new AttendanceSyncResult(enqueuedCount, windowStart, windowEnd, null);
    }

    private static HttpClient CreateDeviceHttpClient(AttendancePollingOptions polling)
    {
        var handler = new SocketsHttpHandler
        {
            Credentials = new NetworkCredential(polling.Username, polling.Password),
            PreAuthenticate = false
        };
        if (polling.AllowSelfSignedCert)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(Math.Max(polling.TimeoutSeconds, 5)) };
    }

    private static async Task<JsonNode?> FetchPageAsync(
        HttpClient httpClient,
        AttendancePollingOptions polling,
        string searchId,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        int resultPosition,
        CancellationToken cancellationToken)
    {
        var searchCondition = new JsonObject
        {
            ["AcsEventCond"] = new JsonObject
            {
                ["searchID"] = searchId,
                ["searchResultPosition"] = resultPosition,
                ["maxResults"] = Math.Clamp(polling.PageSize, 1, 30),
                ["major"] = 5,
                ["minor"] = 0,
                ["startTime"] = windowStart.ToString(TimestampFormat, CultureInfo.InvariantCulture),
                ["endTime"] = windowEnd.ToString(TimestampFormat, CultureInfo.InvariantCulture)
            }
        };

        using var content = new StringContent(searchCondition.ToJsonString(), Encoding.UTF8, "application/json");
        string url = $"{polling.DeviceBaseUrl.TrimEnd('/')}{AcsEventPath}";
        using HttpResponseMessage response = await httpClient.PostAsync(url, content, cancellationToken);
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Terminal returned HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return JsonNode.Parse(responseBody);
    }

    private static QueuedPunch? ToQueuedPunch(JsonNode deviceEvent, string deviceBaseUrl)
    {
        string? employeeNumber = deviceEvent["employeeNoString"]?.ToString();
        string? time = deviceEvent["time"]?.ToString();
        if (string.IsNullOrWhiteSpace(employeeNumber) || !DateTimeOffset.TryParse(time, out DateTimeOffset occurredAt))
        {
            return null;
        }

        var pushShape = new JsonObject
        {
            ["dateTime"] = time,
            ["ipAddress"] = new Uri(deviceBaseUrl).Host,
            ["AccessControllerEvent"] = new JsonObject
            {
                ["employeeNoString"] = employeeNumber,
                ["serialNo"] = deviceEvent["serialNo"]?.ToString(),
                ["minor"] = deviceEvent["minor"]?.ToString()
            }
        };

        return new QueuedPunch(pushShape.ToJsonString(), occurredAt);
    }

    private static DateTimeOffset? ReadCheckpoint()
    {
        if (!File.Exists(CheckpointPath)) return null;

        return DateTimeOffset.TryParse(File.ReadAllText(CheckpointPath), out DateTimeOffset checkpoint) ? checkpoint : null;
    }

    private static void WriteCheckpoint(DateTimeOffset checkpoint)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CheckpointPath)!);
        File.WriteAllText(CheckpointPath, checkpoint.ToString("O"));
    }

    private record QueuedPunch(string Body, DateTimeOffset OccurredAt);
}

public record AttendanceSyncResult(int QueuedCount, DateTimeOffset? WindowStart, DateTimeOffset? WindowEnd, string? Error);
