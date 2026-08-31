namespace LdmsOutletCameraHelper;

public class CameraHelperOptions
{
    public CameraOptions Camera { get; set; } = new();
    public int ListenPort { get; set; } = 8770;
    public string[] AllowedOrigins { get; set; } = [];
}

public class CameraOptions
{
    public string SnapshotUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool AllowSelfSignedCert { get; set; }
    public int TimeoutSeconds { get; set; } = 10;
}
