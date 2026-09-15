namespace LdmsOutletCameraHelper;

public class CameraHelperOptions
{
    public CameraOptions Camera { get; set; } = new();
    public int ListenPort { get; set; } = 8770;
    public string[] AllowedOrigins { get; set; } = [];
    public AttendanceOptions Attendance { get; set; } = new();
    public PrinterOptions Printer { get; set; } = new();
}

public class CameraOptions
{
    public string SnapshotUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool AllowSelfSignedCert { get; set; }
    public int TimeoutSeconds { get; set; } = 10;
}

public class PrinterOptions
{
    public bool Enabled { get; set; } = true;
    public string PortName { get; set; } = string.Empty;
}
