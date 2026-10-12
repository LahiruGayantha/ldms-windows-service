using System.Text.Json;

namespace LdmsOutletCameraHelper;

public class DeviceIdentityStore
{
    private static readonly string IdentityFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "LdmsOutletCameraHelper",
        "device-identity.json");

    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public async Task<int?> LoadDeviceIdAsync(CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(IdentityFilePath))
            {
                return null;
            }

            await using FileStream stream = File.OpenRead(IdentityFilePath);
            DeviceIdentity? identity = await JsonSerializer.DeserializeAsync<DeviceIdentity>(stream, cancellationToken: cancellationToken);
            return identity?.DeviceId;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveDeviceIdAsync(int deviceId, CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(IdentityFilePath)!);
            await using FileStream stream = File.Create(IdentityFilePath);
            await JsonSerializer.SerializeAsync(stream, new DeviceIdentity(deviceId), cancellationToken: cancellationToken);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private record DeviceIdentity(int DeviceId);
}
