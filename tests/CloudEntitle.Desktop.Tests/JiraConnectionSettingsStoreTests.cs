using System.IO;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Desktop.ViewModels;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using Xunit;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;

namespace CloudEntitle.Desktop.Tests;

public sealed class JiraConnectionSettingsStoreTests
{
    [Theory]
    [InlineData("https://acme.atlassian.net/", "https://acme.atlassian.net")]
    [InlineData("https://jira.example.com/jira/", "https://jira.example.com/jira")]
    public void NormalizeBaseUrl_ShouldCanonicalizeJiraSiteUrl(string value, string expected)
        => Assert.Equal(expected, LocalJiraConnectionSettingsStore.NormalizeBaseUrl(value));

    [Theory]
    [InlineData("http://jira.example.com")]
    [InlineData("https://user:token@jira.example.com")]
    [InlineData("https://jira.example.com?token=secret")]
    [InlineData("https://jira.example.com/#settings")]
    public void NormalizeBaseUrl_ShouldRejectUnsafeOrAmbiguousUrls(string value)
        => Assert.Throws<ArgumentException>(() => LocalJiraConnectionSettingsStore.NormalizeBaseUrl(value));

    [Fact]
    public void SaveAndLoad_ShouldPersistNormalizedUrlWithoutCredentials()
    {
        var directory = Path.Combine(Path.GetTempPath(), "YaskCiemJiraSettingsTests", Guid.NewGuid().ToString("N"));
        var filePath = Path.Combine(directory, "jira.json");
        try
        {
            var store = new LocalJiraConnectionSettingsStore(filePath);
            store.Save(" https://acme.atlassian.net/ ");

            Assert.Equal("https://acme.atlassian.net", store.Load().BaseUrl);
            Assert.DoesNotContain("token", File.ReadAllText(filePath), StringComparison.OrdinalIgnoreCase);

            store.Save("https://new-acme.atlassian.net/");
            Assert.Equal("https://new-acme.atlassian.net", store.Load().BaseUrl);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SaveAndLoad_ShouldPersistBothTicketingUrlsAndSelectedProvider()
    {
        var directory = Path.Combine(Path.GetTempPath(), "YaskCiemTicketingSettingsTests", Guid.NewGuid().ToString("N"));
        var filePath = Path.Combine(directory, "ticketing.json");
        try
        {
            var store = new LocalJiraConnectionSettingsStore(filePath);
            store.Save(new JiraConnectionSettings("https://acme.atlassian.net/", "https://acme.service-now.com/", TicketingProvider.ServiceNow,
                new Dictionary<TicketingProvider, string>
                {
                    [TicketingProvider.BmcHelixItsm] = "https://bmc.example.com/helix/",
                    [TicketingProvider.Zendesk] = "https://acme.zendesk.com/"
                }));

            var loaded = store.Load();
            Assert.Equal("https://acme.atlassian.net", loaded.BaseUrl);
            Assert.Equal("https://acme.service-now.com", loaded.ServiceNowBaseUrl);
            Assert.Equal(TicketingProvider.ServiceNow, loaded.SelectedProvider);
            Assert.Equal("https://bmc.example.com/helix", loaded.GetBaseUrl(TicketingProvider.BmcHelixItsm));
            Assert.Equal("https://acme.zendesk.com", loaded.GetBaseUrl(TicketingProvider.Zendesk));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_ShouldMigrateLegacyJiraOnlySettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "YaskCiemLegacyJiraSettingsTests", Guid.NewGuid().ToString("N"));
        var filePath = Path.Combine(directory, "jira.json");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(filePath, "{\"BaseUrl\":\"https://acme.atlassian.net/\"}");

            var settings = new LocalJiraConnectionSettingsStore(filePath).Load();

            Assert.Equal("https://acme.atlassian.net", settings.BaseUrl);
            Assert.Equal(string.Empty, settings.ServiceNowBaseUrl);
            Assert.Equal(TicketingProvider.Jira, settings.SelectedProvider);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(TicketingProvider.Jira, "https://acme.atlassian.net/secure/CreateIssue!default.jspa")]
    [InlineData(TicketingProvider.ServiceNow, "https://acme.service-now.com/now/nav/ui/classic/params/target/incident.do?sys_id=-1")]
    [InlineData(TicketingProvider.JiraServiceManagement, "https://acme.atlassian.net/servicedesk/customer/portals")]
    [InlineData(TicketingProvider.Zendesk, "https://acme.zendesk.com/agent/tickets/new")]
    [InlineData(TicketingProvider.AzureDevOpsBoards, "https://dev.azure.com/acme/project/_workitems/create/Issue")]
    [InlineData(TicketingProvider.BmcHelixItsm, "https://tickets.example.com")]
    [InlineData(TicketingProvider.ManageEngineServiceDeskPlus, "https://tickets.example.com")]
    [InlineData(TicketingProvider.IvantiNeuronsItsm, "https://tickets.example.com")]
    [InlineData(TicketingProvider.SolarWindsServiceDesk, "https://tickets.example.com")]
    [InlineData(TicketingProvider.Dynamics365CustomerService, "https://tickets.example.com")]
    [InlineData(TicketingProvider.Other, "https://tickets.example.com")]
    public void BuildRequestFormUrl_ShouldReturnTheSelectedSystemRequestForm(TicketingProvider provider, string expected)
    {
        var baseUrl = provider switch
        {
            TicketingProvider.Jira or TicketingProvider.JiraServiceManagement => "https://acme.atlassian.net",
            TicketingProvider.ServiceNow => "https://acme.service-now.com",
            TicketingProvider.Zendesk => "https://acme.zendesk.com",
            TicketingProvider.AzureDevOpsBoards => "https://dev.azure.com/acme/project",
            _ => "https://tickets.example.com"
        };

        Assert.Equal(expected, LocalJiraConnectionSettingsStore.BuildRequestFormUrl(provider, baseUrl));
    }

    [Fact]
    public async Task SettingsViewModel_ShouldLoadAndSaveOnlyValidJiraUrls()
    {
        var store = new MemoryJiraSettingsStore("https://acme.atlassian.net");
        var viewModel = new SettingsViewModel(new EmptyDemoDataService(), jiraSettingsStore: store);

        Assert.Equal("https://acme.atlassian.net", viewModel.JiraBaseUrl);
        Assert.True(viewModel.CanConnectToJira);

        viewModel.JiraBaseUrl = "http://jira.example.com";
        Assert.False(viewModel.CanConnectToJira);
        await viewModel.SaveJiraConnectionAsync();
        Assert.Equal("https://acme.atlassian.net", store.Settings.BaseUrl);
        Assert.Contains("HTTPS", viewModel.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        viewModel.JiraBaseUrl = "https://jira.example.com/";
        await viewModel.SaveJiraConnectionAsync();
        Assert.Equal("https://jira.example.com", store.Settings.BaseUrl);
        Assert.Equal(store.Settings.BaseUrl, viewModel.JiraBaseUrl);
        Assert.True(viewModel.CanConnectToJira);
    }

    [Fact]
    public async Task SettingsViewModel_ShouldStoreOnlyAbsoluteHttpsPostureWebhookUrls()
    {
        var credentials = new MemoryPostureWebhookCredentialStore();
        var viewModel = new SettingsViewModel(
            new EmptyDemoDataService(),
            jiraSettingsStore: new MemoryJiraSettingsStore(string.Empty),
            postureWebhookCredentialStore: credentials);

        await viewModel.SavePostureWebhookCredentialAsync("http://hooks.example.com/secret");
        Assert.Null(credentials.Url);
        Assert.Contains("HTTPS", viewModel.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        await viewModel.SavePostureWebhookCredentialAsync("https://user:secret@hooks.example.com/secret");
        Assert.Null(credentials.Url);
        Assert.Contains("username or password", viewModel.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        await viewModel.SavePostureWebhookCredentialAsync("https://hooks.example.com/secret?token=value");
        Assert.Equal("https://hooks.example.com/secret?token=value", credentials.Url);
        Assert.DoesNotContain("secret", viewModel.PostureWebhookStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SettingsViewModel_ShouldSwitchBetweenJiraAndServiceNowRequestForms()
    {
        var store = new MemoryJiraSettingsStore(new JiraConnectionSettings("https://acme.atlassian.net", "https://acme.service-now.com", TicketingProvider.Jira));
        var viewModel = new SettingsViewModel(new EmptyDemoDataService(), jiraSettingsStore: store);

        viewModel.SelectedTicketingProvider = TicketingProvider.ServiceNow;
        Assert.Equal("https://acme.service-now.com", viewModel.TicketingSiteUrl);
        Assert.True(viewModel.CanOpenTicketingRequest);
        Assert.Equal("Open ServiceNow incident form", viewModel.TicketingRequestButtonText);
        Assert.Equal("https://acme.service-now.com/now/nav/ui/classic/params/target/incident.do?sys_id=-1",
            LocalJiraConnectionSettingsStore.BuildRequestFormUrl(viewModel.SelectedTicketingProvider, viewModel.TicketingSiteUrl));

        viewModel.TicketingSiteUrl = "https://servicenow.example.com/";
        await viewModel.SaveTicketingConnectionAsync();

        Assert.Equal("https://acme.atlassian.net", store.Settings.BaseUrl);
        Assert.Equal("https://servicenow.example.com", store.Settings.ServiceNowBaseUrl);
        Assert.Equal(TicketingProvider.ServiceNow, store.Settings.SelectedProvider);
    }

    [Fact]
    public void SettingsViewModel_ShouldListRequestedTicketingToolsAndOther()
    {
        var viewModel = new SettingsViewModel(new EmptyDemoDataService(), jiraSettingsStore: new MemoryJiraSettingsStore(string.Empty));
        var providers = viewModel.TicketingProviders.Select(option => option.DisplayName).ToArray();

        Assert.Contains("ServiceNow ITSM", providers);
        Assert.Contains("Jira Service Management", providers);
        Assert.Contains("BMC Helix ITSM", providers);
        Assert.Contains("ManageEngine ServiceDesk Plus", providers);
        Assert.Contains("Ivanti Neurons for ITSM", providers);
        Assert.Contains("Zendesk", providers);
        Assert.Contains("SolarWinds Service Desk", providers);
        Assert.Contains("Dynamics 365 Customer Service", providers);
        Assert.Contains("Azure DevOps Boards", providers);
        Assert.Contains("Other ticketing portal", providers);
        Assert.Equal(11, providers.Length);
    }

    [Fact]
    public async Task SettingsViewModel_ShouldKeepIndependentUrlsForAdditionalProviders()
    {
        var store = new MemoryJiraSettingsStore(string.Empty);
        var viewModel = new SettingsViewModel(new EmptyDemoDataService(), jiraSettingsStore: store);

        viewModel.SelectedTicketingProvider = TicketingProvider.BmcHelixItsm;
        viewModel.TicketingSiteUrl = "https://bmc.example.com/helix";
        await viewModel.SaveTicketingConnectionAsync();
        viewModel.SelectedTicketingProvider = TicketingProvider.Zendesk;
        viewModel.TicketingSiteUrl = "https://acme.zendesk.com";
        await viewModel.SaveTicketingConnectionAsync();

        Assert.Equal("https://bmc.example.com/helix", store.Settings.GetBaseUrl(TicketingProvider.BmcHelixItsm));
        Assert.Equal("https://acme.zendesk.com", store.Settings.GetBaseUrl(TicketingProvider.Zendesk));
        Assert.Equal(TicketingProvider.Zendesk, store.Settings.SelectedProvider);
    }

    private sealed class MemoryPostureWebhookCredentialStore : IPostureWebhookCredentialStore
    {
        public string? Url { get; private set; }
        public string? ReadUrl() => Url;
        public void SaveUrl(string url) => Url = url;
        public void Delete() => Url = null;
    }

    private sealed class MemoryJiraSettingsStore(string baseUrl) : IJiraConnectionSettingsStore
    {
        public MemoryJiraSettingsStore(JiraConnectionSettings settings) : this(settings.BaseUrl) => Settings = settings;
        public JiraConnectionSettings Settings { get; private set; } = new(baseUrl);
        public JiraConnectionSettings Load() => Settings;
        public void Save(string value) => Settings = new JiraConnectionSettings(value);
        public void Save(JiraConnectionSettings settings) => Settings = settings;
    }

    private sealed class EmptyDemoDataService : IDemoDataService
    {
        public event EventHandler? DataChanged { add { } remove { } }
        public bool IsDemoMode => false;
        public Task<DemoStatistics> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DemoStatistics(0, 0, 0, 0, 0, 0, 0, null));
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DemoSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DemoSnapshot(Array.Empty<CloudAccount>(), Array.Empty<CloudPrincipal>(), Array.Empty<CloudResource>(),
                Array.Empty<Entitlement>(), Array.Empty<DomainEffectiveAccess>(), Array.Empty<RiskFindingModel>(), Array.Empty<RecommendationModel>(), Array.Empty<Scan>()));
    }
}
