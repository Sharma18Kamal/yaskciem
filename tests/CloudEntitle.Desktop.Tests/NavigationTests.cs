using System.IO;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Desktop.ViewModels;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Domain.ValueObjects;
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
        services.AddTransient<FeedbackViewModel>();
        services.AddTransient<SettingsViewModel>();
        using var provider = services.BuildServiceProvider();
        var navigation = new NavigationService(provider);

        navigation.NavigateTo<CloudAccountsViewModel>();
        var first = navigation.CurrentViewModel;
        navigation.NavigateTo<IdentitiesViewModel>();
        var mainWindow = new MainWindowViewModel(navigation);
        Assert.Equal("Identities", mainWindow.CurrentNavigationItem.Title);
        Assert.True(mainWindow.CurrentNavigationItem.IsSelected);
        Assert.False(mainWindow.NavigationItems.Single(item => item.Title == "Dashboard").IsSelected);
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

        Assert.Equal(11, viewModel.NavigationItems.Count);
        Assert.Equal("Dashboard", viewModel.CurrentNavigationItem.Title);
        Assert.Contains(viewModel.NavigationItems, item => item.Title == "Effective Access");
        Assert.Equal("YASK-CIEM", viewModel.ApplicationName);
        Assert.Equal("Cloud Accounts", viewModel.CloudAccountsNavigationItem.Title);
        Assert.Equal("0.1.0", viewModel.ApplicationVersion);
    }

    [Fact]
    public async Task MainWindowViewModel_ShouldStartDeviceTrialWithoutRequestingEmail()
    {
        var trialService = new RecordingTrialService(existingTrial: null, newTrial: new TrialInfo
        {
            TrialStart = DateTime.UtcNow,
            TrialEnd = DateTime.UtcNow.AddDays(7),
            Status = LicenseStatus.Trial
        });
        var viewModel = new MainWindowViewModel(new TestNavigationService(new CloudAccountsViewModel(new NoopDemoDataService())), trialService: trialService, getDeviceIdentifier: () => "stable-machine-id");

        await viewModel.InitializeTrialAsync();

        Assert.True(viewModel.IsTrialActive);
        Assert.Contains("7 days left", viewModel.TrialStatusText);
        Assert.Equal("YASK-LOCAL-DEVICE-stable-machine-id", trialService.StartedUserId);
        Assert.Equal("stable-machine-id", trialService.StartedDeviceIdentifier);
        Assert.DoesNotContain('@', trialService.StartedUserId!);
    }

    [Fact]
    public async Task MainWindowViewModel_ShouldContinueExistingTrialOnSameDevice()
    {
        var existingTrial = new TrialInfo
        {
            TrialStart = DateTime.UtcNow.AddDays(-4),
            TrialEnd = DateTime.UtcNow.AddDays(3),
            Status = LicenseStatus.Trial
        };
        var trialService = new RecordingTrialService(existingTrial, new TrialInfo());
        var viewModel = new MainWindowViewModel(new TestNavigationService(new CloudAccountsViewModel(new NoopDemoDataService())), trialService: trialService, getDeviceIdentifier: () => "stable-machine-id");

        await viewModel.InitializeTrialAsync();

        Assert.True(viewModel.IsTrialActive);
        Assert.Contains("3 days left", viewModel.TrialStatusText);
        Assert.Null(trialService.StartedUserId);
        Assert.Equal("YASK-LOCAL-DEVICE-stable-machine-id", trialService.CheckedUserId);
        Assert.Equal("stable-machine-id", trialService.CheckedDeviceIdentifier);
    }

    [Fact]
    public async Task MainWindowViewModel_ShouldLockFeaturesWhenTrialExpired()
    {
        var trialService = new RecordingTrialService(new TrialInfo
        {
            TrialStart = DateTime.UtcNow.AddDays(-8),
            TrialEnd = DateTime.UtcNow.AddDays(-1),
            Status = LicenseStatus.Expired
        }, new TrialInfo());
        var viewModel = new MainWindowViewModel(new TestNavigationService(new CloudAccountsViewModel(new NoopDemoDataService())), trialService: trialService, getDeviceIdentifier: () => "stable-machine-id");

        await viewModel.InitializeTrialAsync();

        Assert.False(viewModel.IsTrialActive);
        Assert.Equal("Trial expired", viewModel.TrialStatusText);
        Assert.Contains("options are locked", viewModel.StatusMessage);
    }

    [Fact]
    public async Task MainWindowViewModel_ShouldLockFeaturesWhenTrialExpiresWhileOpen()
    {
        var trialService = new RecordingTrialService(new TrialInfo
        {
            TrialStart = DateTime.UtcNow.AddDays(-6),
            TrialEnd = DateTime.UtcNow.AddDays(1),
            Status = LicenseStatus.Trial
        }, new TrialInfo());
        var viewModel = new MainWindowViewModel(new TestNavigationService(new CloudAccountsViewModel(new NoopDemoDataService())), trialService: trialService, getDeviceIdentifier: () => "stable-machine-id");
        await viewModel.InitializeTrialAsync();
        Assert.True(viewModel.IsTrialActive);

        trialService.ExistingTrial = new TrialInfo
        {
            TrialStart = DateTime.UtcNow.AddDays(-8),
            TrialEnd = DateTime.UtcNow.AddSeconds(-1),
            Status = LicenseStatus.Expired
        };
        await viewModel.RefreshTrialStatusAsync();

        Assert.False(viewModel.IsTrialActive);
        Assert.Equal("Trial expired", viewModel.TrialStatusText);
    }

    [Fact]
    public async Task CloudAccountsViewModel_ShouldDisableOtherProvidersAfterSignIn()
    {
        var launcher = new RecordingCloudAuthenticationLauncher();
        var viewModel = new CloudAccountsViewModel(new NoopDemoDataService(), launcher);

        Assert.Equal(new[] { "Azure", "AWS", "Google Cloud" }, viewModel.Providers.Select(provider => provider.Name));

        await viewModel.ConnectAsync(CloudProviderType.Azure);

        Assert.Equal(new[] { CloudProviderType.Azure }, launcher.LaunchedProviders);
        Assert.True(viewModel.Providers.Single(provider => provider.Provider == CloudProviderType.Azure).IsConnected);
        Assert.False(viewModel.Providers.Single(provider => provider.Provider == CloudProviderType.Aws).CanConnect);
        Assert.False(viewModel.Providers.Single(provider => provider.Provider == CloudProviderType.Gcp).CanConnect);
        Assert.Contains("YASK-CIEM does not receive or store", viewModel.ConnectionGuidance);
    }

    [Fact]
    public async Task CloudAccountsViewModel_ShouldRequireConfirmationBeforeDisconnecting()
    {
        var launcher = new RecordingCloudAuthenticationLauncher();
        var dialogs = new RecordingDialogService(confirmationResult: false);
        var viewModel = new CloudAccountsViewModel(new NoopDemoDataService(), launcher, dialogs);

        await viewModel.DisconnectProviderAsync(CloudProviderType.Aws);

        Assert.Empty(launcher.DisconnectedProviders);
        Assert.Contains(dialogs.Confirmations, message => message.Contains("shared CLI session/cache"));
    }

    [Fact]
    public async Task CloudAccountsViewModel_ShouldDisconnectAfterConfirmation()
    {
        var launcher = new RecordingCloudAuthenticationLauncher();
        var dialogs = new RecordingDialogService(confirmationResult: true);
        var viewModel = new CloudAccountsViewModel(new NoopDemoDataService(), launcher, dialogs);

        await viewModel.DisconnectProviderAsync(CloudProviderType.Gcp);

        Assert.Equal(new[] { CloudProviderType.Gcp }, launcher.DisconnectedProviders);
        Assert.Contains("is disconnected", viewModel.ConnectionGuidance);
    }

    [Fact]
    public void HeaderDisconnectCommand_ShouldSignOutActiveProviderAndUpdateStatus()
    {
        var launcher = new RecordingCloudAuthenticationLauncher();
        launcher.LaunchedProviders.Add(CloudProviderType.Azure);
        var dialogs = new RecordingDialogService(confirmationResult: true);
        var connectionState = new CloudConnectionState();
        var cloudAccounts = new CloudAccountsViewModel(new NoopDemoDataService(), launcher, dialogs, connectionState);
        var navigation = new TestNavigationService(cloudAccounts);
        var viewModel = new MainWindowViewModel(navigation, connectionState);

        viewModel.OpenCloudDisconnectCommand.Execute(null);

        Assert.Equal(new[] { CloudProviderType.Azure }, launcher.DisconnectedProviders);
        Assert.Equal("No active CLI sign-in", viewModel.CloudConnectionStatus);
        Assert.False(viewModel.CanDisconnectCloud);
        Assert.True(cloudAccounts.Providers.All(provider => provider.CanConnect));
    }

    [Fact]
    public async Task IdleTimeout_ShouldSignOutActiveProviderWithoutConfirmation()
    {
        var launcher = new RecordingCloudAuthenticationLauncher();
        launcher.LaunchedProviders.Add(CloudProviderType.Azure);
        var connectionState = new CloudConnectionState();
        connectionState.Update(new[] { CloudProviderType.Azure });
        var navigation = new TestNavigationService(new CloudAccountsViewModel(new NoopDemoDataService(), launcher, cloudConnectionState: connectionState));
        var viewModel = new MainWindowViewModel(navigation, connectionState, launcher);

        await viewModel.DisconnectCloudForInactivityAsync();

        Assert.Equal(new[] { CloudProviderType.Azure }, launcher.DisconnectedProviders);
        Assert.Equal("No active CLI sign-in", viewModel.CloudConnectionStatus);
        Assert.Contains("5 minutes of inactivity", viewModel.StatusMessage);
    }

    [Fact]
    public async Task DashboardViewModel_ShouldDisplayLiveScanResultsAndPartialProviderStatus()
    {
        var azure = new FakeCloudProvider(CloudProviderType.Azure, connected: true);
        var aws = new FakeCloudProvider(CloudProviderType.Aws, connected: false);
        var scanService = new LiveCloudScanService(new ICloudProvider[] { azure, aws });
        var dashboard = new DashboardViewModel(new NoopDemoDataService(), new NoopAnalyticsService(), scanService);

        await dashboard.RunScanAsync();

        Assert.Equal(1, dashboard.CloudAccounts);
        Assert.Equal(2, dashboard.Identities);
        Assert.Equal(1, dashboard.Resources);
        Assert.Equal(1, dashboard.PrivilegedIdentities);
        Assert.Equal(1, dashboard.RiskFindings);
        Assert.Equal(1, dashboard.HighFindings);
        Assert.Contains("Aws: no active CLI sign-in", dashboard.ScanStatusMessage);
        Assert.NotEqual("No scans yet", dashboard.LastScan);
    }

    [Fact]
    public async Task DashboardViewModel_ShouldGenerateReportAtChosenPathAndFormat()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), "selected-yask-report.csv");
        var dialogs = new RecordingDialogService(confirmationResult: true) { ReportPath = outputPath };
        var reports = new RecordingReportService();
        var liveScan = new LiveCloudScanService(new ICloudProvider[] { new FakeCloudProvider(CloudProviderType.Azure, connected: true) });
        await liveScan.ScanAsync();
        var dashboard = new DashboardViewModel(new NoopDemoDataService(), new NoopAnalyticsService(), liveScan, reports, dialogs);

        await dashboard.GenerateReportAsync();

        Assert.Equal("csv", reports.ReportType);
        Assert.Equal(outputPath, reports.OutputPath);
        Assert.Single(reports.CurrentScanFindings!);
        Assert.Contains(dialogs.InformationMessages, message => message.Contains(outputPath));
        Assert.Null(dashboard.ErrorMessage);
    }

    [Fact]
    public async Task FeedbackViewModel_ShouldRequireBothFieldsAndSubmitAnonymously()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var filePath = Path.Combine(directory, "feedback.jsonl");
        var viewModel = new FeedbackViewModel(new LocalUserFeedbackService(filePath));

        Assert.False(viewModel.CanSubmit);
        viewModel.FeedbackText = "The scan summary is helpful.";
        Assert.False(viewModel.CanSubmit);
        viewModel.SuggestionsText = "Add provider filters.";
        Assert.True(viewModel.CanSubmit);

        await viewModel.SubmitAsync();

        Assert.False(viewModel.CanSubmit);
        Assert.Contains("submitted on this device", viewModel.SubmissionMessage);
        var savedSubmission = await File.ReadAllTextAsync(filePath);
        Assert.Contains("The scan summary is helpful.", savedSubmission);
        Assert.Contains("Add provider filters.", savedSubmission);
        Assert.DoesNotContain("username", savedSubmission, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("email", savedSubmission, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("phone", savedSubmission, StringComparison.OrdinalIgnoreCase);

        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task LiveScan_ShouldPopulateSearchableIdentityAndResourceScreens()
    {
        var scanService = new LiveCloudScanService(new ICloudProvider[] { new FakeCloudProvider(CloudProviderType.Azure, connected: true) });
        var identities = new IdentitiesViewModel(new NoopDemoDataService(), scanService);
        var resources = new ResourcesViewModel(new NoopDemoDataService(), scanService);
        var dashboard = new DashboardViewModel(new NoopDemoDataService(), new NoopAnalyticsService(), scanService);

        await dashboard.RunScanAsync();

        Assert.Equal(2, identities.Identities.Count);
        Assert.True(identities.HasResults);
        Assert.Single(resources.Resources);
        Assert.True(resources.HasResults);
        identities.SearchText = "Owner";
        resources.SearchText = "resource-test";
        Assert.Single(identities.Identities);
        Assert.Single(resources.Resources);
        identities.SearchText = "missing";
        resources.SearchText = "missing";
        Assert.False(identities.HasResults);
        Assert.False(resources.HasResults);
    }

    [Fact]
    public async Task LiveScan_ShouldIncludeEntraDirectoryAndPimRows()
    {
        var scanner = new LiveCloudScanService(new ICloudProvider[] { new FakeEntraCloudProvider() });

        var result = await scanner.ScanAsync();

        Assert.Contains(result.IdentityRecords, principal => principal.Name == "Entra User");
        Assert.Contains(result.IdentityRecords, principal => principal.Name == "Entra Group");
        Assert.Contains(result.EffectiveAccessRecords, assignment => assignment.AssignmentType == "Active (PIM)");
        Assert.Contains(result.EffectiveAccessRecords, assignment => assignment.AssignmentType == "Eligible (PIM)");
    }

    [Fact]
    public async Task LiveScanHistory_ShouldRetainAccessForSevenDaysThenPruneIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var now = DateTime.UtcNow;
        var historyStore = new LocalLiveScanHistoryStore(Path.Combine(directory, "scans.jsonl"), () => now);
        var scanService = new LiveCloudScanService(new ICloudProvider[] { new FakeCloudProvider(CloudProviderType.Azure, connected: true) }, historyStore);

        await scanService.ScanAsync();
        var recentScans = await historyStore.GetRecentAsync();

        Assert.Single(recentScans);
        Assert.Single(recentScans[0].EffectiveAccess);
        now = now.AddDays(7).AddSeconds(1);
        Assert.Empty(await historyStore.GetRecentAsync());
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task DataScreens_ShouldShowDemoRecordsAndLiveScanResults()
    {
        var accountId = Guid.NewGuid();
        var principal = new CloudPrincipal { Id = Guid.NewGuid(), AccountId = accountId, CloudProvider = CloudProviderType.Azure, Name = "Demo owner", PrincipalId = "demo-owner" };
        var role = new CloudRole { Id = Guid.NewGuid(), AccountId = accountId, Name = "Reader", RoleId = "reader" };
        var permission = new CloudPermission { Id = Guid.NewGuid(), AccountId = accountId, Name = "Read resources", Action = "resource/read" };
        var resource = new CloudResource { Id = Guid.NewGuid(), AccountId = accountId, Name = "Demo vault", Type = "Vault" };
        var entitlement = new Entitlement { Id = Guid.NewGuid(), PrincipalId = principal.Id, RoleId = role.Id, PermissionId = permission.Id, CloudAccountId = accountId, ResourceId = resource.Id, AssignmentType = AssignmentType.Direct };
        var access = new EffectiveAccess { Id = Guid.NewGuid(), PrincipalId = principal.Id, CloudAccountId = accountId, ResourceId = resource.Id, PermissionId = permission.Id, RoleId = role.Id, Provider = CloudProvider.Azure, IsDirect = true };
        var finding = new RiskFindingModel { Id = Guid.NewGuid(), RuleId = Guid.NewGuid(), CloudProvider = CloudProvider.Azure, CloudAccountId = accountId, PrincipalId = principal.Id, Severity = RiskSeverity.High, Title = "Demo finding", Evidence = new RiskEvidence("Demo evidence") };
        var recommendation = new RecommendationModel { Id = Guid.NewGuid(), RiskFindingId = finding.Id, Title = "Review demo role", CurrentState = "Reader at account scope", SuggestedState = "Narrow resource access", Provider = CloudProvider.Azure };
        var scan = new Scan { Id = Guid.NewGuid(), CloudAccountId = accountId, StartedAt = DateTime.UtcNow.AddMinutes(-2), CompletedAt = DateTime.UtcNow, Status = ScanStatus.Completed };
        var snapshot = new DemoSnapshot(
            new[] { new CloudAccount { Id = accountId, CloudProvider = CloudProviderType.Azure } },
            new[] { principal },
            new[] { resource },
            new[] { entitlement },
            new[] { access },
            new[] { finding },
            new[] { recommendation },
            new[] { scan })
        {
            Roles = new[] { role },
            Permissions = new[] { permission }
        };
        var demoData = new SnapshotDemoDataService(snapshot);
        var historyDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var historyStore = new LocalLiveScanHistoryStore(Path.Combine(historyDirectory, "scans.jsonl"));
        var liveScan = new LiveCloudScanService(new ICloudProvider[] { new FakeCloudProvider(CloudProviderType.Azure, connected: true) }, historyStore);
        var entitlements = new EntitlementsViewModel(demoData, liveScan);
        var effectiveAccess = new EffectiveAccessViewModel(demoData, liveScan, historyStore);
        var riskFindings = new RiskFindingsViewModel(demoData, liveScan);
        var recommendations = new RecommendationsViewModel(demoData, new SnapshotRecommendationStore(snapshot), liveScan);
        var history = new ScanHistoryViewModel(demoData, liveScan, historyStore);
        var dashboard = new DashboardViewModel(demoData, new NoopAnalyticsService(), liveScan);

        await dashboard.RunScanAsync();
        await Task.WhenAll(entitlements.RefreshAsync(), effectiveAccess.RefreshAsync(), riskFindings.RefreshAsync(), recommendations.RefreshAsync(), history.RefreshAsync());

        Assert.True(entitlements.HasResults);
        Assert.True(entitlements.Entitlements.Count >= 3);
        Assert.Equal(2, effectiveAccess.AccessRecords.Count);
        Assert.Equal(2, riskFindings.Findings.Count);
        Assert.True(recommendations.HasResults);
        Assert.True(history.Scans.Count >= 2);
        Directory.Delete(historyDirectory, recursive: true);
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

    private sealed class RecordingTrialService : ITrialService
    {
        public RecordingTrialService(TrialInfo? existingTrial, TrialInfo newTrial)
        {
            ExistingTrial = existingTrial;
            NewTrial = newTrial;
        }

        public TrialInfo? ExistingTrial { get; set; }
        public TrialInfo NewTrial { get; }
        public string? CheckedUserId { get; private set; }
        public string? CheckedDeviceIdentifier { get; private set; }
        public string? StartedUserId { get; private set; }
        public string? StartedDeviceIdentifier { get; private set; }
        public Task<TrialInfo?> GetTrialStatusAsync(string userId, string deviceIdentifierHash, CancellationToken cancellationToken = default)
        {
            CheckedUserId = userId;
            CheckedDeviceIdentifier = deviceIdentifierHash;
            return Task.FromResult(ExistingTrial);
        }
        public Task<TrialInfo> StartTrialAsync(string userId, string deviceIdentifierHash, CancellationToken cancellationToken = default)
        {
            StartedUserId = userId;
            StartedDeviceIdentifier = deviceIdentifierHash;
            return Task.FromResult(NewTrial);
        }
    }

    private sealed class SnapshotDemoDataService(DemoSnapshot snapshot) : IDemoDataService
    {
        public event EventHandler? DataChanged { add { } remove { } }
        public bool IsDemoMode => true;
        public Task<DemoStatistics> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(snapshot.Statistics);
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DemoSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
    }

    private sealed class SnapshotRecommendationStore(DemoSnapshot snapshot) : IRecommendationStore
    {
        public Task<IReadOnlyList<RecommendationModel>> GetAsync(string? search = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RecommendationModel>>(snapshot.Recommendations);
    }

    private sealed class RecordingCloudAuthenticationLauncher : ICloudAuthenticationLauncher
    {
        public List<CloudProviderType> LaunchedProviders { get; } = new();
        public List<CloudProviderType> DisconnectedProviders { get; } = new();
        public void LaunchAuthentication(CloudProviderType provider) => LaunchedProviders.Add(provider);
        public void LaunchDisconnection(CloudProviderType provider) => DisconnectedProviders.Add(provider);
        public Task<bool> IsConnectedAsync(CloudProviderType provider, CancellationToken cancellationToken = default)
            => Task.FromResult(LaunchedProviders.Contains(provider) && !DisconnectedProviders.Contains(provider));
        public Task DisconnectAsync(CloudProviderType provider, CancellationToken cancellationToken = default)
        {
            DisconnectedProviders.Add(provider);
            return Task.CompletedTask;
        }
    }

    private sealed class TestNavigationService(CloudAccountsViewModel cloudAccounts) : INavigationService
    {
        public object CurrentViewModel { get; private set; } = new object();
        public event EventHandler? CurrentViewModelChanged;
        public void NavigateTo<TViewModel>() where TViewModel : class
        {
            CurrentViewModel = typeof(TViewModel) == typeof(CloudAccountsViewModel) ? cloudAccounts : new object();
            CurrentViewModelChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class RecordingDialogService(bool confirmationResult) : IDialogService
    {
        public List<string> Confirmations { get; } = new();
        public List<string> InformationMessages { get; } = new();
        public string? ReportPath { get; set; }
        public void ShowInformation(string message, string title = "YASK-CIEM") => InformationMessages.Add(message);
        public void ShowError(string message, string title = "YASK-CIEM") { }
        public string? ChooseReportPath() => ReportPath;
        public bool Confirm(string message, string title = "YASK-CIEM")
        {
            Confirmations.Add(message);
            return confirmationResult;
        }
    }

    private sealed class RecordingReportService : IReportService
    {
        public string? ReportType { get; private set; }
        public string? OutputPath { get; private set; }
        public IReadOnlyList<CloudEntitle.Domain.RiskFinding>? CurrentScanFindings { get; private set; }
        public Task<string> GenerateReportAsync(string reportType, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);
        public Task<string> GenerateReportAsync(string reportType, string outputPath, CancellationToken cancellationToken = default)
        {
            ReportType = reportType;
            OutputPath = outputPath;
            return Task.FromResult(outputPath);
        }
        public Task<string> GenerateReportAsync(string reportType, string outputPath, IReadOnlyList<CloudEntitle.Domain.RiskFinding>? currentScanFindings, CancellationToken cancellationToken = default)
        {
            ReportType = reportType;
            OutputPath = outputPath;
            CurrentScanFindings = currentScanFindings;
            return Task.FromResult(outputPath);
        }
    }

    private sealed class FakeCloudProvider(CloudProviderType providerType, bool connected) : ICloudProvider, IResourceProvider, ICloudEffectiveAccessProvider
    {
        private readonly Guid _accountId = Guid.NewGuid();
        public CloudProviderType ProviderType => providerType;
        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(connected);

        public Task<IReadOnlyList<CloudAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudAccount>>(new[]
            {
                new CloudAccount { Id = _accountId, Name = "Test subscription", CloudProvider = providerType, SubscriptionId = "sub-test", ExternalAccountId = "tenant-test" }
            });

        public Task<IReadOnlyList<CloudPrincipal>> DiscoverIdentitiesAsync(Guid accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudPrincipal>>(new[]
            {
                new CloudPrincipal { Id = Guid.NewGuid(), AccountId = accountId, CloudProvider = providerType, Name = "Owner", PrincipalId = "owner-test", IdentityType = IdentityType.User, IsPrivileged = true },
                new CloudPrincipal { Id = Guid.NewGuid(), AccountId = accountId, CloudProvider = providerType, Name = "Reader", PrincipalId = "reader-test", IdentityType = IdentityType.User }
            });

        public Task<IReadOnlyList<CloudRole>> DiscoverRolesAsync(Guid accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudRole>>(new[]
            {
                new CloudRole { Id = Guid.NewGuid(), AccountId = accountId, Name = "Owner", RoleId = "owner-role" }
            });

        public Task<IReadOnlyList<CloudPermission>> DiscoverPermissionsAsync(Guid accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudPermission>>(new[]
            {
                new CloudPermission { Id = Guid.NewGuid(), AccountId = accountId, Name = "Owner", Action = "*", Resource = "*", Source = "Test provider" }
            });

        public Task<IReadOnlyList<CloudResource>> GetResourcesAsync(Guid accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudResource>>(new[]
            {
                new CloudResource { Id = Guid.NewGuid(), AccountId = accountId, Name = "Test resource", ResourceId = "resource-test", Type = "Resource" }
            });

        public Task<IReadOnlyList<CloudEffectiveAccessAssignment>> DiscoverEffectiveAccessAsync(Guid accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudEffectiveAccessAssignment>>(new[]
            {
                new CloudEffectiveAccessAssignment("test-principal", "Owner", "test-scope", "Test assignment", true)
            });
    }

    private sealed class FakeEntraCloudProvider : ICloudProvider, IEntraDirectoryProvider
    {
        private readonly Guid _accountId = Guid.NewGuid();
        public CloudProviderType ProviderType => CloudProviderType.Azure;
        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<IReadOnlyList<CloudAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudAccount>>(new[] { new CloudAccount { Id = _accountId, Name = "Azure test", CloudProvider = CloudProviderType.Azure } });
        public Task<IReadOnlyList<CloudPrincipal>> DiscoverIdentitiesAsync(Guid accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudPrincipal>>(Array.Empty<CloudPrincipal>());
        public Task<IReadOnlyList<CloudRole>> DiscoverRolesAsync(Guid accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudRole>>(Array.Empty<CloudRole>());
        public Task<IReadOnlyList<CloudPermission>> DiscoverPermissionsAsync(Guid accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudPermission>>(Array.Empty<CloudPermission>());
        public Task<EntraDirectoryScanResult> DiscoverEntraDirectoryAsync(Guid accountId, CancellationToken cancellationToken = default)
            => Task.FromResult(new EntraDirectoryScanResult(
                new[]
                {
                    new CloudPrincipal { Id = Guid.NewGuid(), AccountId = accountId, CloudProvider = CloudProviderType.Azure, Name = "Entra User", PrincipalId = "entra-user", IdentityType = IdentityType.User },
                    new CloudPrincipal { Id = Guid.NewGuid(), AccountId = accountId, CloudProvider = CloudProviderType.Azure, Name = "Entra Group", PrincipalId = "entra-group", IdentityType = IdentityType.Group }
                },
                new[]
                {
                    new CloudEffectiveAccessAssignment("Entra User", "Global Administrator", "/", "Entra PIM", true, "Active (PIM)"),
                    new CloudEffectiveAccessAssignment("Entra Group", "Security Administrator", "/", "Entra PIM", true, "Eligible (PIM)")
                },
                new[] { "Azure Entra: discovered test directory data." }));
    }
}
