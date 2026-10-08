using System.Security.Cryptography;
using CloudEntitle.Compliance.Services;

namespace CloudEntitle.Desktop.Services;

public sealed class WindowsComplianceBundleSigner : IComplianceBundleSigner
{
    private const string KeyName = "YASK-CIEM-Compliance-Bundle-Signing-v1";
    private static readonly object Gate = new();

    public ComplianceBundleSignatureDocument Sign(ReadOnlySpan<byte> manifestBytes)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Compliance bundle signing requires Windows CNG key storage.");
        if (manifestBytes.IsEmpty) throw new ArgumentException("The compliance manifest cannot be empty.", nameof(manifestBytes));

        lock (Gate)
        {
            using var key = OpenOrCreateKey();
            using var signer = new ECDsaCng(key);
            var publicKey = signer.ExportSubjectPublicKeyInfo();
            var signature = signer.SignData(manifestBytes, HashAlgorithmName.SHA256);
            var keyId = Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant();
            var manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
            return new ComplianceBundleSignatureDocument("ECDSA-P256-SHA256", keyId,
                Convert.ToBase64String(publicKey), manifestHash, Convert.ToBase64String(signature));
        }
    }

    private static CngKey OpenOrCreateKey()
    {
        var provider = CngProvider.MicrosoftSoftwareKeyStorageProvider;
        if (CngKey.Exists(KeyName, provider)) return CngKey.Open(KeyName, provider);
        var creation = new CngKeyCreationParameters
        {
            Provider = provider,
            ExportPolicy = CngExportPolicies.None,
            KeyUsage = CngKeyUsages.Signing
        };
        try { return CngKey.Create(CngAlgorithm.ECDsaP256, KeyName, creation); }
        catch (CryptographicException) when (CngKey.Exists(KeyName, provider))
        {
            return CngKey.Open(KeyName, provider);
        }
    }
}
