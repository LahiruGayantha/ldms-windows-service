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
}
