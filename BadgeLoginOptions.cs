namespace LdmsOutletCameraHelper;

public class BadgeLoginOptions
{
    public string ApiBaseUrl { get; set; } = string.Empty;
    public string[] FrontendOrigins { get; set; } = [];
    public string DeviceKeyName { get; set; } = "LdmsOutletBadgeDeviceKey";
}
