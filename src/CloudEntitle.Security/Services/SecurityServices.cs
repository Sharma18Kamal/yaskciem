using System.Text.RegularExpressions;

namespace CloudEntitle.Security.Services;

public static partial class SensitiveDataRedactor
{
    private static readonly string[] SensitiveKeys = ["password", "token", "secret", "private", "credential", "authorization", "api-key", "apikey", "access-key", "refresh"];

    public static bool IsSensitiveKey(string key) => SensitiveKeys.Any(item => key.Contains(item, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyDictionary<string, string> RedactMetadata(IReadOnlyDictionary<string, string>? metadata)
        => (metadata ?? new Dictionary<string, string>()).ToDictionary(item => item.Key, item => IsSensitiveKey(item.Key) ? "[REDACTED]" : item.Value);

    public static string RedactText(string value)
    {
        var redacted = value;
        redacted = BearerTokenRegex().Replace(redacted, "$1[REDACTED]");
        redacted = SecretAssignmentRegex().Replace(redacted, "$1[REDACTED]");
        return redacted;
    }

    [GeneratedRegex("(?i)(bearer\\s+)[^\\s,;]+")]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex("(?i)(password|token|secret|api[-_]?key|private[-_]?key)\\s*[:=]\\s*([^\\s,;]+)")]
    private static partial Regex SecretAssignmentRegex();
}

public sealed record SecurityConfiguration(bool RequireHttps, bool UseHsts, bool RedactSensitiveLogs, int MaxRequestBodyBytes)
{
    public static SecurityConfiguration Production => new(true, true, true, 1024 * 1024);
    public static SecurityConfiguration Development => new(false, false, true, 1024 * 1024);
}

public static class SecurityHeaderPolicy
{
    public static void Apply(Microsoft.AspNetCore.Http.HttpResponse response, bool includeHsts)
    {
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["X-Frame-Options"] = "DENY";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
        if (includeHsts) response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    }
}
