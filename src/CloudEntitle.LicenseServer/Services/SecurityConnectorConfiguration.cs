using CloudEntitle.Integration.Contracts;

namespace CloudEntitle.LicenseServer.Services;

public static class SecurityConnectorConfiguration
{
    public static string Status(IConfiguration section, string kind)
    {
        if (ConnectorConfiguration.Kind(section) != kind) return "Invalid";
        if (string.IsNullOrWhiteSpace(section["Url"])) return "Unconfigured";
        if (!Uri.TryCreate(section["Url"], UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)) return "Invalid";

        var valid = kind switch
        {
            SecurityConnectorCatalog.Splunk => uri.Scheme == Uri.UriSchemeHttps
                && uri.AbsolutePath == "/services/collector/event"
                && IsToken(section["Token"])
                && !string.IsNullOrWhiteSpace(section["Index"]),
            SecurityConnectorCatalog.Sentinel => uri.Scheme == Uri.UriSchemeHttps
                && uri.IsDefaultPort && uri.AbsolutePath == "/"
                && uri.Host.EndsWith(".ingest.monitor.azure.com", StringComparison.OrdinalIgnoreCase)
                && section["RuleId"] is { } rule && rule.StartsWith("dcr-", StringComparison.Ordinal)
                && IsIdentifier(rule, 128)
                && section["Stream"] is { } stream && stream.StartsWith("Custom-", StringComparison.Ordinal)
                && IsIdentifier(stream, 128)
                && (string.IsNullOrEmpty(section["ManagedIdentityClientId"])
                    || Guid.TryParse(section["ManagedIdentityClientId"], out _)),
            SecurityConnectorCatalog.QRadar => uri.Scheme == "tls" && uri.Port is > 0 and <= 65535
                && uri.AbsolutePath is "" or "/"
                && section["LogSourceIdentifier"] is { Length: > 0 and <= 255 } host
                && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
                && section["ClientCertificatePath"] is { } path && !string.IsNullOrWhiteSpace(path)
                && Path.IsPathFullyQualified(path),
            _ => false
        };
        return valid ? "Configured" : "Invalid";
    }

    private static bool IsIdentifier(string value, int maximumLength)
        => value.Length is > 0 && value.Length <= maximumLength
            && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static bool IsToken(string? token)
        => !string.IsNullOrWhiteSpace(token) && token.All(c => c is >= '!' and <= '~');
}
