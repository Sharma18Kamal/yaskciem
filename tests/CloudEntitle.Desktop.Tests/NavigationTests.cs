using System.IO;
using System.Text.Json;
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
        services.AddTransient<AccessPathExplorerViewModel>();
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
        services.AddTransient<AccessPathExplorerViewModel>();
        services.AddSingleton<INavigationService, NavigationService>();
        using var provider = services.BuildServiceProvider();
        var navigation = provider.GetRequiredService<INavigationService>();
        var viewModel = new MainWindowViewModel(navigation);

        Assert.Equal(15, viewModel.NavigationItems.Count);
        Assert.Equal("Dashboard", viewModel.CurrentNavigationItem.Title);
        Assert.Contains(viewModel.NavigationItems, item => item.Title == "Effective Access");
        Assert.Contains(viewModel.NavigationItems, item => item.Title == "Access Paths");
        Assert.Contains(viewModel.NavigationItems, item => item.Title == "Remediation");
        Assert.Contains(viewModel.NavigationItems, item => item.Title == "Organizations");
        Assert.Contains(viewModel.NavigationItems, item => item.Title == "Access Reviews");
        Assert.Equal("YASK-CIEM", viewModel.ApplicationName);
        Assert.Equal("Cloud Accounts", viewModel.CloudAccountsNavigationItem.Title);
        Assert.Equal("1.0.0", viewModel.ApplicationVersion);
    }

    [Fact]
    public async Task MainWindowViewModel_ShouldRefreshAndShareCloudCliStatusAtStartup()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDemoDataService, NoopDemoDataService>();
        services.AddSingleton<IAnalyticsService, NoopAnalyticsService>();
        services.AddTransient<DashboardViewModel>();
        services.AddSingleton<INavigationService, NavigationService>();
        using var provider = services.BuildServiceProvider();
        var cloudState = new CloudConnectionState();
        var authenticationLauncher = new RecordingCloudAuthenticationLauncher();
        authenticationLauncher.LaunchedProviders.Add(CloudProviderType.Azure);
        var viewModel = new MainWindowViewModel(provider.GetRequiredService<INavigationService>(), cloudState, authenticationLauncher);

        await viewModel.RefreshCloudConnectionStatusAsync();

        Assert.Equal("Azure connected", viewModel.CloudConnectionStatus);
    }

    [Fact]
    public void MainWindowViewModel_ShouldOfferFreeEditionWithoutTrialOrDeviceIdentityService()
    {
        var viewModel = new MainWindowViewModel(new TestNavigationService(new CloudAccountsViewModel(new NoopDemoDataService())));

        Assert.Equal("Free standalone edition", viewModel.EditionDisplayName);
        Assert.Equal("Ready", viewModel.StatusMessage);
        Assert.DoesNotContain(typeof(MainWindowViewModel).GetConstructors().SelectMany(constructor => constructor.GetParameters()),
            parameter => parameter.ParameterType == typeof(ITrialService) || parameter.Name == "getDeviceIdentifier");
        Assert.DoesNotContain(typeof(MainWindowViewModel).GetMethods(), method =>
            method.Name is "InitializeTrialAsync" or "RefreshTrialStatusAsync");
    }

    [Fact]
    public void MainWindowViewModel_ShouldAllowCloudDisconnectWithoutTrialInitialization()
    {
        var state = new CloudConnectionState();
        var viewModel = new MainWindowViewModel(new TestNavigationService(new CloudAccountsViewModel(new NoopDemoDataService())), state);
        Assert.False(viewModel.CanDisconnectCloud);
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        state.Update(new[] { CloudProviderType.Azure });

        Assert.True(viewModel.CanDisconnectCloud);
        Assert.Contains(nameof(MainWindowViewModel.CanDisconnectCloud), changed);
        state.DisconnectProvider(CloudProviderType.Azure);
        Assert.False(viewModel.CanDisconnectCloud);
    }

    [Fact]
    public void CloudConnectionState_ShouldKeepExplicitlyDisconnectedProviderOutUntilReconnect()
    {
        var state = new CloudConnectionState();
        state.Update(new[] { CloudProviderType.Azure, CloudProviderType.Aws });

        state.DisconnectProvider(CloudProviderType.Aws);
        state.Update(new[] { CloudProviderType.Azure, CloudProviderType.Aws });

        Assert.DoesNotContain(CloudProviderType.Aws, state.ConnectedProviders);
        Assert.Contains(CloudProviderType.Azure, state.ConnectedProviders);
        Assert.True(state.IsExplicitlyDisconnected(CloudProviderType.Aws));

        state.AllowConnection(CloudProviderType.Aws);
        state.Update(new[] { CloudProviderType.Azure, CloudProviderType.Aws });

        Assert.Contains(CloudProviderType.Aws, state.ConnectedProviders);
        Assert.False(state.IsExplicitlyDisconnected(CloudProviderType.Aws));
    }

    [Fact]
    public async Task CloudAccountsViewModel_ShouldAllowMultipleProviderSignIns()
    {
        var launcher = new RecordingCloudAuthenticationLauncher();
        var viewModel = new CloudAccountsViewModel(new NoopDemoDataService(), launcher);

        Assert.Equal(new[] { "Azure", "AWS", "Google Cloud" }, viewModel.Providers.Select(provider => provider.Name));

        await viewModel.ConnectAsync(CloudProviderType.Azure);

        Assert.Equal(new[] { CloudProviderType.Azure }, launcher.LaunchedProviders);
        Assert.True(viewModel.Providers.Single(provider => provider.Provider == CloudProviderType.Azure).IsConnected);
        Assert.True(viewModel.Providers.Single(provider => provider.Provider == CloudProviderType.Aws).CanConnect);
        Assert.True(viewModel.Providers.Single(provider => provider.Provider == CloudProviderType.Gcp).CanConnect);
        Assert.Contains("YASK-CIEM does not receive or store", viewModel.ConnectionGuidance);

        await viewModel.ConnectAsync(CloudProviderType.Gcp);

        Assert.Equal(new[] { CloudProviderType.Azure, CloudProviderType.Gcp }, launcher.LaunchedProviders);
        Assert.True(viewModel.Providers.Single(provider => provider.Provider == CloudProviderType.Azure).IsConnected);
        Assert.True(viewModel.Providers.Single(provider => provider.Provider == CloudProviderType.Gcp).IsConnected);
    }

    [Fact]
    public async Task CloudAccountsViewModel_ShouldScanOnlyVerifiedScopeAndShowPartialCoverage()
    {
        var account = new LiveScanAccountRecord("Aws", "Approved", "123456789012", "approved-sso", "", "Scanned");
        var result = VerifiedScanResult(account);
        var scan = new OnboardingLiveScanService(result, [account]);
        var launcher = new RecordingCloudAuthenticationLauncher();
        var viewModel = new CloudAccountsViewModel(new NoopDemoDataService(), launcher, liveCloudScanService: scan)
        {
            VerificationProvider = "Aws", VerificationAccountId = account.AccountId, VerificationProfile = account.Profile
        };
        Assert.False(viewModel.CanScanVerifiedAccount);
        await viewModel.ScanVerifiedAccountAsync();
        Assert.Empty(scan.VerifiedScanRequests);
        await viewModel.VerifyEnteredAccountAsync();
        Assert.True(viewModel.CanScanVerifiedAccount);

        await viewModel.ScanVerifiedAccountAsync();

        var request = Assert.Single(scan.VerifiedScanRequests);
        Assert.Equal(account.SelectionKey, request.Account.SelectionKey);
        Assert.Equal("fixture-identity", request.Identity.Identity);
        Assert.Contains("Collection finished", viewModel.VerifiedScanStatus);
        Assert.Contains("1 source(s)", viewModel.VerifiedScanStatus);
        Assert.Contains("No account was bound", viewModel.VerifiedScanStatus);
        Assert.Contains(viewModel.SourceCoverage, item => item.Status == "Denied");
        Assert.False(viewModel.CanScanVerifiedAccount);
        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task CloudAccountsViewModel_ShouldRejectScanAfterScopeEdits()
    {
        var account = new LiveScanAccountRecord("Aws", "Approved", "123456789012", "approved-sso", "", "Scanned");
        var scan = new OnboardingLiveScanService(VerifiedScanResult(account), [account]);
        var viewModel = new CloudAccountsViewModel(new NoopDemoDataService(), new RecordingCloudAuthenticationLauncher(), liveCloudScanService: scan)
        {
            VerificationProvider = "Aws", VerificationAccountId = account.AccountId, VerificationProfile = account.Profile
        };
        await viewModel.VerifyEnteredAccountAsync();
        viewModel.VerificationProfile = "other-profile";
        Assert.False(viewModel.CanScanVerifiedAccount);
        await viewModel.ScanVerifiedAccountAsync();
        Assert.Empty(scan.VerifiedScanRequests);
        Assert.Contains("Scan not started", viewModel.VerifiedScanStatus);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CloudAccountsViewModel_ShouldSurfaceScanFailureOrMissingScope(bool throwFailure)
    {
        var account = new LiveScanAccountRecord("Aws", "Approved", "123456789012", "approved-sso", "", "Scanned");
        var scan = new OnboardingLiveScanService(VerifiedScanResult(account) with { AccountRecords = [], AccountCount = 0 }, [account])
        {
            VerifiedScanFailure = throwFailure ? new InvalidOperationException("Identity changed.") : null
        };
        var viewModel = new CloudAccountsViewModel(new NoopDemoDataService(), new RecordingCloudAuthenticationLauncher(), liveCloudScanService: scan)
        {
            VerificationProvider = "Aws", VerificationAccountId = account.AccountId, VerificationProfile = account.Profile
        };
        await viewModel.VerifyEnteredAccountAsync();
        await viewModel.ScanVerifiedAccountAsync();
        Assert.Contains("Verified-account scan failed", viewModel.VerifiedScanStatus);
        Assert.NotNull(viewModel.ErrorMessage);
        Assert.False(viewModel.CanScanVerifiedAccount);
    }

    [Fact]
    public async Task CloudAccountsViewModel_ShouldPreventDuplicateScanWhileFirstIsRunning()
    {
        var account = new LiveScanAccountRecord("Aws", "Approved", "123456789012", "approved-sso", "", "Scanned");
        var result = VerifiedScanResult(account);
        var pending = new TaskCompletionSource<LiveCloudScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scan = new OnboardingLiveScanService(result, [account]) { PendingVerifiedScan = pending };
        var viewModel = new CloudAccountsViewModel(new NoopDemoDataService(), new RecordingCloudAuthenticationLauncher(), liveCloudScanService: scan)
        {
            VerificationProvider = "Aws", VerificationAccountId = account.AccountId, VerificationProfile = account.Profile
        };
        await viewModel.VerifyEnteredAccountAsync();
        var first = viewModel.ScanVerifiedAccountAsync();
        Assert.False(viewModel.CanScanVerifiedAccount);
        await viewModel.ScanVerifiedAccountAsync();
        Assert.Single(scan.VerifiedScanRequests);
        pending.SetResult(result);
        await first;
        Assert.Contains("Collection finished", viewModel.VerifiedScanStatus);
    }

    private static LiveCloudScanResult VerifiedScanResult(LiveScanAccountRecord account)
        => new(1, 0, 0, 0, 0, 0, [], DateTime.UtcNow, [], [], [], [], [], DateTime.UtcNow, [], [account])
        {
            Coverage = [new ScanCoverageRecord(account.Provider, "Identities", ScanCoverageStatus.Denied, 0, "Read permission missing.")]
        };

    [Fact]
    public async Task CloudAccountsViewModel_ShouldVerifyUserEnteredAzureScopeAndExpectedTenant()
    {
        var subscription = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid().ToString();
        var launcher = new RecordingCloudAuthenticationLauncher
        {
            VerificationResult = new("Azure", subscription, "approved-identity", tenant, DateTime.UtcNow)
        };
        var viewModel = new CloudAccountsViewModel(new NoopDemoDataService(), launcher)
        {
            VerificationAccountId = $" {subscription} ",
            VerificationTenantId = $" {tenant} "
        };
        await viewModel.VerifyEnteredAccountAsync();
        Assert.Contains($"Verified Azure account {subscription}", viewModel.VerificationStatus);
        Assert.Contains(tenant, viewModel.VerificationStatus);
        Assert.Equal(subscription, Assert.Single(launcher.VerifiedAccounts).AccountId);
        Assert.Empty(launcher.LaunchedProviders);
        Assert.Empty(viewModel.DiscoveredAccounts);
        viewModel.VerificationTenantId = Guid.NewGuid().ToString();
        Assert.DoesNotContain("Verified Azure", viewModel.VerificationStatus);
        await viewModel.VerifyEnteredAccountAsync();
        Assert.StartsWith("Not verified.", viewModel.VerificationStatus);
        Assert.Contains("does not match", viewModel.ErrorMessage);
    }

    [Theory]
    [InlineData("Aws", "123456789012", "approved-sso", "")]
    [InlineData("Aws", "123456789012", "default", "")]
    [InlineData("Gcp", "approved-project", "", "approved@example.test")]
    public async Task CloudAccountsViewModel_ShouldVerifyRuntimeAwsOrGcpScope(string provider, string accountId, string profile, string identity)
    {
        var launcher = new RecordingCloudAuthenticationLauncher
        {
            VerificationResult = new(provider, accountId, string.IsNullOrEmpty(identity) ? "assumed-role/audit" : identity, "", DateTime.UtcNow)
        };
        var viewModel = new CloudAccountsViewModel(new NoopDemoDataService(), launcher)
        {
            VerificationProvider = provider,
            VerificationAccountId = accountId,
            VerificationProfile = profile,
            VerificationIdentity = identity
        };
        await viewModel.VerifyEnteredAccountAsync();
        Assert.Contains($"Verified {provider} account {accountId}", viewModel.VerificationStatus);
        Assert.Equal(profile, Assert.Single(launcher.VerifiedAccounts).Profile);
        Assert.Empty(launcher.LaunchedProviders);
    }

    [Theory]
    [InlineData("Azure", "not-a-guid", "", "", "")]
    [InlineData("Azure", "11111111-1111-1111-1111-111111111111", "", "", "")]
    [InlineData("Aws", "123", "", "default", "")]
    [InlineData("Aws", "123456789012", "", "", "")]
    [InlineData("Aws", "123456789012", "", "--other-option", "")]
    [InlineData("Gcp", "bad_project", "", "", "user@example.test")]
    [InlineData("Gcp", "approved-project", "", "", "")]
    [InlineData("Other", "account", "", "", "")]
    public async Task CloudAccountsViewModel_ShouldValidateRuntimeScopeBeforeAnyCliCall(string provider, string accountId, string tenant, string profile, string identity)
    {
        var launcher = new RecordingCloudAuthenticationLauncher();
        var viewModel = new CloudAccountsViewModel(new NoopDemoDataService(), launcher)
        {
            VerificationProvider = provider,
            VerificationAccountId = accountId,
            VerificationTenantId = tenant,
            VerificationProfile = profile,
            VerificationIdentity = identity
        };
        await viewModel.VerifyEnteredAccountAsync();
        Assert.StartsWith("Not verified.", viewModel.VerificationStatus);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.ErrorMessage));
        Assert.Empty(launcher.VerifiedAccounts);
    }

    [Theory]
    [InlineData("Gcp", "approved-project", "other@example.test")]
    [InlineData("Gcp", "other-project", "approved@example.test")]
    [InlineData("Aws", "approved-project", "approved@example.test")]
    public async Task CloudAccountsViewModel_ShouldRejectReturnedScopeMismatch(string actualProvider, string actualAccount, string actualIdentity)
    {
        var launcher = new RecordingCloudAuthenticationLauncher
        {
            VerificationResult = new(actualProvider, actualAccount, actualIdentity, "", DateTime.UtcNow)
        };
        var viewModel = new CloudAccountsViewModel(new NoopDemoDataService(), launcher)
        {
            VerificationProvider = "Gcp",
            VerificationAccountId = "approved-project",
            VerificationIdentity = "approved@example.test"
        };
        await viewModel.VerifyEnteredAccountAsync();
        Assert.StartsWith("Not verified.", viewModel.VerificationStatus);
        Assert.Contains("does not match", viewModel.ErrorMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloudAccountsViewModel_ShouldDiscardLateVerificationAfterScopeEdit(bool failure)
    {
        var pending = new TaskCompletionSource<CloudAccountVerification>(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = new RecordingCloudAuthenticationLauncher { PendingVerification = pending };
        var viewModel = new CloudAccountsViewModel(new NoopDemoDataService(), launcher)
        {
            VerificationProvider = "Aws",
            VerificationAccountId = "123456789012",
            VerificationProfile = "approved"
        };
        var verification = viewModel.VerifyEnteredAccountAsync();
        viewModel.VerificationAccountId = "999999999999";
        if (failure) pending.SetException(new InvalidOperationException("Old account failed."));
        else pending.SetResult(new("Aws", "123456789012", "fixture", "", DateTime.UtcNow));
        await verification;
        Assert.DoesNotContain("Verified Aws", viewModel.VerificationStatus);
        Assert.DoesNotContain("Old account failed", viewModel.VerificationStatus);
        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task CloudAccountsViewModel_ShouldShowVerifiedIdentityAndInvalidateOnSelectionChange()
    {
        var launcher = new RecordingCloudAuthenticationLauncher();
        var viewModel = new CloudAccountsViewModel(new NoopDemoDataService(), launcher);
        viewModel.SelectedVerificationAccount = new("Aws", "Approved", "123456789012", "audit", "Reader");

        await viewModel.VerifySelectedAccountAsync();

        Assert.Contains("Verified Aws account 123456789012 as fixture-identity", viewModel.VerificationStatus);
        Assert.Contains("run a read-only scan", viewModel.VerificationStatus);
        viewModel.SelectedVerificationAccount = null;
        Assert.DoesNotContain("Verified Aws", viewModel.VerificationStatus);
        await viewModel.VerifySelectedAccountAsync();
        Assert.Contains("Select a discovered account", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task CloudAccountsViewModel_ShouldExposeVerificationFailureWithoutSuccessStatus()
    {
        var launcher = new RecordingCloudAuthenticationLauncher { VerificationFailure = new InvalidOperationException("Scope mismatch") };
        var viewModel = new CloudAccountsViewModel(new NoopDemoDataService(), launcher);
        viewModel.SelectedVerificationAccount = new("Aws", "Approved", "123456789012", "audit", "Reader");
        await viewModel.VerifySelectedAccountAsync();
        Assert.Equal("Scope mismatch", viewModel.ErrorMessage);
        Assert.StartsWith("Not verified.", viewModel.VerificationStatus);
    }

    [Fact]
    public void CloudAccountsViewModel_ShouldRetainLastGapFreeCollectionAfterPartialScan()
    {
        var completedAt = DateTime.UtcNow.AddHours(-1);
        var result = new LiveCloudScanResult(1, 0, 0, 0, 0, 0, Array.Empty<RiskFinding>(), completedAt,
            Array.Empty<string>(), Array.Empty<CloudPrincipal>(), Array.Empty<CloudResource>(),
            Array.Empty<DiscoveredCloudRole>(), Array.Empty<DiscoveredCloudPermission>(), completedAt.AddMinutes(-1),
            Array.Empty<LiveEffectiveAccessRecord>(), Array.Empty<LiveScanAccountRecord>())
        {
            Coverage = new[] { new ScanCoverageRecord("Azure", "Resources", ScanCoverageStatus.Collected, 1, "Read succeeded.") }
        };
        var scan = new OnboardingLiveScanService(result, Array.Empty<LiveScanAccountRecord>());
        var viewModel = new CloudAccountsViewModel(new NoopDemoDataService(), new RecordingCloudAuthenticationLauncher(), liveCloudScanService: scan);
        var azure = viewModel.Providers.Single(provider => provider.Provider == CloudProviderType.Azure);
        var successful = azure.LastSuccessfulScanText;
        Assert.Contains(completedAt.ToLocalTime().ToString("g"), successful);
        foreach (var status in new[] { ScanCoverageStatus.Partial, ScanCoverageStatus.Denied, ScanCoverageStatus.Unavailable, ScanCoverageStatus.Unsupported, ScanCoverageStatus.Skipped })
        {
            scan.CompleteScan(result with
            {
                CompletedAt = completedAt.AddMinutes(30),
                Coverage = new[] { new ScanCoverageRecord("Azure", "Resources", status, 0, "Not fully collected.") }
            });
            Assert.Equal(successful, azure.LastSuccessfulScanText);
            Assert.DoesNotContain("No permission or unsupported-source gaps", azure.AccessValidationText);
        }
    }

    [Fact]
    public async Task CloudAccountsViewModel_ShouldDiscoverAccountsAndExposeLastPermissionCoverage()
    {
        var account = new LiveScanAccountRecord("Azure", "Production", "subscription-123", "", "Reader", "Scanned");
        var completedAt = DateTime.UtcNow;
        var scan = new LiveCloudScanResult(1, 0, 0, 0, 0, 0, Array.Empty<RiskFinding>(), completedAt,
            Array.Empty<string>(), Array.Empty<CloudPrincipal>(), Array.Empty<CloudResource>(),
            Array.Empty<DiscoveredCloudRole>(), Array.Empty<DiscoveredCloudPermission>(), completedAt.AddMinutes(-1),
            Array.Empty<LiveEffectiveAccessRecord>(), new[] { account })
        {
            Coverage = new[]
            {
                new ScanCoverageRecord("Azure", "Identities", ScanCoverageStatus.Denied, 0, "Microsoft Graph consent missing."),
                new ScanCoverageRecord("Azure", "Resources", ScanCoverageStatus.Collected, 12, "Read succeeded.")
            }
        };
        var liveScan = new OnboardingLiveScanService(scan, new[] { account });
        var viewModel = new CloudAccountsViewModel(new NoopDemoDataService(), new RecordingCloudAuthenticationLauncher(), liveCloudScanService: liveScan);

        Assert.Equal(2, viewModel.SourceCoverage.Count);
        Assert.Contains(completedAt.ToLocalTime().ToString("g"), viewModel.LastCollectionText);
        await viewModel.DiscoverAccountsAsync();
        liveScan.CompleteScan();

        Assert.Equal(account.AccountId, Assert.Single(viewModel.DiscoveredAccounts).AccountId);
        Assert.Contains("does not validate every scan permission", viewModel.DiscoveryStatus);
        Assert.Equal(2, viewModel.SourceCoverage.Count);
        Assert.Contains("1 denied", viewModel.Providers.Single(provider => provider.Provider == CloudProviderType.Azure).AccessValidationText);
        Assert.Contains("0 unsupported", viewModel.Providers.Single(provider => provider.Provider == CloudProviderType.Azure).AccessValidationText);
        Assert.Contains(completedAt.ToLocalTime().ToString("g"), viewModel.LastCollectionText);
    }

    [Fact]
    public async Task CloudAccountsViewModel_ShouldNotConnectCachedAzureCredentialsWhenGcpIsSelected()
    {
        var launcher = new RecordingCloudAuthenticationLauncher();
        launcher.LaunchedProviders.Add(CloudProviderType.Azure);
        launcher.LaunchedProviders.Add(CloudProviderType.Gcp);
        var state = new CloudConnectionState();
        var viewModel = new CloudAccountsViewModel(new NoopDemoDataService(), launcher, cloudConnectionState: state);

        Assert.Empty(state.ConnectedProviders);
        await viewModel.ConnectAsync(CloudProviderType.Gcp);
        viewModel.RefreshConnectionsCommand.Execute(null);

        Assert.Equal(new[] { CloudProviderType.Gcp }, state.ConnectedProviders);
        Assert.False(viewModel.Providers.Single(provider => provider.Provider == CloudProviderType.Azure).IsConnected);
        Assert.True(viewModel.Providers.Single(provider => provider.Provider == CloudProviderType.Azure).CanConnect);
        Assert.True(await launcher.IsConnectedAsync(CloudProviderType.Azure));
        Assert.Empty(launcher.DisconnectedProviders);
    }

    [Fact]
    public async Task CloudAccountsViewModel_ShouldClearAwsErrorWhenGcpConnectionSucceeds()
    {
        var launcher = new RecordingCloudAuthenticationLauncher { LaunchFailure = new InvalidOperationException("AWS CLI unavailable.") };
        launcher.LaunchedProviders.Add(CloudProviderType.Gcp);
        var viewModel = new CloudAccountsViewModel(new NoopDemoDataService(), launcher);
        await viewModel.ConnectAsync(CloudProviderType.Aws);
        Assert.NotNull(viewModel.ErrorMessage);

        launcher.LaunchFailure = null;
        await viewModel.ConnectAsync(CloudProviderType.Gcp);

        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task CloudAccountsViewModel_ShouldConnectAwsWithoutWaitingForAnotherProviderCli()
    {
        var launcher = new RecordingCloudAuthenticationLauncher { BlockedProvider = CloudProviderType.Gcp };
        var viewModel = new CloudAccountsViewModel(new NoopDemoDataService(), launcher);

        try
        {
            await viewModel.ConnectAsync(CloudProviderType.Aws).WaitAsync(TimeSpan.FromSeconds(2));

            var aws = viewModel.Providers.Single(provider => provider.Provider == CloudProviderType.Aws);
            Assert.True(aws.IsConnected);
            Assert.False(aws.IsConnecting);
            Assert.Equal(1, launcher.LaunchedProviders.Count(provider => provider == CloudProviderType.Aws));
        }
        finally
        {
            launcher.ConnectionCheckRelease.TrySetResult(false);
        }
    }

    [Fact]
    public async Task CloudAccountsViewModel_ShouldRecoverWhenProviderCliIsMissing()
    {
        var launcher = new RecordingCloudAuthenticationLauncher
        {
            LaunchFailure = new FileNotFoundException("Google Cloud CLI was not found. Install it and reopen YASK-CIEM.")
        };
        var viewModel = new CloudAccountsViewModel(new NoopDemoDataService(), launcher);

        await viewModel.ConnectAsync(CloudProviderType.Gcp);

        var gcpProvider = viewModel.Providers.Single(provider => provider.Provider == CloudProviderType.Gcp);
        Assert.False(gcpProvider.IsConnecting);
        Assert.True(gcpProvider.CanConnect);
        Assert.Contains("Google Cloud CLI was not found", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task CloudAccountsViewModel_ShouldCancelPendingSignInWithoutSigningOut()
    {
        var launcher = new RecordingCloudAuthenticationLauncher { NeverConnect = true };
        var connectionState = new CloudConnectionState();
        var viewModel = new CloudAccountsViewModel(new NoopDemoDataService(), launcher, cloudConnectionState: connectionState);

        var connectTask = viewModel.ConnectAsync(CloudProviderType.Azure);
        Assert.Equal(CloudProviderType.Azure, connectionState.PendingProvider);
        Assert.Equal("Cancel Sign-in", new MainWindowViewModel(new TestNavigationService(viewModel), connectionState).DisconnectCloudButtonLabel);

        Assert.True(await viewModel.CancelPendingConnectionAsync());
        await connectTask;

        Assert.Null(connectionState.PendingProvider);
        Assert.Empty(launcher.DisconnectedProviders);
        Assert.True(viewModel.Providers.Single(provider => provider.Provider == CloudProviderType.Aws).CanConnect);
        Assert.Contains("Sign-in wait canceled", viewModel.ConnectionGuidance);
    }

    [Fact]
    public async Task CloudAccountsViewModel_ShouldLaunchAwsProfileConfigurationAndAllowCancellation()
    {
        var launcher = new RecordingCloudAuthenticationLauncher { NeverConnect = true };
        var state = new CloudConnectionState();
        var viewModel = new CloudAccountsViewModel(new NoopDemoDataService(), launcher, cloudConnectionState: state);

        var connectTask = viewModel.ConnectAsync(CloudProviderType.Aws);
        Assert.Equal(new[] { CloudProviderType.Aws }, launcher.LaunchedProviders);
        Assert.Equal(CloudProviderType.Aws, state.PendingProvider);
        Assert.Null(viewModel.ErrorMessage);
        Assert.Contains("yask-ciem profile", viewModel.ConnectionGuidance);
        Assert.True(await viewModel.CancelPendingConnectionAsync());
        await connectTask;

        Assert.Null(state.PendingProvider);
        Assert.True(viewModel.Providers.Single(provider => provider.Provider == CloudProviderType.Aws).CanConnect);
        Assert.Empty(launcher.DisconnectedProviders);
    }

    [Fact]
    public async Task DashboardViewModel_ShouldStopActiveScanAndRetainCompletedState()
    {
        var liveScan = new LiveCloudScanService(new ICloudProvider[] { new CancellationBlockingCloudProvider() });
        var dashboard = new DashboardViewModel(new NoopDemoDataService(), new NoopAnalyticsService(), liveScan);

        var scanTask = dashboard.RunScanAsync();
        Assert.True(dashboard.IsScanning);
        dashboard.StopScanCommand.Execute(null);
        await scanTask;

        Assert.False(dashboard.IsScanning);
        Assert.False(dashboard.IsLoading);
        Assert.Contains("Scan stopped", dashboard.ScanStatusMessage);
    }

    [Fact]
    public async Task CloudAccountsViewModel_ShouldRequireConfirmationBeforeDisconnecting()
    {
        var launcher = new RecordingCloudAuthenticationLauncher();
        var dialogs = new RecordingDialogService(confirmationResult: false);
        var viewModel = new CloudAccountsViewModel(new NoopDemoDataService(), launcher, dialogs);

        await viewModel.DisconnectProviderAsync(CloudProviderType.Aws);

        Assert.Empty(launcher.DisconnectedProviders);
        Assert.Contains(dialogs.Confirmations, message => message.Contains("IAM access keys will not be removed or revoked"));
    }

    [Fact]
    public async Task CloudAccountsViewModel_ShouldDisconnectAwsWithoutRemovingCliCredentialsAndReconnectExplicitly()
    {
        var launcher = new RecordingCloudAuthenticationLauncher();
        launcher.LaunchedProviders.Add(CloudProviderType.Aws);
        launcher.LaunchedProviders.Add(CloudProviderType.Azure);
        var connectionState = new CloudConnectionState();
        var viewModel = new CloudAccountsViewModel(new NoopDemoDataService(), launcher, new RecordingDialogService(true), connectionState);

        await viewModel.ConnectAsync(CloudProviderType.Azure);
        await viewModel.ConnectAsync(CloudProviderType.Aws);
        await viewModel.DisconnectProviderAsync(CloudProviderType.Aws);
        viewModel.RefreshConnectionsCommand.Execute(null);

        Assert.Empty(launcher.DisconnectedProviders);
        Assert.True(await launcher.IsConnectedAsync(CloudProviderType.Aws));
        Assert.False(viewModel.Providers.Single(provider => provider.Provider == CloudProviderType.Aws).IsConnected);
        Assert.True(viewModel.Providers.Single(provider => provider.Provider == CloudProviderType.Aws).CanConnect);
        Assert.True(viewModel.Providers.Single(provider => provider.Provider == CloudProviderType.Azure).IsConnected);
        Assert.DoesNotContain(CloudProviderType.Aws, connectionState.ConnectedProviders);
        Assert.Contains("CLI credentials are unchanged", viewModel.ConnectionGuidance);

        await viewModel.ConnectAsync(CloudProviderType.Aws);

        Assert.True(viewModel.Providers.Single(provider => provider.Provider == CloudProviderType.Aws).IsConnected);
        Assert.Contains(CloudProviderType.Aws, connectionState.ConnectedProviders);
        Assert.Equal(3, launcher.LaunchedProviders.Count(provider => provider == CloudProviderType.Aws));
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
        connectionState.Update(new[] { CloudProviderType.Azure });
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
    public async Task IdleTimeout_ShouldDisconnectAwsWithoutRemovingCliCredentials()
    {
        var launcher = new RecordingCloudAuthenticationLauncher();
        launcher.LaunchedProviders.Add(CloudProviderType.Aws);
        var connectionState = new CloudConnectionState();
        var cloudAccounts = new CloudAccountsViewModel(new NoopDemoDataService(), launcher, cloudConnectionState: connectionState);
        var viewModel = new MainWindowViewModel(new TestNavigationService(cloudAccounts), connectionState, launcher);

        await viewModel.DisconnectCloudForInactivityAsync();
        cloudAccounts.RefreshConnectionsCommand.Execute(null);

        Assert.Empty(launcher.DisconnectedProviders);
        Assert.True(await launcher.IsConnectedAsync(CloudProviderType.Aws));
        Assert.DoesNotContain(CloudProviderType.Aws, connectionState.ConnectedProviders);
        Assert.False(cloudAccounts.Providers.Single(provider => provider.Provider == CloudProviderType.Aws).IsConnected);
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
        Assert.Contains("Scan complete", dashboard.ScanStatusMessage, StringComparison.Ordinal);
        Assert.Contains("Scan History", dashboard.ScanStatusMessage, StringComparison.Ordinal);
        Assert.Contains(scanService.CurrentResult!.Coverage, item => item.Provider == "Aws" && item.DataSource == "CLI sign-in" && item.Status == ScanCoverageStatus.Unavailable);
        Assert.NotEqual("No scans yet", dashboard.LastScan);
    }

    [Fact]
    public async Task DashboardViewModel_ShouldShowEvidenceQualityAndUnknownTotals()
    {
        var now = DateTime.UtcNow;
        var scan = new LiveCloudScanResult(1, 1, 0, 0, 0, 0, Array.Empty<RiskFinding>(), now,
            Array.Empty<string>(), Array.Empty<CloudPrincipal>(), Array.Empty<CloudResource>(),
            Array.Empty<DiscoveredCloudRole>(), Array.Empty<DiscoveredCloudPermission>(), now,
            new[]
            {
                new LiveEffectiveAccessRecord("Azure", "fully-attributed", "Reader", "subscription", "Activity Log", false,
                    LastChangedAt: now, LastChangedBy: "admin@example.invalid"),
                new LiveEffectiveAccessRecord("Azure", "partial-attribution", "Reader", "subscription", "RBAC", false,
                    LastChangedAt: now),
                new LiveEffectiveAccessRecord("Azure", "unattributed", "Reader", "subscription", "RBAC", false)
            }, Array.Empty<LiveScanAccountRecord>())
        {
            Coverage = new[]
            {
                new ScanCoverageRecord("Azure", "RBAC assignments", ScanCoverageStatus.Collected, 3, "Collected."),
                new ScanCoverageRecord("Azure", "permission usage", ScanCoverageStatus.Unsupported, 0, "Unsupported."),
                new ScanCoverageRecord("Azure", "Entra directory", ScanCoverageStatus.Denied, 0, "Consent required.")
            },
            ResourceRiskFindings = new[]
            {
                new RiskFindingModel { Id = Guid.NewGuid(), EvaluationState = RiskEvaluationState.Unknown },
                new RiskFindingModel { Id = Guid.NewGuid(), EvaluationState = RiskEvaluationState.Unsupported }
            },
            ModeledAccessPaths = new[]
            {
                new CloudEntitle.Domain.Entities.AccessPath { Id = Guid.NewGuid(), PathStatus = AccessPathStatus.Unknown },
                new CloudEntitle.Domain.Entities.AccessPath { Id = Guid.NewGuid(), PathStatus = AccessPathStatus.Unsupported }
            }
        };
        var scanService = new FixedLiveCloudScanService(scan);
        var dashboard = new DashboardViewModel(new NoopDemoDataService(), new NoopAnalyticsService(), scanService);

        await dashboard.RunScanAsync();

        Assert.NotEqual("Not available", dashboard.EvidenceAge);
        Assert.Contains("Completed", dashboard.EvidenceAgeDetails);
        Assert.Equal("1/3 sources", dashboard.SourceCoverageSummary);
        Assert.Contains("Denied 1", dashboard.SourceCoverageDetails);
        Assert.Equal(1, dashboard.UnsupportedPermissionSourceCount);
        Assert.Contains("permission usage", dashboard.UnsupportedPermissionSourceDetails);
        Assert.Equal("33%", dashboard.AttributionConfidence);
        Assert.Contains("1/3", dashboard.AttributionDetails);
        Assert.Equal(2, dashboard.UnknownEvaluationTotal);
        Assert.Equal(2, dashboard.UnsupportedEvaluationTotal);
    }

    [Fact]
    public async Task LiveCloudScan_ShouldOnlyScanTheSingleConnectedProvider()
    {
        var azure = new FakeCloudProvider(CloudProviderType.Azure, connected: true);
        var gcp = new FakeCloudProvider(CloudProviderType.Gcp, connected: true);
        var connectionState = new CloudConnectionState();
        connectionState.Update(new[] { CloudProviderType.Gcp });
        var scanner = new LiveCloudScanService(new ICloudProvider[] { azure, gcp }, cloudConnectionState: connectionState);

        var result = await scanner.ScanAsync();

        Assert.Equal(1, result.AccountCount);
        Assert.All(result.AccountRecords, account => Assert.Equal(nameof(CloudProviderType.Gcp), account.Provider));
        Assert.All(result.IdentityRecords, identity => Assert.Equal(CloudProviderType.Gcp, identity.CloudProvider));
        Assert.DoesNotContain(result.ProviderMessages, message => message.StartsWith("Azure:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LiveCloudScan_ShouldNotRediscoverOrScanExplicitlyDisconnectedAws()
    {
        var connectionState = new CloudConnectionState();
        connectionState.Update(new[] { CloudProviderType.Aws });
        connectionState.DisconnectProvider(CloudProviderType.Aws);
        var scanner = new LiveCloudScanService(new ICloudProvider[]
        {
            new FakeCloudProvider(CloudProviderType.Aws, connected: true)
        }, cloudConnectionState: connectionState);

        Assert.Empty(await scanner.DiscoverAccountsAsync());
        var result = await scanner.ScanAsync();

        Assert.Equal(0, result.AccountCount);
        Assert.Empty(result.AccountRecords);
        Assert.Empty(connectionState.ConnectedProviders);
        Assert.Contains(result.ProviderMessages, message => message.Contains("disconnected from YASK-CIEM"));
    }

    [Fact]
    public async Task LiveCloudScan_ShouldScanMultipleConnectedProvidersSeparately()
    {
        var connectionState = new CloudConnectionState();
        connectionState.Update(new[] { CloudProviderType.Azure, CloudProviderType.Gcp });
        var scanner = new LiveCloudScanService(new ICloudProvider[]
        {
            new FakeCloudProvider(CloudProviderType.Azure, connected: true),
            new FakeCloudProvider(CloudProviderType.Gcp, connected: true)
        }, cloudConnectionState: connectionState);

        var result = await scanner.ScanAsync();

        Assert.Equal(2, result.AccountCount);
        Assert.Equal(new[] { "Azure", "Gcp" }, result.AccountRecords.Select(account => account.Provider).OrderBy(provider => provider, StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { CloudProviderType.Azure, CloudProviderType.Gcp }, result.IdentityRecords.Select(identity => identity.CloudProvider).Distinct().OrderBy(provider => provider).ToArray());
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
    public async Task LiveScanHistory_ShouldPersistLastChangedSeparatelyFromLastUsed()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var historyStore = new LocalLiveScanHistoryStore(Path.Combine(directory, "scans.jsonl"));
        var lastUsedAt = DateTime.UtcNow.AddDays(-2);
        var lastChangedAt = DateTime.UtcNow.AddDays(-1);
        var entry = new LiveScanHistoryEntry(
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow, "AWS", 1, 1, 0, 1, 1, 0,
            new[] { new LiveEffectiveAccessRecord("AWS", "audit-user", "ReadOnly", "account", "CloudTrail", false, LastUsedAt: lastUsedAt, LastChangedAt: lastChangedAt, LastChangedBy: "auditor@example.invalid") },
            Array.Empty<LiveScanAccountRecord>());

        await historyStore.RecordAsync(entry);
        var savedAccess = Assert.Single(Assert.Single(await historyStore.GetRecentAsync()).EffectiveAccess);

        Assert.Equal(lastUsedAt, savedAccess.LastUsedAt);
        Assert.Equal(lastChangedAt, savedAccess.LastChangedAt);
        Assert.Equal("auditor@example.invalid", savedAccess.LastChangedBy);
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task EffectiveAccess_ShouldShowOnlyConnectedProviderAndNewestMatchingHistory()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var historyStore = new LocalLiveScanHistoryStore(Path.Combine(directory, "scans.jsonl"));
        var now = DateTime.UtcNow;
        LiveScanHistoryEntry CreateEntry(string provider, string principal, DateTime completedAt)
            => new(completedAt.AddMinutes(-1), completedAt, provider, 1, 1, 0, 1, 1, 0,
                new[] { new LiveEffectiveAccessRecord(provider, principal, "Reader", "project", "Test", false) },
                Array.Empty<LiveScanAccountRecord>());

        await historyStore.RecordAsync(CreateEntry("Gcp", "older-gcp", now.AddMinutes(-10)));
        await historyStore.RecordAsync(CreateEntry("Azure", "newer-azure", now.AddMinutes(-2)));
        await historyStore.RecordAsync(CreateEntry("Gcp", "newest-gcp", now.AddMinutes(-1)));
        var connectionState = new CloudConnectionState();
        connectionState.Update(new[] { CloudProviderType.Gcp });
        var historyViewModel = new EffectiveAccessViewModel(
            new NoopDemoDataService(), liveScanHistoryStore: historyStore, cloudConnectionState: connectionState);

        await historyViewModel.RefreshAsync();

        var historicalRow = Assert.Single(historyViewModel.AccessRecords);
        Assert.Equal("Gcp", historicalRow.Provider);
        Assert.Equal("newest-gcp", historicalRow.Principal);

        var liveScan = new LiveCloudScanService(new ICloudProvider[]
        {
            new FakeCloudProvider(CloudProviderType.Azure, connected: true),
            new FakeCloudProvider(CloudProviderType.Gcp, connected: true)
        }, historyStore);
        var liveViewModel = new EffectiveAccessViewModel(
            new NoopDemoDataService(), liveScan, historyStore, connectionState);
        await liveScan.ScanAsync();
        await liveViewModel.RefreshAsync();

        Assert.NotEmpty(liveViewModel.AccessRecords);
        Assert.All(liveViewModel.AccessRecords, row => Assert.Equal("Gcp", row.Provider));
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
        var access = new CloudEntitle.Domain.Entities.EffectiveAccess { Id = Guid.NewGuid(), PrincipalId = principal.Id, CloudAccountId = accountId, ResourceId = resource.Id, PermissionId = permission.Id, RoleId = role.Id, Provider = CloudProvider.Azure, IsDirect = true };
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

        Assert.Contains("Scan History", dashboard.ScanStatusMessage, StringComparison.Ordinal);
        Assert.True(entitlements.HasResults);
        Assert.True(entitlements.Entitlements.Count >= 3);
        Assert.Contains(entitlements.Entitlements, row => row.Provider == "Azure" && row.Subject == "test-principal" && row.Grant == "Owner" && row.Source == "Test assignment");
        Assert.Equal(1, dashboard.Entitlements);
        Assert.Equal(2, effectiveAccess.AccessRecords.Count);
        Assert.Contains(effectiveAccess.AccessRecords, row => row.Principal == "test-principal" && row.Provider == "Azure");
        Assert.Equal(2, riskFindings.Findings.Count);
        Assert.True(recommendations.HasResults);
        Assert.True(history.Scans.Count >= 2);
        Directory.Delete(historyDirectory, recursive: true);
    }

    [Fact]
    public async Task PolicyWorkbench_ShouldExportSelectedProposalAsNonExecutableDraft()
    {
        var recommendation = new RecommendationModel
        {
            Id = Guid.NewGuid(),
            Title = "Narrow wildcard permission",
            RecommendationType = RecommendationType.NarrowWildcardPermission,
            Provider = CloudProvider.Azure,
            CurrentState = "* at subscription scope",
            SuggestedState = "storage.read at resource scope",
            Impact = "Impact=RequiresReview",
            ImpactState = RecommendationImpactState.RequiresReview,
            EvaluationState = RiskEvaluationState.Potential,
            Reason = "Required operations need owner validation.",
            Evidence = "Observed provider evidence"
        };
        var snapshot = new DemoSnapshot(
            Array.Empty<CloudAccount>(), Array.Empty<CloudPrincipal>(), Array.Empty<CloudResource>(), Array.Empty<Entitlement>(),
            Array.Empty<CloudEntitle.Domain.Entities.EffectiveAccess>(), Array.Empty<RiskFindingModel>(), new[] { recommendation }, Array.Empty<Scan>());
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "policy-draft.json");
        var dialogs = new RecordingDialogService(true) { PolicyDraftPath = path };
        var viewModel = new RecommendationsViewModel(new SnapshotDemoDataService(snapshot), new SnapshotRecommendationStore(snapshot), dialogService: dialogs);

        try
        {
            await viewModel.RefreshAsync();
            viewModel.SelectedRecommendation = Assert.Single(viewModel.Recommendations);
            Assert.True(viewModel.CanExportDraft);

            await viewModel.ExportDraftAsync();

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var draft = document.RootElement;
            Assert.False(draft.GetProperty("executable").GetBoolean());
            Assert.True(draft.GetProperty("approvalRequired").GetBoolean());
            Assert.Equal(recommendation.CurrentState, draft.GetProperty("currentState").GetString());
            Assert.Equal(recommendation.SuggestedState, draft.GetProperty("proposedState").GetString());
            Assert.Equal(recommendation.Impact, draft.GetProperty("impactAssessment").GetString());
            Assert.Equal(recommendation.Evidence, draft.GetProperty("evidence").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RiskFindings_ShouldOrderRowsByQualityAdjustedPriority()
    {
        var highPriority = new RiskFindingModel
        {
            Id = Guid.NewGuid(), RuleId = Guid.NewGuid(), CloudProvider = CloudProvider.Azure,
            CloudAccountId = Guid.NewGuid(), PrincipalId = Guid.NewGuid(), Severity = RiskSeverity.High,
            Title = "Fresh, high-priority finding", Evidence = new RiskEvidence("Complete current evidence"), PriorityScore = 78
        };
        var lowerPriority = new RiskFindingModel
        {
            Id = Guid.NewGuid(), RuleId = Guid.NewGuid(), CloudProvider = CloudProvider.Aws,
            CloudAccountId = Guid.NewGuid(), PrincipalId = Guid.NewGuid(), Severity = RiskSeverity.Critical,
            Title = "Stale, lower-priority finding", Evidence = new RiskEvidence("Stale evidence"), PriorityScore = 42
        };
        var snapshot = new DemoSnapshot(Array.Empty<CloudAccount>(), Array.Empty<CloudPrincipal>(), Array.Empty<CloudResource>(),
            Array.Empty<Entitlement>(), Array.Empty<CloudEntitle.Domain.Entities.EffectiveAccess>(), new[] { lowerPriority, highPriority },
            Array.Empty<RecommendationModel>(), Array.Empty<Scan>());
        var viewModel = new RiskFindingsViewModel(new SnapshotDemoDataService(snapshot));

        await viewModel.RefreshAsync();

        Assert.Equal(new[] { "Fresh, high-priority finding", "Stale, lower-priority finding" }, viewModel.Findings.Select(item => item.Title));
    }

    private sealed class NoopDemoDataService : IDemoDataService
    {
        public event EventHandler? DataChanged { add { } remove { } }
        public bool IsDemoMode => false;
        public Task<DemoStatistics> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DemoStatistics(0, 0, 0, 0, 0, 0, 0, null));
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DemoSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DemoSnapshot(Array.Empty<CloudAccount>(), Array.Empty<CloudPrincipal>(), Array.Empty<CloudResource>(), Array.Empty<Entitlement>(), Array.Empty<CloudEntitle.Domain.Entities.EffectiveAccess>(), Array.Empty<RiskFindingModel>(), Array.Empty<RecommendationModel>(), Array.Empty<Scan>()));
    }

    private sealed class NoopAnalyticsService : IAnalyticsService
    {
        public AnalyticsSnapshot Calculate(DemoSnapshot snapshot, DateTime? asOf = null) => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, Array.Empty<AnalyticsMetric>(), Array.Empty<AnalyticsMetric>(), Array.Empty<AnalyticsMetric>(), Array.Empty<AnalyticsMetric>(), Array.Empty<AnalyticsMetric>());
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
        public Exception? LaunchFailure { get; set; }
        public Exception? VerificationFailure { get; set; }
        public CloudAccountVerification? VerificationResult { get; set; }
        public TaskCompletionSource<CloudAccountVerification>? PendingVerification { get; set; }
        public List<LiveScanAccountRecord> VerifiedAccounts { get; } = [];
        public Task<CloudAccountVerification> VerifyAccountAsync(LiveScanAccountRecord account, CancellationToken cancellationToken = default)
        {
            VerifiedAccounts.Add(account);
            return VerificationFailure is not null
                ? throw VerificationFailure
                : PendingVerification?.Task ?? Task.FromResult(VerificationResult ?? new CloudAccountVerification(account.Provider, account.AccountId, "fixture-identity", "", DateTime.UtcNow));
        }
        public bool NeverConnect { get; set; }
        public CloudProviderType? BlockedProvider { get; set; }
        public TaskCompletionSource<bool> ConnectionCheckRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void LaunchAuthentication(CloudProviderType provider)
        {
            if (LaunchFailure is not null) throw LaunchFailure;
            LaunchedProviders.Add(provider);
        }
        public void LaunchDisconnection(CloudProviderType provider) => DisconnectedProviders.Add(provider);
        public Task<bool> IsConnectedAsync(CloudProviderType provider, CancellationToken cancellationToken = default)
            => provider == BlockedProvider
                ? ConnectionCheckRelease.Task.WaitAsync(cancellationToken)
                : Task.FromResult(!NeverConnect && LaunchedProviders.Contains(provider) && !DisconnectedProviders.Contains(provider));
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
        public string? PolicyDraftPath { get; set; }
        public string? ComplianceBundlePath { get; set; }
        public string? LocalBackupSavePath { get; set; }
        public string? LocalBackupOpenPath { get; set; }
        public void ShowInformation(string message, string title = "YASK-CIEM") => InformationMessages.Add(message);
        public void ShowError(string message, string title = "YASK-CIEM") { }
        public string? ChooseReportPath() => ReportPath;
        public string? ChoosePolicyDraftPath() => PolicyDraftPath;
        public string? ChooseComplianceBundlePath() => ComplianceBundlePath;
        public string? ChooseLocalBackupSavePath() => LocalBackupSavePath;
        public string? ChooseLocalBackupOpenPath() => LocalBackupOpenPath;
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

    private sealed class FixedLiveCloudScanService(LiveCloudScanResult result) : ILiveCloudScanService
    {
        public LiveCloudScanResult? CurrentResult { get; private set; }
        public event EventHandler? ScanCompleted;
        public Task<IReadOnlyList<LiveScanAccountRecord>> DiscoverAccountsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<LiveScanAccountRecord>>(Array.Empty<LiveScanAccountRecord>());
        public Task<LiveCloudScanResult> ScanAsync(CancellationToken cancellationToken = default)
            => ScanAsync(null, cancellationToken);
        public Task<LiveCloudScanResult> ScanAsync(IReadOnlySet<string>? selectedAccountKeys, CancellationToken cancellationToken = default)
        {
            CurrentResult = result;
            ScanCompleted?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(result);
        }
    }

    private sealed class OnboardingLiveScanService : ILiveCloudScanService
    {
        private readonly LiveCloudScanResult _result;
        private readonly IReadOnlyList<LiveScanAccountRecord> _discoveredAccounts;

        public OnboardingLiveScanService(LiveCloudScanResult result, IReadOnlyList<LiveScanAccountRecord> discoveredAccounts)
        {
            _result = result;
            _discoveredAccounts = discoveredAccounts;
            CurrentResult = result;
        }

        public LiveCloudScanResult? CurrentResult { get; private set; }
        public List<(LiveScanAccountRecord Account, CloudAccountVerification Identity)> VerifiedScanRequests { get; } = [];
        public Exception? VerifiedScanFailure { get; init; }
        public TaskCompletionSource<LiveCloudScanResult>? PendingVerifiedScan { get; init; }
        public async Task<LiveCloudScanResult> ScanVerifiedAccountAsync(LiveScanAccountRecord account, CloudAccountVerification verification, CancellationToken cancellationToken = default)
        {
            VerifiedScanRequests.Add((account, verification));
            if (VerifiedScanFailure is not null) throw VerifiedScanFailure;
            var result = PendingVerifiedScan is null ? _result : await PendingVerifiedScan.Task;
            CompleteScan(result);
            return result;
        }
        public event EventHandler? ScanCompleted;
        public Task<IReadOnlyList<LiveScanAccountRecord>> DiscoverAccountsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_discoveredAccounts);
        public Task<LiveCloudScanResult> ScanAsync(CancellationToken cancellationToken = default) => Task.FromResult(_result);
        public Task<LiveCloudScanResult> ScanAsync(IReadOnlySet<string>? selectedAccountKeys, CancellationToken cancellationToken = default)
            => Task.FromResult(_result);
        public void CompleteScan(LiveCloudScanResult? result = null)
        {
            CurrentResult = result ?? _result;
            ScanCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class CancellationBlockingCloudProvider : ICloudProvider
    {
        public CloudProviderType ProviderType => CloudProviderType.Gcp;
        public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return true;
        }
        public Task<IReadOnlyList<CloudAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CloudAccount>>(Array.Empty<CloudAccount>());
        public Task<IReadOnlyList<CloudPrincipal>> DiscoverIdentitiesAsync(Guid accountId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CloudPrincipal>>(Array.Empty<CloudPrincipal>());
        public Task<IReadOnlyList<CloudRole>> DiscoverRolesAsync(Guid accountId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CloudRole>>(Array.Empty<CloudRole>());
        public Task<IReadOnlyList<CloudPermission>> DiscoverPermissionsAsync(Guid accountId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CloudPermission>>(Array.Empty<CloudPermission>());
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
