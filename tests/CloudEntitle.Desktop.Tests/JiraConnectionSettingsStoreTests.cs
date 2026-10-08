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
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
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

    private sealed class MemoryJiraSettingsStore(string baseUrl) : IJiraConnectionSettingsStore
    {
        public JiraConnectionSettings Settings { get; private set; } = new(baseUrl);
        public JiraConnectionSettings Load() => Settings;
        public void Save(string value) => Settings = new JiraConnectionSettings(value);
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
