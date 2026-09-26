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
        Assert.Equal("0.1.0", viewModel.ApplicationVersion);
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
}
