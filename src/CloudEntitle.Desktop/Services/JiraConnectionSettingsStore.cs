using System.IO;
using System.Text.Json;

namespace CloudEntitle.Desktop.Services;

public enum TicketingProvider
{
    Jira,
    ServiceNow,
    JiraServiceManagement,
    BmcHelixItsm,
    ManageEngineServiceDeskPlus,
    IvantiNeuronsItsm,
    Zendesk,
    SolarWindsServiceDesk,
    Dynamics365CustomerService,
    AzureDevOpsBoards,
    Other
}

public sealed record TicketingProviderOption(TicketingProvider Provider, string DisplayName);
public sealed record TicketingProviderConfiguration(string UserName = "", string ProjectKey = "", string RequestType = "", string WebhookUrl = "");

public sealed record JiraConnectionSettings(
    string BaseUrl,
    string ServiceNowBaseUrl = "",
    TicketingProvider SelectedProvider = TicketingProvider.Jira,
    IReadOnlyDictionary<TicketingProvider, string>? ProviderUrls = null,
    IReadOnlyDictionary<TicketingProvider, TicketingProviderConfiguration>? ProviderConfigurations = null)
{
    public string GetBaseUrl(TicketingProvider provider)
    {
        if (ProviderUrls is not null && ProviderUrls.TryGetValue(provider, out var value)) return value;
        return provider switch
        {
            TicketingProvider.Jira => BaseUrl,
            TicketingProvider.ServiceNow => ServiceNowBaseUrl,
            _ => string.Empty
        };
    }
}

public interface IJiraConnectionSettingsStore
{
    JiraConnectionSettings Load();
    void Save(string baseUrl);
    void Save(JiraConnectionSettings settings);
}

public sealed class LocalJiraConnectionSettingsStore : IJiraConnectionSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _filePath;
    private readonly string? _legacyFilePath;

    public LocalJiraConnectionSettingsStore(string? filePath = null)
    {
        if (filePath is null)
        {
            var settingsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YASK-CIEM");
            _filePath = Path.Combine(settingsDirectory, "ticketing-connections.json");
            _legacyFilePath = Path.Combine(settingsDirectory, "jira-connection.json");
        }
        else
        {
            _filePath = filePath;
        }
    }

    public JiraConnectionSettings Load()
    {
        var sourcePath = File.Exists(_filePath) ? _filePath : _legacyFilePath;
        if (sourcePath is null || !File.Exists(sourcePath)) return new JiraConnectionSettings(string.Empty);
        try
        {
            var settings = JsonSerializer.Deserialize<JiraConnectionSettings>(File.ReadAllText(sourcePath), JsonOptions)
                ?? new JiraConnectionSettings(string.Empty);
            var provider = Enum.IsDefined(settings.SelectedProvider) ? settings.SelectedProvider : TicketingProvider.Jira;
            var urls = settings.ProviderUrls is null
                ? new Dictionary<TicketingProvider, string>()
                : settings.ProviderUrls.Where(pair => Enum.IsDefined(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => NormalizeOptionalBaseUrl(pair.Value));
            if (!urls.ContainsKey(TicketingProvider.Jira) && !string.IsNullOrWhiteSpace(settings.BaseUrl))
                urls[TicketingProvider.Jira] = NormalizeBaseUrl(settings.BaseUrl);
            if (!urls.ContainsKey(TicketingProvider.ServiceNow) && !string.IsNullOrWhiteSpace(settings.ServiceNowBaseUrl))
                urls[TicketingProvider.ServiceNow] = NormalizeBaseUrl(settings.ServiceNowBaseUrl);
            var providerConfigurations = NormalizeProviderConfigurations(settings.ProviderConfigurations);

            return settings with
            {
                BaseUrl = urls.GetValueOrDefault(TicketingProvider.Jira, string.Empty),
                ServiceNowBaseUrl = urls.GetValueOrDefault(TicketingProvider.ServiceNow, string.Empty),
                SelectedProvider = provider,
                ProviderUrls = urls,
                ProviderConfigurations = providerConfigurations
            };
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The local ticketing connection settings are corrupted and were not loaded.", exception);
        }
    }

    public void Save(string baseUrl)
    {
        var settings = Load();
        var normalizedUrl = NormalizeOptionalBaseUrl(baseUrl);
        var urls = settings.ProviderUrls?.ToDictionary(pair => pair.Key, pair => pair.Value)
            ?? new Dictionary<TicketingProvider, string>();
        if (normalizedUrl.Length == 0) urls.Remove(TicketingProvider.Jira);
        else urls[TicketingProvider.Jira] = normalizedUrl;
        Save(settings with { BaseUrl = normalizedUrl, ProviderUrls = urls });
    }

    public void Save(JiraConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var urls = settings.ProviderUrls is null
            ? new Dictionary<TicketingProvider, string>()
            : settings.ProviderUrls.Where(pair => Enum.IsDefined(pair.Key))
                .ToDictionary(pair => pair.Key, pair => NormalizeOptionalBaseUrl(pair.Value));
        if (!urls.ContainsKey(TicketingProvider.Jira) && !string.IsNullOrWhiteSpace(settings.BaseUrl))
            urls[TicketingProvider.Jira] = NormalizeBaseUrl(settings.BaseUrl);
        if (!urls.ContainsKey(TicketingProvider.ServiceNow) && !string.IsNullOrWhiteSpace(settings.ServiceNowBaseUrl))
            urls[TicketingProvider.ServiceNow] = NormalizeBaseUrl(settings.ServiceNowBaseUrl);

        var normalized = settings with
        {
            BaseUrl = urls.GetValueOrDefault(TicketingProvider.Jira, string.Empty),
            ServiceNowBaseUrl = urls.GetValueOrDefault(TicketingProvider.ServiceNow, string.Empty),
            SelectedProvider = Enum.IsDefined(settings.SelectedProvider) ? settings.SelectedProvider : TicketingProvider.Jira,
            ProviderUrls = urls,
            ProviderConfigurations = NormalizeProviderConfigurations(settings.ProviderConfigurations)
        };
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(normalized, JsonOptions));
        File.Move(temporaryPath, _filePath, overwrite: true);
    }

    public static string BuildRequestFormUrl(TicketingProvider provider, string baseUrl)
    {
        var normalized = NormalizeBaseUrl(baseUrl);
        var siteRoot = new Uri(normalized.TrimEnd('/') + "/", UriKind.Absolute);
        var relativePath = provider switch
        {
            TicketingProvider.Jira => "secure/CreateIssue!default.jspa",
            TicketingProvider.ServiceNow => "now/nav/ui/classic/params/target/incident.do?sys_id=-1",
            TicketingProvider.JiraServiceManagement => "servicedesk/customer/portals",
            TicketingProvider.Zendesk => "agent/tickets/new",
            TicketingProvider.AzureDevOpsBoards => "_workitems/create/Issue",
            _ => string.Empty
        };
        return relativePath.Length == 0 ? normalized : new Uri(siteRoot, relativePath).AbsoluteUri;
    }

    private static string NormalizeOptionalBaseUrl(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : NormalizeBaseUrl(value);

    private static IReadOnlyDictionary<TicketingProvider, TicketingProviderConfiguration> NormalizeProviderConfigurations(
        IReadOnlyDictionary<TicketingProvider, TicketingProviderConfiguration>? configurations)
    {
        if (configurations is null) return new Dictionary<TicketingProvider, TicketingProviderConfiguration>();
        return configurations.Where(pair => Enum.IsDefined(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value with
        {
            UserName = pair.Value.UserName?.Trim() ?? string.Empty,
            ProjectKey = pair.Value.ProjectKey?.Trim() ?? string.Empty,
            RequestType = pair.Value.RequestType?.Trim() ?? string.Empty,
            WebhookUrl = NormalizeOptionalBaseUrl(pair.Value.WebhookUrl)
        });
    }

    public static string NormalizeBaseUrl(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Enter an HTTPS ticketing site URL without credentials, query parameters, or a fragment.", nameof(value));

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }
}
