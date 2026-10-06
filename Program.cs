using LdmsOutletCameraHelper;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService(options => options.ServiceName = "LdmsOutletCameraHelper");

string sharedConfigPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "LdmsOutletCameraHelper",
    "appsettings.json");
builder.Configuration.AddJsonFile(sharedConfigPath, optional: true, reloadOnChange: true);

builder.Services.Configure<CameraHelperOptions>(builder.Configuration);
builder.Services.AddSingleton<CameraSnapshotClient>();
builder.Services.AddSingleton<TagPrinterClient>();
builder.Services.AddSingleton<AttendanceRelayService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AttendanceRelayService>());

var helperOptions = builder.Configuration.Get<CameraHelperOptions>() ?? new CameraHelperOptions();

// The camera endpoint is loopback-only (browser calls it from the same PC). The
// attendance endpoint must accept connections from the fingerprint terminal elsewhere
// on the outlet LAN, so it binds all interfaces — a deliberately wider exposure than the
// camera helper has ever had, scoped to the outlet's own trusted LAN (see AllowedDeviceIp).
var listenUrls = new List<string> { $"http://127.0.0.1:{helperOptions.ListenPort}" };
if (helperOptions.Attendance.Enabled)
{
    listenUrls.Add($"http://0.0.0.0:{helperOptions.Attendance.ListenPort}");
}
builder.WebHost.UseUrls(listenUrls.ToArray());

builder.Services.AddCors(corsOptions =>
{
    corsOptions.AddPolicy("OutletFrontend", policy =>
    {
        if (helperOptions.AllowedOrigins.Length == 0)
        {
            policy.AllowAnyOrigin();
        }
        else
        {
            policy.WithOrigins(helperOptions.AllowedOrigins);
        }

        policy.WithMethods("GET", "POST").AllowAnyHeader();
    });
});

var app = builder.Build();

app.UsePrivateNetworkAccess();
app.UseCors("OutletFrontend");

app.MapGet("/health", (IOptionsMonitor<CameraHelperOptions> monitor, AttendanceRelayService relay) =>
{
    CameraHelperOptions current = monitor.CurrentValue;
    bool cameraConfigured = !string.IsNullOrWhiteSpace(current.Camera.SnapshotUrl);
    bool printerConfigured = current.Printer.Enabled && !string.IsNullOrWhiteSpace(current.Printer.PortName);
    return Results.Ok(new
    {
        status = "ok",
        cameraConfigured,
        printerConfigured,
        attendanceEnabled = current.Attendance.Enabled,
        attendanceQueueDepth = current.Attendance.Enabled ? relay.PendingCount() : (int?)null
    });
});

app.MapGet("/snapshot", async (CameraSnapshotClient snapshotClient, CancellationToken cancellationToken) =>
{
    SnapshotResult result = await snapshotClient.GetSnapshotAsync(cancellationToken);
    return result.Success
        ? Results.File(result.Content!, result.ContentType)
        : Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status502BadGateway);
});

app.MapPost("/print/tag", async (PrintTagRequest request, TagPrinterClient printerClient, CancellationToken cancellationToken) =>
{
    PrintResult result = await printerClient.PrintAsync(request.Tag, request.TagCount, cancellationToken);
    return Results.Ok(new { success = result.Success, message = result.Message });
});

app.MapPost("/print/guest-laundry-tag", async (PrintGuestLaundryTagRequest request, TagPrinterClient printerClient, CancellationToken cancellationToken) =>
{
    PrintResult result = await printerClient.PrintGuestLaundryTagAsync(request, cancellationToken);
    return Results.Ok(new { success = result.Success, message = result.Message });
});

if (helperOptions.Attendance.Enabled)
{
    app.MapPost("/attendance/events", async (HttpRequest request, AttendanceRelayService relay, IOptionsMonitor<CameraHelperOptions> monitor, CancellationToken cancellationToken) =>
    {
        AttendanceOptions attendance = monitor.CurrentValue.Attendance;
        string? remoteIp = request.HttpContext.Connection.RemoteIpAddress?.MapToIPv4().ToString();
        if (!string.IsNullOrWhiteSpace(attendance.AllowedDeviceIp) && remoteIp != attendance.AllowedDeviceIp)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        using var bodyStream = new MemoryStream();
        await request.Body.CopyToAsync(bodyStream, cancellationToken);
        string contentType = request.ContentType ?? "application/octet-stream";
        await relay.EnqueueAsync(contentType, bodyStream.ToArray(), cancellationToken);
        return Results.Ok();
    });
}

app.Run();

public record PrintTagRequest(string Tag, int TagCount);

public record PrintGuestLaundryTagRequest(
    string HotelCode,
    string IncomingNumber,
    string TagNumber,
    int TotalPcs,
    string? ProcessType,
    string? DeliveryType,
    string QrPayload);
