using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;

namespace CloudEntitle.LicenseServer.Services;

public sealed class AuditIntegrityKeyRing : IDisposable
{
    private readonly Dictionary<string, byte[]> _keys;

    public AuditIntegrityKeyRing(string currentKeyId, IReadOnlyDictionary<string, byte[]> keys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentKeyId);
        ArgumentNullException.ThrowIfNull(keys);
        if (!keys.TryGetValue(currentKeyId, out var currentKey))
            throw new ArgumentException("The active audit integrity key ID must exist in the key ring.", nameof(currentKeyId));

        _keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (keyId, key) in keys)
        {
            if (string.IsNullOrWhiteSpace(keyId) || keyId.Length > 64
                || keyId.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
                throw new ArgumentException("Audit integrity key IDs may contain only letters, digits, period, underscore, and hyphen.", nameof(keys));
            if (key.Length < 32) throw new ArgumentException($"Audit integrity key '{keyId}' must contain at least 32 bytes.", nameof(keys));
            _keys.Add(keyId, key.ToArray());
        }

        CurrentKeyId = currentKeyId;
        if (currentKey.Length < 32) throw new ArgumentException("The active audit integrity key must contain at least 32 bytes.", nameof(keys));
    }

    public string CurrentKeyId { get; }

    internal ReadOnlySpan<byte> GetKey(string keyId)
        => _keys.TryGetValue(keyId, out var key)
            ? key
            : throw new InvalidDataException($"Audit event references unavailable integrity key ID '{keyId}'.");

    public byte[] CopyCurrentKey() => _keys[CurrentKeyId].ToArray();

    public static AuditIntegrityKeyRing? FromConfiguration(IConfiguration configuration)
    {
        var decoded = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var legacy = TryDecode(configuration["AuditRetention:IntegrityKey"]);
        if (legacy is not null) decoded.Add("legacy", legacy);

        foreach (var item in configuration.GetSection("AuditRetention:IntegrityKeys").GetChildren())
        {
            var key = TryDecode(item.Value);
            if (key is null) continue;
            if (!decoded.TryAdd(item.Key, key))
            {
                CryptographicOperations.ZeroMemory(key);
                foreach (var value in decoded.Values) CryptographicOperations.ZeroMemory(value);
                throw new InvalidOperationException($"Audit integrity key ID '{item.Key}' is configured more than once.");
            }
        }

        if (decoded.Count == 0) return null;
        var currentKeyId = configuration["AuditRetention:IntegrityKeyId"]
            ?? (decoded.ContainsKey("legacy") ? "legacy" : decoded.Keys.SingleOrDefault());
        if (string.IsNullOrWhiteSpace(currentKeyId) || !decoded.ContainsKey(currentKeyId))
        {
            foreach (var value in decoded.Values) CryptographicOperations.ZeroMemory(value);
            throw new InvalidOperationException("AuditRetention:IntegrityKeyId must identify a configured audit integrity key.");
        }

        try { return new AuditIntegrityKeyRing(currentKeyId, decoded); }
        finally
        {
            foreach (var value in decoded.Values) CryptographicOperations.ZeroMemory(value);
        }
    }

    private static byte[]? TryDecode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length >= 32) return bytes;
            CryptographicOperations.ZeroMemory(bytes);
            return null;
        }
        catch (FormatException) { return null; }
    }

    public void Dispose()
    {
        foreach (var key in _keys.Values) CryptographicOperations.ZeroMemory(key);
        _keys.Clear();
    }
}
