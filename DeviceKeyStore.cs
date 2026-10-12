using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace LdmsOutletCameraHelper;

public class DeviceKeyStore(IOptionsMonitor<CameraHelperOptions> options)
{
    private static readonly CngProvider KeyProvider = CngProvider.MicrosoftSoftwareKeyStorageProvider;

    private readonly IOptionsMonitor<CameraHelperOptions> _options = options;

    private string KeyName => _options.CurrentValue.BadgeLogin.DeviceKeyName;

    public string GetOrCreatePublicKey()
    {
        using CngKey key = OpenOrCreateKey();
        using ECDsaCng signer = new(key);
        return Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo());
    }

    public string? Sign(string message)
    {
        if (!CngKey.Exists(KeyName, KeyProvider, CngKeyOpenOptions.MachineKey))
        {
            return null;
        }

        using CngKey key = CngKey.Open(KeyName, KeyProvider, CngKeyOpenOptions.MachineKey);
        using ECDsaCng signer = new(key);
        byte[] signature = signer.SignData(
            Encoding.UTF8.GetBytes(message),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Convert.ToBase64String(signature);
    }

    private CngKey OpenOrCreateKey()
    {
        if (CngKey.Exists(KeyName, KeyProvider, CngKeyOpenOptions.MachineKey))
        {
            return CngKey.Open(KeyName, KeyProvider, CngKeyOpenOptions.MachineKey);
        }

        CngKeyCreationParameters creationParameters = new()
        {
            Provider = KeyProvider,
            KeyCreationOptions = CngKeyCreationOptions.MachineKey,
            ExportPolicy = CngExportPolicies.None,
            KeyUsage = CngKeyUsages.Signing
        };
        return CngKey.Create(CngAlgorithm.ECDsaP256, KeyName, creationParameters);
    }
}
