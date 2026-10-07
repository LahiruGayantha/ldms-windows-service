namespace LdmsOutletCameraHelper;

public class AttendanceOptions
{
    public bool Enabled { get; set; }
    public int ListenPort { get; set; } = 8771;
    public int DeviceId { get; set; }
    public string DeviceSecret { get; set; } = string.Empty;
    public string LdmsApiBaseUrl { get; set; } = string.Empty;

    /// <summary>Optional LAN-IP allowlist. The terminal cannot send our webhook secret on its own
    /// push, so this is the only check available at the network boundary.</summary>
    public string AllowedDeviceIp { get; set; } = string.Empty;

    public AttendancePollingOptions Polling { get; set; } = new();
}

public class AttendancePollingOptions
{
    public bool Enabled { get; set; }
    public string DeviceBaseUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool AllowSelfSignedCert { get; set; }
    public int IntervalSeconds { get; set; } = 60;
    public int InitialLookbackHours { get; set; } = 24;
    public int PageSize { get; set; } = 30;
    public int TimeoutSeconds { get; set; } = 15;
}
