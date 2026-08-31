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

var helperOptions = builder.Configuration.Get<CameraHelperOptions>() ?? new CameraHelperOptions();
builder.WebHost.UseUrls($"http://127.0.0.1:{helperOptions.ListenPort}");

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

        policy.WithMethods("GET").AllowAnyHeader();
    });
});

var app = builder.Build();

app.UsePrivateNetworkAccess();
app.UseCors("OutletFrontend");

app.MapGet("/health", (IOptionsMonitor<CameraHelperOptions> monitor) =>
{
    bool cameraConfigured = !string.IsNullOrWhiteSpace(monitor.CurrentValue.Camera.SnapshotUrl);
    return Results.Ok(new { status = "ok", cameraConfigured });
});

app.MapGet("/snapshot", async (CameraSnapshotClient snapshotClient, CancellationToken cancellationToken) =>
{
    SnapshotResult result = await snapshotClient.GetSnapshotAsync(cancellationToken);
    return result.Success
        ? Results.File(result.Content!, result.ContentType)
        : Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status502BadGateway);
});

app.Run();
