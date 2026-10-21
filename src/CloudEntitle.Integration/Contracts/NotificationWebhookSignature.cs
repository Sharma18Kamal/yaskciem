using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CloudEntitle.Integration.Contracts;

public static class NotificationWebhookSignature
{
    public static string Sign(ReadOnlySpan<byte> body, Guid eventId, string timestamp, string signingKey)
    {
        if (Encoding.UTF8.GetByteCount(signingKey) < 32)
            throw new ArgumentException("Webhook signing keys must contain at least 32 UTF-8 bytes.", nameof(signingKey));
        var prefix = Encoding.UTF8.GetBytes($"{timestamp}.{eventId:D}.");
        var signedBytes = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signedBytes, 0);
        body.CopyTo(signedBytes.AsSpan(prefix.Length));
        return $"v1={Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingKey), signedBytes)).ToLowerInvariant()}";
    }

    public static bool Verify(ReadOnlySpan<byte> body, Guid eventId, string timestamp, string signature,
        string signingKey, DateTimeOffset now)
    {
        if (eventId == Guid.Empty || signature is null || signature.Length != 67 || string.IsNullOrEmpty(signingKey)
            || Encoding.UTF8.GetByteCount(signingKey) < 32
            || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds < now.ToUnixTimeSeconds() - 300 || seconds > now.ToUnixTimeSeconds() + 30)
            return false;
        var expected = Sign(body, eventId, timestamp, signingKey);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature));
    }
}
