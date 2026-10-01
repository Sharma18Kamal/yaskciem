using CloudEntitle.Desktop.Services;
using CloudEntitle.Desktop.ViewModels;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Analytics.Models;
using CloudEntitle.Analytics.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CloudEntitle.Desktop.Tests;

public sealed class NavigationTests
{
    [Fact]
    public void NavigationService_ShouldReuseViewModelsAndNavigateToEveryScreen()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDemoDataService, NoopDemoDataService>();
        services.AddSingleton<IAnalyticsService, NoopAnalyticsService>();
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<CloudAccountsViewModel>();
        services.AddTransient<IdentitiesViewModel>();
        services.AddTransient<ResourcesViewModel>();
        services.AddTransient<EntitlementsViewModel>();
        services.AddTransient<EffectiveAccessViewModel>();
        services.AddTransient<RiskFindingsViewModel>();
        services.AddTransient<RecommendationsViewModel>();
        services.AddTransient<ScanHistoryViewModel>();
        services.AddTransient<SettingsViewModel>();
        using var provider = services.BuildServiceProvider();
        var navigation = new NavigationService(provider);

        navigation.NavigateTo<CloudAccountsViewModel>();
        var first = navigation.CurrentViewModel;
        navigation.NavigateTo<IdentitiesViewModel>();
        navigation.NavigateTo<CloudAccountsViewModel>();

        Assert.IsType<CloudAccountsViewModel>(navigation.CurrentViewModel);
        Assert.Same(first, navigation.CurrentViewModel);
        Assert.Equal("Cloud Accounts", ((WorkspaceViewModel)navigation.CurrentViewModel).Title);
    }

    [Fact]
    public void MainWindowViewModel_ShouldExposeCompleteNavigationSurface()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDemoDataService, NoopDemoDataService>();
        services.AddSingleton<IAnalyticsService, NoopAnalyticsService>();
        services.AddTransient<DashboardViewModel>();
        services.AddSingleton<INavigationService, NavigationService>();
        using var provider = services.BuildServiceProvider();
        var navigation = provider.GetRequiredService<INavigationService>();
        var viewModel = new MainWindowViewModel(navigation);

        Assert.Equal(10, viewModel.NavigationItems.Count);
        Assert.Equal("Dashboard", viewModel.CurrentNavigationItem.Title);
        Assert.Contains(viewModel.NavigationItems, item => item.Title == "Effective Access");
        Assert.Equal("YASK-CIEM", viewModel.ApplicationName);
        Assert.Equal("Cloud Accounts", viewModel.CloudAccountsNavigationItem.Title);
        Assert.Equal("0.1.0", viewModel.ApplicationVersion);
    }

    [Fact]
    public void CloudAccountsViewModel_ShouldExposeConnectActionsForAllProviders()
    {
        var launcher = new RecordingCloudAuthenticationLauncher();
        var viewModel = new CloudAccountsViewModel(new NoopDemoDataService(), launcher);

        Assert.Equal(new[] { "Azure", "AWS", "Google Cloud" }, viewModel.Providers.Select(provider => provider.Name));

        foreach (var provider in viewModel.Providers)
        {
            provider.ConnectCommand.Execute(null);
        }

        Assert.Equal(new[] { CloudProviderType.Azure, CloudProviderType.Aws, CloudProviderType.Gcp }, launcher.LaunchedProviders);
        Assert.Contains("YASK-CIEM does not receive or store", viewModel.ConnectionGuidance);
    }

    [Fact]
    public void CloudAccountsViewModel_ShouldRequireConfirmationBeforeDisconnecting()
    {
        var launcher = new RecordingCloudAuthenticationLauncher();
        var dialogs = new RecordingDialogService(confirmationResult: false);
        var viewModel = new CloudAccountsViewModel(new NoopDemoDataService(), launcher, dialogs);

        viewModel.Providers.Single(provider => provider.Name == "AWS").DisconnectCommand.Execute(null);

        Assert.Empty(launcher.DisconnectedProviders);
        Assert.Contains(dialogs.Confirmations, message => message.Contains("shared CLI session/cache"));
    }

    [Fact]
    public void CloudAccountsViewModel_ShouldLaunchDisconnectAfterConfirmation()
    {
        var launcher = new RecordingCloudAuthenticationLauncher();
        var dialogs = new RecordingDialogService(confirmationResult: true);
        var viewModel = new CloudAccountsViewModel(new NoopDemoDataService(), launcher, dialogs);

        viewModel.Providers.Single(provider => provider.Name == "Google Cloud").DisconnectCommand.Execute(null);

        Assert.Equal(new[] { CloudProviderType.Gcp }, launcher.DisconnectedProviders);
        Assert.Contains("sign-out command was opened", viewModel.ConnectionGuidance);
    }

    private sealed class NoopDemoDataService : IDemoDataService
    {
        public event EventHandler? DataChanged { add { } remove { } }
        public bool IsDemoMode => false;
        public Task<DemoStatistics> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DemoStatistics(0, 0, 0, 0, 0, 0, 0, null));
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DemoSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DemoSnapshot(Array.Empty<CloudAccount>(), Array.Empty<CloudPrincipal>(), Array.Empty<CloudResource>(), Array.Empty<Entitlement>(), Array.Empty<EffectiveAccess>(), Array.Empty<RiskFindingModel>(), Array.Empty<RecommendationModel>(), Array.Empty<Scan>()));
    }

    private sealed class NoopAnalyticsService : IAnalyticsService
    {
        public AnalyticsSnapshot Calculate(DemoSnapshot snapshot, DateTime? asOf = null) => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, Array.Empty<AnalyticsMetric>(), Array.Empty<AnalyticsMetric>(), Array.Empty<AnalyticsMetric>(), Array.Empty<AnalyticsMetric>(), Array.Empty<AnalyticsMetric>());
    }

    private sealed class RecordingCloudAuthenticationLauncher : ICloudAuthenticationLauncher
    {
        public List<CloudProviderType> LaunchedProviders { get; } = new();
        public List<CloudProviderType> DisconnectedProviders { get; } = new();
        public void LaunchAuthentication(CloudProviderType provider) => LaunchedProviders.Add(provider);
        public void LaunchDisconnection(CloudProviderType provider) => DisconnectedProviders.Add(provider);
    }

    private sealed class RecordingDialogService(bool confirmationResult) : IDialogService
    {
        public List<string> Confirmations { get; } = new();
        public void ShowInformation(string message, string title = "YASK-CIEM") { }
        public void ShowError(string message, string title = "YASK-CIEM") { }
        public bool Confirm(string message, string title = "YASK-CIEM")
        {
            Confirmations.Add(message);
            return confirmationResult;
        }
    }
}
