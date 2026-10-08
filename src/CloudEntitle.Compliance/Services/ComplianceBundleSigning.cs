using System.Security.Cryptography;
using System.Text.Json;

namespace CloudEntitle.Compliance.Services;

public sealed record ComplianceBundleSignatureDocument(
    string Algorithm,
    string KeyId,
    string PublicKeySubjectPublicKeyInfo,
    string ManifestSha256,
    string Signature);

public interface IComplianceBundleSigner
{
    ComplianceBundleSignatureDocument Sign(ReadOnlySpan<byte> manifestBytes);
}

public static class ComplianceBundleSignatureVerifier
{
    private static readonly JsonSerializerOptions SignatureJsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static ComplianceBundleSignatureDocument ReadSignature(ReadOnlySpan<byte> signatureBytes)
        => JsonSerializer.Deserialize<ComplianceBundleSignatureDocument>(signatureBytes, SignatureJsonOptions)
            ?? throw new InvalidDataException("The compliance bundle signature document is empty.");

    public static bool VerifyArtifact(ReadOnlySpan<byte> manifestBytes, string artifactName, ReadOnlySpan<byte> artifactBytes)
    {
        if (string.IsNullOrWhiteSpace(artifactName)) return false;
        try
        {
            using var manifest = JsonDocument.Parse(manifestBytes.ToArray());
            var expectedHash = manifest.RootElement.GetProperty("files").GetProperty(artifactName).GetString();
            if (string.IsNullOrWhiteSpace(expectedHash)) return false;
            var actualHash = Convert.ToHexString(SHA256.HashData(artifactBytes)).ToLowerInvariant();
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expectedHash), Convert.FromHexString(actualHash));
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            return false;
        }
    }

    public static bool Verify(ReadOnlySpan<byte> manifestBytes, ComplianceBundleSignatureDocument signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        if (!string.Equals(signature.Algorithm, "ECDSA-P256-SHA256", StringComparison.Ordinal)) return false;
        try
        {
            var manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(manifestHash),
                    Convert.FromHexString(signature.ManifestSha256))) return false;

            var publicKey = Convert.FromBase64String(signature.PublicKeySubjectPublicKeyInfo);
            var keyId = Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant();
            if (!string.Equals(keyId, signature.KeyId, StringComparison.Ordinal)) return false;

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
            return bytesRead == publicKey.Length
                && ecdsa.ExportParameters(false).Curve.Oid.Value == "1.2.840.10045.3.1.7"
                && ecdsa.VerifyData(manifestBytes, Convert.FromBase64String(signature.Signature), HashAlgorithmName.SHA256);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
