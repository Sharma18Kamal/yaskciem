using System.IO;
using System.Text.Json;

namespace CloudEntitle.Desktop.Services;

public sealed record JiraConnectionSettings(string BaseUrl);

public interface IJiraConnectionSettingsStore
{
    JiraConnectionSettings Load();
    void Save(string baseUrl);
}

public sealed class LocalJiraConnectionSettingsStore : IJiraConnectionSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _filePath;

    public LocalJiraConnectionSettingsStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YASK-CIEM", "jira-connection.json");
    }

    public JiraConnectionSettings Load()
    {
        if (!File.Exists(_filePath)) return new JiraConnectionSettings(string.Empty);
        try
        {
            var settings = JsonSerializer.Deserialize<JiraConnectionSettings>(File.ReadAllText(_filePath), JsonOptions)
                ?? new JiraConnectionSettings(string.Empty);
            return new JiraConnectionSettings(string.IsNullOrWhiteSpace(settings.BaseUrl)
                ? string.Empty
                : NormalizeBaseUrl(settings.BaseUrl));
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The local Jira connection settings are corrupted and were not loaded.", exception);
        }
    }

    public void Save(string baseUrl)
    {
        var normalized = string.IsNullOrWhiteSpace(baseUrl) ? string.Empty : NormalizeBaseUrl(baseUrl);
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new JiraConnectionSettings(normalized), JsonOptions));
        File.Move(temporaryPath, _filePath, overwrite: true);
    }

    public static string NormalizeBaseUrl(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Enter an HTTPS Jira site URL without credentials, query parameters, or a fragment.", nameof(value));

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }
}
