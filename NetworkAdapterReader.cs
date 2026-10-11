using System.Net.NetworkInformation;

namespace LdmsOutletCameraHelper;

public static class NetworkAdapterReader
{
    public static IReadOnlyList<string> ReadMacAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
            .Select(adapter => adapter.GetPhysicalAddress().ToString())
            .Where(address => address.Length > 0)
            .Distinct()
            .ToList();
}
