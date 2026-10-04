using System.IO;
using System.Text.Json;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Analytics.Models;
using CloudEntitle.Analytics.Services;
using CloudEntitle.Domain;
using CloudEntitle.EffectiveAccess.Abstractions;
using CloudEntitle.EffectiveAccess.Models;
using CloudEntitle.EffectiveAccess.Services;
using CloudEntitle.Normalization.Models;

namespace CloudEntitle.Desktop.ViewModels;

public sealed class NavigationItem : ObservableObject
{
    private bool _isSelected;

    public NavigationItem(string title, string icon, Type viewModelType)
    {
        Title = title;
        Icon = icon;
        ViewModelType = viewModelType;
    }

    public string Title { get; }
    public string Icon { get; }
    public Type ViewModelType { get; }
    public bool IsSelected { get => _isSelected; internal set => SetProperty(ref _isSelected, value); }
}

public sealed class CloudConnectionState : ObservableObject
{
    private string _status = "Checking cloud sign-ins";
    private bool _hasConnections;
    private IReadOnlySet<CloudProviderType> _connectedProviders = new HashSet<CloudProviderType>();
    private CloudProviderType? _pendingProvider;

    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool HasConnections { get => _hasConnections; private set => SetProperty(ref _hasConnections, value); }
    public IReadOnlySet<CloudProviderType> ConnectedProviders { get => _connectedProviders; private set => SetProperty(ref _connectedProviders, value); }
    public CloudProviderType? PendingProvider { get => _pendingProvider; private set => SetProperty(ref _pendingProvider, value); }

    public void Update(IEnumerable<CloudProviderType> connectedProviders, CloudProviderType? pendingProvider = null)
    {
        var connected = connectedProviders.ToHashSet();
        ConnectedProviders = connected;
        PendingProvider = pendingProvider;
        HasConnections = connected.Count > 0 || pendingProvider.HasValue;
        Status = pendingProvider.HasValue
            ? $"Connecting to {pendingProvider.Value}"
            : connected.Count switch
            {
                0 => "No active CLI sign-in",
                1 => $"{connected.Single()} connected",
                _ => $"{connected.Count} cloud CLIs signed in"
            };
    }
}

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly CloudConnectionState _cloudConnectionState;
    private readonly ICloudAuthenticationLauncher? _authenticationLauncher;
    private readonly ITrialService? _trialService;
    private readonly Func<string> _getDeviceIdentifier;
    private object _currentViewModel;
    private NavigationItem _currentNavigationItem;
    private string _statusMessage = "Ready";
    private string _trialStatusText = "Checking trial";
    private bool _isTrialActive;
    private string? _trialUserId;
    private string? _trialDeviceIdentifier;
    private DateTime? _trialExpiresAtUtc;

    public MainWindowViewModel(
        INavigationService navigation,
        CloudConnectionState? cloudConnectionState = null,
        ICloudAuthenticationLauncher? authenticationLauncher = null,
        ITrialService? trialService = null,
        Func<string>? getDeviceIdentifier = null)
    {
        _navigation = navigation;
        _cloudConnectionState = cloudConnectionState ?? new CloudConnectionState();
        _authenticationLauncher = authenticationLauncher;
        _trialService = trialService;
        _getDeviceIdentifier = getDeviceIdentifier ?? WindowsDeviceIdentity.GetIdentifier;
        _cloudConnectionState.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(CloudConnectionState.Status)) OnPropertyChanged(nameof(CloudConnectionStatus));
            if (args.PropertyName == nameof(CloudConnectionState.HasConnections)) OnPropertyChanged(nameof(CanDisconnectCloud));
            if (args.PropertyName == nameof(CloudConnectionState.PendingProvider)) OnPropertyChanged(nameof(DisconnectCloudButtonLabel));
        };
        _navigation.CurrentViewModelChanged += (_, _) =>
        {
            CurrentViewModel = _navigation.CurrentViewModel;
            SetSelectedNavigationItem(NavigationItems!.FirstOrDefault(item => item.ViewModelType == CurrentViewModel.GetType()) ?? NavigationItems![0]);
            StatusMessage = $"Viewing {CurrentNavigationItem.Title}";
        };

        NavigationItems =
        [
            new("Dashboard", "⌂", typeof(DashboardViewModel)),
            new("Cloud Accounts", "▣", typeof(CloudAccountsViewModel)),
            new("Identities", "♙", typeof(IdentitiesViewModel)),
            new("Resources", "◇", typeof(ResourcesViewModel)),
            new("Entitlements", "◆", typeof(EntitlementsViewModel)),
            new("Effective Access", "↳", typeof(EffectiveAccessViewModel)),
            new("Access Paths", "⇢", typeof(AccessPathExplorerViewModel)),
            new("Risk Findings", "!", typeof(RiskFindingsViewModel)),
            new("Recommendations", "✓", typeof(RecommendationsViewModel)),
            new("Remediation", "⚒", typeof(RemediationViewModel)),
            new("Organizations", "▦", typeof(OrganizationViewModel)),
            new("Access Reviews", "☑", typeof(AccessReviewViewModel)),
            new("Scan History", "◷", typeof(ScanHistoryViewModel)),
            new("Feedback", "✎", typeof(FeedbackViewModel)),
            new("Settings", "⚙", typeof(SettingsViewModel))
        ];
        _currentViewModel = navigation.CurrentViewModel;
        _currentNavigationItem = NavigationItems.FirstOrDefault(item => item.ViewModelType == _currentViewModel.GetType()) ?? NavigationItems[0];
        _currentNavigationItem.IsSelected = true;
        NavigateCommand = new ParameterizedRelayCommand(parameter =>
        {
            if (parameter is NavigationItem item)
            {
                Navigate(item);
            }
        });
        OpenCloudDisconnectCommand = new AsyncRelayCommand(DisconnectActiveCloudAsync);
        _ = RefreshCloudConnectionStatusAsync();
    }

    public string ApplicationName => "YASK-CIEM";
    public string ApplicationVersion => typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    public string DatabaseStatus => "Connected";
    public bool IsTrialActive { get => _isTrialActive; private set { if (SetProperty(ref _isTrialActive, value)) OnPropertyChanged(nameof(CanDisconnectCloud)); } }
    public string TrialStatusText { get => _trialStatusText; private set => SetProperty(ref _trialStatusText, value); }
    public DateTime? TrialExpiresAtUtc { get => _trialExpiresAtUtc; private set => SetProperty(ref _trialExpiresAtUtc, value); }
    public string CloudConnectionStatus => _cloudConnectionState.Status;
    public bool CanDisconnectCloud => IsTrialActive && _cloudConnectionState.HasConnections;
    public string DisconnectCloudButtonLabel => _cloudConnectionState.PendingProvider.HasValue ? "Cancel Sign-in" : "Disconnect Cloud";
    public IReadOnlyList<NavigationItem> NavigationItems { get; }
    public NavigationItem CloudAccountsNavigationItem => NavigationItems[1];
    public NavigationItem SettingsNavigationItem => NavigationItems[^1];
    public object CurrentViewModel { get => _currentViewModel; private set => SetProperty(ref _currentViewModel, value); }
    public NavigationItem CurrentNavigationItem { get => _currentNavigationItem; private set => SetProperty(ref _currentNavigationItem, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public ParameterizedRelayCommand NavigateCommand { get; }
    public AsyncRelayCommand OpenCloudDisconnectCommand { get; }

    public async Task RefreshCloudConnectionStatusAsync()
    {
        if (_authenticationLauncher is null)
        {
            return;
        }

        var states = await Task.WhenAll(Enum.GetValues<CloudProviderType>().Select(async provider =>
        {
            try
            {
                return (Provider: provider, IsConnected: await _authenticationLauncher.IsConnectedAsync(provider));
            }
            catch
            {
                return (Provider: provider, IsConnected: false);
            }
        }));
        _cloudConnectionState.Update(states.Where(state => state.IsConnected).Select(state => state.Provider));
    }

    public async Task InitializeTrialAsync(CancellationToken cancellationToken = default)
    {
        IsTrialActive = false;
        TrialStatusText = "Checking trial";
        if (_trialService is null)
        {
            TrialStatusText = "Trial service unavailable";
            StatusMessage = "Trial status could not be verified. Features are locked.";
            return;
        }

        try
        {
            _trialDeviceIdentifier = _getDeviceIdentifier();
            _trialUserId = $"YASK-LOCAL-DEVICE-{_trialDeviceIdentifier}";
            var trialInfo = await _trialService.GetTrialStatusAsync(_trialUserId, _trialDeviceIdentifier, cancellationToken);
            trialInfo ??= await _trialService.StartTrialAsync(_trialUserId, _trialDeviceIdentifier, cancellationToken);
            ApplyTrialStatus(trialInfo);
        }
        catch (Exception exception)
        {
            IsTrialActive = false;
            TrialStatusText = "Trial status unavailable";
            StatusMessage = $"Trial verification failed; application options are locked. {exception.Message}";
        }
    }

    public async Task RefreshTrialStatusAsync(CancellationToken cancellationToken = default)
    {
        if (_trialService is null || _trialUserId is null || _trialDeviceIdentifier is null)
        {
            return;
        }

        try
        {
            var trialInfo = await _trialService.GetTrialStatusAsync(_trialUserId, _trialDeviceIdentifier, cancellationToken);
            if (trialInfo is null)
            {
                IsTrialActive = false;
                TrialExpiresAtUtc = null;
                TrialStatusText = "Trial status unavailable";
                StatusMessage = "Trial status could not be verified. Application options are locked.";
                return;
            }

            ApplyTrialStatus(trialInfo);
        }
        catch (Exception exception)
        {
            IsTrialActive = false;
            TrialStatusText = "Trial status unavailable";
            StatusMessage = $"Trial verification failed; application options are locked. {exception.Message}";
        }
    }

    private void ApplyTrialStatus(TrialInfo trialInfo)
    {
        TrialExpiresAtUtc = trialInfo.TrialEnd.ToUniversalTime();
        var remaining = (int)Math.Ceiling((TrialExpiresAtUtc.Value - DateTime.UtcNow).TotalDays);
        IsTrialActive = trialInfo.Status == LicenseStatus.Trial && remaining > 0;
        TrialStatusText = IsTrialActive
            ? $"Trial: {remaining} {(remaining == 1 ? "day" : "days")} left"
            : "Trial expired";
        if (!IsTrialActive)
        {
            StatusMessage = "Trial expired. Application options are locked.";
        }
    }

    private async Task DisconnectActiveCloudAsync()
    {
        Navigate(CloudAccountsNavigationItem);
        if (_navigation.CurrentViewModel is CloudAccountsViewModel cloudAccounts)
        {
            if (_cloudConnectionState.PendingProvider.HasValue)
            {
                var canceled = await cloudAccounts.CancelPendingConnectionAsync();
                StatusMessage = canceled
                    ? "Sign-in wait canceled. Close the provider terminal to stop its sign-in flow."
                    : cloudAccounts.ConnectionGuidance;
                return;
            }

            var disconnected = await cloudAccounts.DisconnectActiveCloudAsync();
            StatusMessage = disconnected ? "Cloud CLI session disconnected" : cloudAccounts.ConnectionGuidance;
        }
    }

    public async Task DisconnectCloudForInactivityAsync()
    {
        if (_authenticationLauncher is null)
        {
            return;
        }

        var connectedProviders = new List<CloudProviderType>();
        foreach (var provider in Enum.GetValues<CloudProviderType>())
        {
            try
            {
                if (await _authenticationLauncher.IsConnectedAsync(provider))
                {
                    connectedProviders.Add(provider);
                }
            }
            catch
            {
                // Preserve the current state when a provider CLI cannot be checked.
            }
        }

        if (connectedProviders.Count == 0)
        {
            _cloudConnectionState.Update(Array.Empty<CloudProviderType>());
            return;
        }

        var remainingProviders = new List<CloudProviderType>(connectedProviders);
        foreach (var provider in connectedProviders)
        {
            try
            {
                await _authenticationLauncher.DisconnectAsync(provider);
                remainingProviders.Remove(provider);
            }
            catch (Exception exception)
            {
                _cloudConnectionState.Update(remainingProviders);
                StatusMessage = $"Automatic {provider} sign-out failed: {exception.Message}";
                return;
            }
        }

        _cloudConnectionState.Update(remainingProviders);
        StatusMessage = "Cloud sessions disconnected after 5 minutes of inactivity";
    }

    public void Navigate(NavigationItem item)
    {
        var method = typeof(INavigationService).GetMethod(nameof(INavigationService.NavigateTo))!.MakeGenericMethod(item.ViewModelType);
        method.Invoke(_navigation, null);
    }

    private void SetSelectedNavigationItem(NavigationItem item)
    {
        if (CurrentNavigationItem is not null)
        {
            CurrentNavigationItem.IsSelected = false;
        }
        item.IsSelected = true;
        CurrentNavigationItem = item;
    }
}

public abstract class WorkspaceViewModel : ObservableObject
{
    private readonly IDemoDataService? _demoData;
    private bool _isLoading;
    private string? _errorMessage;
    private int _demoRecords;

    public abstract string Title { get; }
    public abstract string Subtitle { get; }
    public bool IsLoading { get => _isLoading; protected set => SetProperty(ref _isLoading, value); }
    public string? ErrorMessage { get => _errorMessage; protected set { if (SetProperty(ref _errorMessage, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasEmptyState => !IsLoading && !HasError;
    public int DemoRecords { get => _demoRecords; private set => SetProperty(ref _demoRecords, value); }
    public string DemoStatus => DemoRecords > 0 ? $"{DemoRecords} synthetic record(s) loaded from Demo Mode." : "No synthetic records loaded.";
    public RelayCommand RefreshCommand { get; }

    protected WorkspaceViewModel(IDemoDataService? demoData = null)
    {
        _demoData = demoData;
        if (_demoData != null)
        {
            _demoData.DataChanged += async (_, _) => await RefreshDemoAsync();
            _ = RefreshDemoAsync();
        }
        RefreshCommand = new RelayCommand(() => OnPropertyChanged(nameof(HasEmptyState)));
    }

    private async Task RefreshDemoAsync()
    {
        if (_demoData == null) return;
        var snapshot = await _demoData.GetSnapshotAsync();
        DemoRecords = Title switch
        {
            "Cloud Accounts" => snapshot.Accounts.Count,
            "Identities" => snapshot.Principals.Count,
            "Resources" => snapshot.Resources.Count,
            "Entitlements" => snapshot.Entitlements.Count,
            "Effective Access" => snapshot.EffectiveAccess.Count,
            "Risk Findings" => snapshot.RiskFindings.Count,
            "Recommendations" => snapshot.Recommendations.Count,
            "Scan History" => snapshot.Scans.Count,
            _ => 0
        };
        OnPropertyChanged(nameof(DemoStatus));
    }
}

public sealed class DashboardViewModel : WorkspaceViewModel
{
    private readonly IDemoDataService _demoData;
    private readonly IAnalyticsService _analytics;
    private readonly ILiveCloudScanService? _liveCloudScanService;
    private readonly IReportService? _reportService;
    private readonly IDialogService? _dialogService;
    private AnalyticsSnapshot _analyticsSnapshot = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, Array.Empty<AnalyticsMetric>(), Array.Empty<AnalyticsMetric>(), Array.Empty<AnalyticsMetric>(), Array.Empty<AnalyticsMetric>(), Array.Empty<AnalyticsMetric>());
    private LiveCloudScanResult? _liveScan;
    private IReadOnlyList<ScanAccountSelection> _availableAccounts = Array.Empty<ScanAccountSelection>();
    private bool _hasDiscoveredAccounts;
    private string _scanStatusMessage = "Connect a provider, then select Scan Now to fetch its data.";
    private string _accountSelectionMessage = "Discover accounts to choose the scope for this scan.";
    private CancellationTokenSource? _scanCancellation;
    private bool _isScanning;

    public DashboardViewModel(IDemoDataService demoData, IAnalyticsService analytics, ILiveCloudScanService? liveCloudScanService = null, IReportService? reportService = null, IDialogService? dialogService = null)
    {
        _demoData = demoData;
        _analytics = analytics;
        _liveCloudScanService = liveCloudScanService;
        _reportService = reportService;
        _dialogService = dialogService;
        ScanCommand = new AsyncRelayCommand(RunScanAsync);
        DiscoverAccountsCommand = new AsyncRelayCommand(DiscoverAccountsAsync);
        SelectAllAccountsCommand = new RelayCommand(() => SetAccountSelection(true));
        SelectNoAccountsCommand = new RelayCommand(() => SetAccountSelection(false));
        StopScanCommand = new RelayCommand(StopScan, () => IsScanning);
        GenerateReportCommand = new AsyncRelayCommand(GenerateReportAsync);
        _demoData.DataChanged += async (_, _) => await RefreshAsync();
        _ = RefreshAsync();
    }

    public override string Title => "Dashboard";
    public override string Subtitle => "A clear view of your cloud entitlement posture.";
    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand DiscoverAccountsCommand { get; }
    public RelayCommand SelectAllAccountsCommand { get; }
    public RelayCommand SelectNoAccountsCommand { get; }
    public RelayCommand StopScanCommand { get; }
    public AsyncRelayCommand GenerateReportCommand { get; }
    public IReadOnlyList<ScanAccountSelection> AvailableAccounts => _availableAccounts;
    public string AccountSelectionMessage { get => _accountSelectionMessage; private set => SetProperty(ref _accountSelectionMessage, value); }
    public bool HasAvailableAccounts => _availableAccounts.Count > 0;
    public bool HasPartialAccountSelection => _availableAccounts.Any(account => account.IsSelected) && _availableAccounts.Any(account => !account.IsSelected);
    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetProperty(ref _isScanning, value)) StopScanCommand.Refresh();
        }
    }
    public string ScanStatusMessage { get => _scanStatusMessage; private set => SetProperty(ref _scanStatusMessage, value); }
    public int CloudAccounts => _analyticsSnapshot.CloudAccounts + (_liveScan?.AccountCount ?? 0);
    public int Identities => _analyticsSnapshot.Identities + (_liveScan?.IdentityCount ?? 0);
    public int Resources => _analyticsSnapshot.Resources + (_liveScan?.ResourceCount ?? 0);
    public int Entitlements => _analyticsSnapshot.Entitlements + (_liveScan?.EffectiveAccessRecords.Count ?? 0);
    public int PrivilegedIdentities => _analyticsSnapshot.PrivilegedIdentities + (_liveScan?.PrivilegedIdentityCount ?? 0);
    public int RiskFindings => _analyticsSnapshot.RiskFindings + (_liveScan?.Findings.Count ?? 0);
    public int CriticalFindings => _analyticsSnapshot.CriticalFindings + (_liveScan?.Findings.Count(finding => finding.Severity == RiskSeverity.Critical) ?? 0);
    public int HighFindings => _analyticsSnapshot.HighFindings + (_liveScan?.Findings.Count(finding => finding.Severity == RiskSeverity.High) ?? 0);
    public int DormantIdentities => _analyticsSnapshot.DormantIdentities;
    public int ExternalIdentities => _analyticsSnapshot.ExternalIdentities;
    public string LastScan => (_liveScan is not null && (!_analyticsSnapshot.LastScan.HasValue || _liveScan.CompletedAt > _analyticsSnapshot.LastScan.Value)
        ? _liveScan.CompletedAt
        : _analyticsSnapshot.LastScan)?.ToLocalTime().ToString("g") ?? "No scans yet";
    public IReadOnlyList<AnalyticsMetric> FindingsByProvider => MergeMetrics(_analyticsSnapshot.FindingsByProvider, _liveScan?.Findings.GroupBy(finding => finding.CloudProvider.ToString()).Select(group => new AnalyticsMetric(group.Key, group.Count())));
    public IReadOnlyList<AnalyticsMetric> FindingsBySeverity => MergeMetrics(_analyticsSnapshot.FindingsBySeverity, _liveScan?.Findings.GroupBy(finding => finding.Severity.ToString()).Select(group => new AnalyticsMetric(group.Key, group.Count())));
    public IReadOnlyList<AnalyticsMetric> AccessByDecision => _analyticsSnapshot.AccessByDecision;

    public async Task DiscoverAccountsAsync()
    {
        if (_liveCloudScanService is null)
        {
            AccountSelectionMessage = "Live cloud scanning is not configured.";
            return;
        }

        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var previouslySelected = _availableAccounts.Where(account => account.IsSelected)
                .Select(account => account.Account.SelectionKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var accounts = await _liveCloudScanService.DiscoverAccountsAsync();
            _availableAccounts = accounts.Select(account => new ScanAccountSelection(account,
                !_hasDiscoveredAccounts || previouslySelected.Contains(account.SelectionKey))).ToArray();
            _hasDiscoveredAccounts = true;
            foreach (var account in _availableAccounts)
                account.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(ScanAccountSelection.IsSelected)) UpdateAccountSelectionMessage();
                };
            AccountSelectionMessage = _availableAccounts.Count == 0
                ? "No accounts are available. Confirm a provider CLI sign-in, then refresh accounts."
                : $"{_availableAccounts.Count} account/profile(s) discovered; {_availableAccounts.Count(account => account.IsSelected)} selected.";
            OnPropertyChanged(nameof(AvailableAccounts));
            OnPropertyChanged(nameof(HasAvailableAccounts));
            OnPropertyChanged(nameof(HasPartialAccountSelection));
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to discover scan accounts: {exception.Message}";
            AccountSelectionMessage = "Account discovery failed; no new account selection was applied.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void SetAccountSelection(bool isSelected)
    {
        foreach (var account in _availableAccounts) account.IsSelected = isSelected;
        UpdateAccountSelectionMessage();
    }

    private void UpdateAccountSelectionMessage()
    {
        AccountSelectionMessage = $"{_availableAccounts.Count} account/profile(s) discovered; {_availableAccounts.Count(account => account.IsSelected)} selected.";
        OnPropertyChanged(nameof(HasPartialAccountSelection));
    }

    public async Task GenerateReportAsync()
    {
        if (_reportService is null || _dialogService is null)
        {
            ErrorMessage = "Report generation is not configured.";
            return;
        }

        var outputPath = _dialogService.ChooseReportPath();
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }

        var reportType = Path.GetExtension(outputPath).TrimStart('.').ToLowerInvariant();
        if (reportType is not ("json" or "csv"))
        {
            ErrorMessage = "Choose a JSON or CSV report file.";
            return;
        }

        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var generatedPath = await _reportService.GenerateReportAsync(reportType, outputPath, _liveCloudScanService?.CurrentResult?.Findings);
            _dialogService.ShowInformation($"Report saved to:\n{generatedPath}");
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to generate the report: {exception.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task RunScanAsync()
    {
        if (_scanCancellation is not null) return;
        if (_liveCloudScanService is null)
        {
            ScanStatusMessage = "Live cloud scanning is not configured.";
            return;
        }

        var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        IsScanning = true;
        IsLoading = true;
        ErrorMessage = null;
        ScanStatusMessage = "Checking provider sign-ins and scanning available accounts...";
        try
        {
            IReadOnlySet<string>? selectedAccountKeys = null;
            if (_availableAccounts.Count > 0)
            {
                var selected = _availableAccounts.Where(account => account.IsSelected).Select(account => account.Account.SelectionKey).ToArray();
                if (selected.Length == 0)
                {
                    ScanStatusMessage = "Select at least one account before scanning.";
                    return;
                }
                if (selected.Length < _availableAccounts.Count)
                    selectedAccountKeys = selected.ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            _liveScan = await _liveCloudScanService.ScanAsync(selectedAccountKeys, cancellation.Token);
            ScanStatusMessage = FormatScanSummary(_liveScan);
            await RefreshAsync();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ScanStatusMessage = "Scan stopped. Results from the previous completed scan are retained.";
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Scan failed: {exception.Message}";
            ScanStatusMessage = "The scan did not complete.";
        }
        finally
        {
            _scanCancellation = null;
            cancellation.Dispose();
            IsScanning = false;
            IsLoading = false;
            OnPropertyChanged(nameof(ScanStatusMessage));
        }
    }

    private void StopScan() => _scanCancellation?.Cancel();

    private static string FormatScanSummary(LiveCloudScanResult scan)
    {
        var providers = string.Join(", ", scan.AccountRecords.Select(account => account.Provider).Distinct(StringComparer.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(providers)) providers = "connected providers";
        var totals = $"{scan.AccountCount} account(s), {scan.IdentityCount} identities, {scan.ResourceCount} resources, {scan.EffectiveAccessRecords.Count} effective-access assignments";
        var attentionCount = scan.Coverage.Count(item => item.Status is ScanCoverageStatus.Partial or ScanCoverageStatus.Denied or ScanCoverageStatus.Unavailable or ScanCoverageStatus.Unsupported or ScanCoverageStatus.Skipped);
        return attentionCount > 0
            ? $"Scan complete: {providers}; {totals}. {attentionCount} source(s) need attention. See Scan History for coverage details."
            : $"Scan complete: {providers}; {totals}. See Scan History for coverage details.";
    }

    private async Task RefreshAsync()
    {
        _analyticsSnapshot = _analytics.Calculate(await _demoData.GetSnapshotAsync());
        foreach (var property in new[] { nameof(CloudAccounts), nameof(Identities), nameof(Resources), nameof(Entitlements), nameof(PrivilegedIdentities), nameof(RiskFindings), nameof(CriticalFindings), nameof(HighFindings), nameof(DormantIdentities), nameof(ExternalIdentities), nameof(LastScan), nameof(FindingsByProvider), nameof(FindingsBySeverity), nameof(AccessByDecision) })
        {
            OnPropertyChanged(property);
        }
    }

    private static IReadOnlyList<AnalyticsMetric> MergeMetrics(IReadOnlyList<AnalyticsMetric> existing, IEnumerable<AnalyticsMetric>? additional)
    {
        var values = existing.Where(metric => metric.Value > 0)
            .Concat(additional ?? Array.Empty<AnalyticsMetric>())
            .GroupBy(metric => metric.Label)
            .Select(group => new AnalyticsMetric(group.Key, group.Sum(metric => metric.Value)))
            .OrderByDescending(metric => metric.Value)
            .ThenBy(metric => metric.Label)
            .ToArray();
        return values.Length == 0 ? new[] { new AnalyticsMetric("No findings", 0) } : values;
    }
}

public sealed class ScanAccountSelection : ObservableObject
{
    private bool _isSelected;

    public ScanAccountSelection(LiveScanAccountRecord account, bool isSelected)
    {
        Account = account;
        _isSelected = isSelected;
    }

    public LiveScanAccountRecord Account { get; }
    public string Provider => Account.Provider;
    public string AccountName => Account.AccountName;
    public string AccountId => Account.AccountId;
    public string Profile => Account.Profile;
    public string PermissionSet => Account.PermissionSet;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public sealed class CloudAccountsViewModel : WorkspaceViewModel
{
    private readonly ICloudAuthenticationLauncher _authenticationLauncher;
    private readonly IDialogService? _dialogService;
    private readonly CloudConnectionState _cloudConnectionState;
    private readonly ILiveCloudScanService? _liveCloudScanService;
    private CloudProviderType? _pendingProvider;
    private CancellationTokenSource? _pendingConnectCancellation;
    private TaskCompletionSource? _pendingConnectCompletion;

    public CloudAccountsViewModel(IDemoDataService demoData, ICloudAuthenticationLauncher? authenticationLauncher = null, IDialogService? dialogService = null, CloudConnectionState? cloudConnectionState = null, ILiveCloudScanService? liveCloudScanService = null) : base(demoData)
    {
        _authenticationLauncher = authenticationLauncher ?? new CloudAuthenticationLauncher();
        _dialogService = dialogService;
        _cloudConnectionState = cloudConnectionState ?? new CloudConnectionState();
        _liveCloudScanService = liveCloudScanService;
        _cloudConnectionState.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(CloudConnectionState.Status)) OnPropertyChanged(nameof(CloudConnectionStatus));
        };
        Providers =
        [
            new(CloudProviderType.Azure, "Azure", "Azure CLI", "az login", "Sign in with az. Entra/PIM needs tenant-approved Microsoft Graph read permissions.", new AsyncRelayCommand(() => ConnectAsync(CloudProviderType.Azure)), new AsyncRelayCommand(() => DisconnectProviderAsync(CloudProviderType.Azure))),
            new(CloudProviderType.Aws, "AWS", "AWS CLI", "aws configure sso", "Set up an AWS IAM Identity Center profile.", new AsyncRelayCommand(() => ConnectAsync(CloudProviderType.Aws)), new AsyncRelayCommand(() => DisconnectProviderAsync(CloudProviderType.Aws))),
            new(CloudProviderType.Gcp, "Google Cloud", "Google Cloud CLI", "gcloud auth login", "Sign in to your Google Cloud account in your browser.", new AsyncRelayCommand(() => ConnectAsync(CloudProviderType.Gcp)), new AsyncRelayCommand(() => DisconnectProviderAsync(CloudProviderType.Gcp)))
        ];
        RefreshConnectionsCommand = new AsyncRelayCommand(() => RefreshConnectionsAsync());
        if (_liveCloudScanService is not null)
        {
            _liveCloudScanService.ScanCompleted += (_, _) => OnPropertyChanged(nameof(DiscoveredAccounts));
        }
        _ = RefreshConnectionsAsync();
    }

    public override string Title => "Cloud Accounts";
    public override string Subtitle => "Connect Azure, AWS, or Google Cloud with your existing CLI identity.";
    public IReadOnlyList<CloudProviderConnectionOption> Providers { get; }
    public string ConnectionGuidance { get; private set; } = "Choose a provider to start its official sign-in flow in a separate terminal.";
    public string CloudConnectionStatus => _cloudConnectionState.Status;
    public IReadOnlyList<LiveScanAccountRecord> DiscoveredAccounts => _liveCloudScanService?.CurrentResult?.AccountRecords ?? Array.Empty<LiveScanAccountRecord>();
    public AsyncRelayCommand RefreshConnectionsCommand { get; }

    public async Task<bool> DisconnectActiveCloudAsync()
    {
        await RefreshConnectionsAsync();
        var connected = Providers.Where(provider => provider.IsConnected).ToArray();
        if (connected.Length == 0)
        {
            ConnectionGuidance = "No active cloud CLI sign-in was found.";
            OnPropertyChanged(nameof(ConnectionGuidance));
            return false;
        }

        if (connected.Length > 1)
        {
            ConnectionGuidance = "More than one provider is signed in. Disconnect the provider you want to remove from its card.";
            OnPropertyChanged(nameof(ConnectionGuidance));
            return false;
        }

        await DisconnectProviderAsync(connected[0].Provider);
        return !connected[0].IsConnected;
    }

    public async Task<bool> CancelPendingConnectionAsync()
    {
        var cancellation = _pendingConnectCancellation;
        var completion = _pendingConnectCompletion;
        if (_pendingProvider is null || cancellation is null || completion is null) return false;

        cancellation.Cancel();
        await completion.Task;
        return true;
    }

    public async Task ConnectAsync(CloudProviderType provider)
    {
        if (_pendingProvider.HasValue && _pendingProvider != provider)
        {
            ConnectionGuidance = $"Finish or cancel the {_pendingProvider} sign-in before choosing another provider.";
            OnPropertyChanged(nameof(ConnectionGuidance));
            return;
        }

        var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingProvider = provider;
        _pendingConnectCancellation = cancellation;
        _pendingConnectCompletion = completion;
        try
        {
            UpdateProviderStates(Providers.Where(option => option.IsConnected).Select(option => option.Provider).ToHashSet());
            await RefreshConnectionsAsync(cancellation.Token);
            if (Providers.Any(option => option.Provider == provider && option.IsConnected))
            {
                ConnectionGuidance = $"{provider} is already connected. Disconnect it before selecting another provider.";
                OnPropertyChanged(nameof(ConnectionGuidance));
                return;
            }
            if (Providers.Any(option => option.Provider != provider && option.IsConnected))
            {
                ConnectionGuidance = "Disconnect the active provider before signing in to another cloud.";
                OnPropertyChanged(nameof(ConnectionGuidance));
                return;
            }

            UpdateProviderStates(Providers.Where(option => option.IsConnected).Select(option => option.Provider).ToHashSet());
            _authenticationLauncher.LaunchAuthentication(provider);
            ConnectionGuidance = $"Complete {provider} sign-in in the terminal. Other providers stay disabled until sign-in is verified.";
            ErrorMessage = null;
            OnPropertyChanged(nameof(ConnectionGuidance));

            for (var attempt = 0; attempt < 90; attempt++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (await _authenticationLauncher.IsConnectedAsync(provider, cancellation.Token))
                {
                    await RefreshConnectionsAsync();
                    ConnectionGuidance = $"{provider} is connected. Go to Dashboard and select Scan Now to fetch resources, assignments, and findings. Disconnect it before selecting another provider. YASK-CIEM does not receive or store cloud credentials.";
                    OnPropertyChanged(nameof(ConnectionGuidance));
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(2), cancellation.Token);
            }

            ConnectionGuidance = "Sign-in was not detected. Check the provider terminal and try again.";
            OnPropertyChanged(nameof(ConnectionGuidance));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ConnectionGuidance = "Sign-in wait canceled. Close the provider terminal to stop its sign-in flow.";
            OnPropertyChanged(nameof(ConnectionGuidance));
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to connect to {provider}: {exception.Message}";
            ConnectionGuidance = "The provider sign-in could not be verified.";
            OnPropertyChanged(nameof(ConnectionGuidance));
        }
        finally
        {
            if (ReferenceEquals(_pendingConnectCancellation, cancellation))
            {
                _pendingProvider = null;
                _pendingConnectCancellation = null;
                _pendingConnectCompletion = null;
                cancellation.Dispose();
                await RefreshConnectionsAsync();
                completion.TrySetResult();
            }
        }
    }

    public async Task DisconnectProviderAsync(CloudProviderType provider)
    {
        var providerOption = Providers.Single(item => item.Provider == provider);
        var confirmation = $"Sign out of {providerOption.Name} in its shared CLI session/cache? This can affect other tools using those credentials.";
        if (_dialogService?.Confirm(confirmation, $"Disconnect {providerOption.Name}") != true)
        {
            return;
        }

        try
        {
            await _authenticationLauncher.DisconnectAsync(provider);
            await RefreshConnectionsAsync();
            ConnectionGuidance = $"{providerOption.Name} is disconnected. You can now connect to another provider.";
            ErrorMessage = null;
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to disconnect {providerOption.Name}: {exception.Message}";
        }

        OnPropertyChanged(nameof(ConnectionGuidance));
    }

    private async Task RefreshConnectionsAsync(CancellationToken cancellationToken = default)
    {
        var states = await Task.WhenAll(Providers.Select(async option =>
        {
            try
            {
                return (option.Provider, IsConnected: await _authenticationLauncher.IsConnectedAsync(option.Provider, cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return (option.Provider, IsConnected: false);
            }
        }));

        UpdateProviderStates(states.Where(state => state.IsConnected).Select(state => state.Provider).ToHashSet());
    }

    private void UpdateProviderStates(IReadOnlySet<CloudProviderType> connectedProviders)
    {
        foreach (var option in Providers)
        {
            var isConnected = connectedProviders.Contains(option.Provider);
            var anotherProviderConnected = connectedProviders.Any(provider => provider != option.Provider);
            option.UpdateState(isConnected, !isConnected && !anotherProviderConnected && (_pendingProvider is null || _pendingProvider == option.Provider), _pendingProvider == option.Provider);
        }

        _cloudConnectionState.Update(connectedProviders, _pendingProvider);
    }
}

public sealed class CloudProviderConnectionOption : ObservableObject
{
    private bool _isConnected;
    private bool _canConnect;
    private bool _isConnecting;

    public CloudProviderConnectionOption(CloudProviderType provider, string name, string cliName, string command, string description, AsyncRelayCommand connectCommand, AsyncRelayCommand disconnectCommand)
    {
        Provider = provider;
        Name = name;
        CliName = cliName;
        Command = command;
        Description = description;
        ConnectCommand = connectCommand;
        DisconnectCommand = disconnectCommand;
    }

    public CloudProviderType Provider { get; }
    public string Name { get; }
    public string CliName { get; }
    public string Command { get; }
    public string Description { get; }
    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand DisconnectCommand { get; }
    public bool IsConnected { get => _isConnected; private set => SetProperty(ref _isConnected, value); }
    public bool CanConnect { get => _canConnect; private set => SetProperty(ref _canConnect, value); }
    public bool IsConnecting { get => _isConnecting; private set => SetProperty(ref _isConnecting, value); }
    public string StateText => IsConnecting ? "Connecting" : IsConnected ? "Connected" : "Available";

    internal void UpdateState(bool isConnected, bool canConnect, bool isConnecting)
    {
        IsConnected = isConnected;
        CanConnect = canConnect;
        IsConnecting = isConnecting;
        OnPropertyChanged(nameof(StateText));
    }
}
public sealed class IdentitiesViewModel : WorkspaceViewModel
{
    private readonly ILiveCloudScanService? _liveCloudScanService;
    private string _searchText = string.Empty;

    public IdentitiesViewModel(IDemoDataService demoData, ILiveCloudScanService? liveCloudScanService = null) : base(demoData)
    {
        _liveCloudScanService = liveCloudScanService;
        if (_liveCloudScanService is not null)
        {
            _liveCloudScanService.ScanCompleted += (_, _) =>
            {
                OnPropertyChanged(nameof(Identities));
                OnPropertyChanged(nameof(HasResults));
            };
        }
    }

    public override string Title => "Identities";
    public override string Subtitle => "Review users, groups, service identities, and privilege signals.";
    public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) { OnPropertyChanged(nameof(Identities)); OnPropertyChanged(nameof(HasResults)); } } }
    public IReadOnlyList<CloudPrincipal> Identities => (_liveCloudScanService?.CurrentResult?.IdentityRecords ?? Array.Empty<CloudPrincipal>())
        .Where(identity => string.IsNullOrWhiteSpace(SearchText)
            || identity.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || (identity.Email?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false)
            || (identity.PrincipalId?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false))
        .ToArray();
    public bool HasResults => Identities.Count > 0;
}

public sealed class ResourcesViewModel : WorkspaceViewModel
{
    private readonly ILiveCloudScanService? _liveCloudScanService;
    private string _searchText = string.Empty;

    public ResourcesViewModel(IDemoDataService demoData, ILiveCloudScanService? liveCloudScanService = null) : base(demoData)
    {
        _liveCloudScanService = liveCloudScanService;
        if (_liveCloudScanService is not null)
        {
            _liveCloudScanService.ScanCompleted += (_, _) =>
            {
                OnPropertyChanged(nameof(Resources));
                OnPropertyChanged(nameof(HasResults));
            };
        }
    }

    public override string Title => "Resources";
    public override string Subtitle => "Search normalized cloud resources by provider and scope.";
    public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) { OnPropertyChanged(nameof(Resources)); OnPropertyChanged(nameof(HasResults)); } } }
    public IReadOnlyList<CloudResource> Resources => (_liveCloudScanService?.CurrentResult?.ResourceRecords ?? Array.Empty<CloudResource>())
        .Where(resource => string.IsNullOrWhiteSpace(SearchText)
            || resource.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || resource.Type.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || (resource.ResourceId?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false)
            || (resource.Scope?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false))
        .ToArray();
    public bool HasResults => Resources.Count > 0;
}
public sealed record EntitlementGridRow(string Provider, string Subject, string Grant, string Scope, string Assignment, string Source);
public sealed record EffectiveAccessGridRow(string Provider, string Principal, string Role, string Permission, string Resource, string Decision, string Path, string Source, string LastUsed, string LastChanged, bool Privileged);
public sealed record PermissionSimulatorOption(Guid Id, string Label);
public sealed record PermissionSimulationRow(string Provider, string Principal, string Resource, string Permission, string Decision, string EvaluationStatus, string Source, string Evidence, string Diagnostics);
public sealed record RiskFindingGridRow(string Provider, string Severity, string Title, string Principal, string Resource, string Evidence, string Recommendation);
public sealed record RecommendationGridRow(RecommendationModel? Model, string Type, string Title, string Provider, string State, string Status, string Current, string Suggested, string Priority, string Impact, string Reason, string Evidence);
public sealed record ScanHistoryGridRow(string Provider, DateTime StartedAt, DateTime? CompletedAt, string Status, int Identities, int Items, int Permissions, int Findings, int EffectiveAccess);
public sealed record AccessChangeTimelineRow(string Provider, string Change, DateTime DetectedAt, DateTime? ProviderChangedAt, string ChangedBy, string Principal, string Assignment, string Scope, string PreviousState, string CurrentState, string Source);
public sealed record ScanCoverageGridRow(string Provider, string DataSource, string Status, int Records, string Details);
public sealed record PostureTrendGridRow(DateTime CompletedAt, string Provider, int Accounts, int Identities, int Resources, int Permissions, int Findings, int Critical, int High, int EffectiveAccess, int Added, int Removed, int Changed);
public sealed record PostureDriftGridRow(DateTime DetectedAt, string Provider, string Type, string EntityType, string Severity, string Summary, bool AlertEligible);

public sealed class EntitlementsViewModel : WorkspaceViewModel
{
    private readonly IDemoDataService _demoData;
    private readonly ILiveCloudScanService? _liveCloudScanService;
    private DemoSnapshot? _snapshot;
    private string _searchText = string.Empty;

    public EntitlementsViewModel(IDemoDataService demoData, ILiveCloudScanService? liveCloudScanService = null) : base(demoData)
    {
        _demoData = demoData;
        _liveCloudScanService = liveCloudScanService;
        _demoData.DataChanged += async (_, _) => await RefreshAsync();
        if (_liveCloudScanService is not null)
        {
            _liveCloudScanService.ScanCompleted += async (_, _) => await RefreshAsync();
        }
        _ = RefreshAsync();
    }

    public override string Title => "Entitlements";
    public override string Subtitle => "Review role and permission assignments, plus discovered provider inventory.";
    public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) { OnPropertyChanged(nameof(Entitlements)); OnPropertyChanged(nameof(HasResults)); } } }
    public IReadOnlyList<EntitlementGridRow> Entitlements
    {
        get
        {
            var snapshot = _snapshot;
            var accounts = snapshot?.Accounts.ToDictionary(item => item.Id) ?? new Dictionary<Guid, CloudAccount>();
            var principals = snapshot?.Principals.ToDictionary(item => item.Id) ?? new Dictionary<Guid, CloudPrincipal>();
            var roles = snapshot?.Roles.ToDictionary(item => item.Id) ?? new Dictionary<Guid, CloudRole>();
            var permissions = snapshot?.Permissions.ToDictionary(item => item.Id) ?? new Dictionary<Guid, CloudPermission>();
            var resources = snapshot?.Resources.ToDictionary(item => item.Id) ?? new Dictionary<Guid, CloudResource>();
            var rows = (snapshot?.Entitlements ?? Array.Empty<Entitlement>()).Select(item => new EntitlementGridRow(
                accounts.GetValueOrDefault(item.CloudAccountId)?.CloudProvider.ToString() ?? "Demo",
                principals.GetValueOrDefault(item.PrincipalId)?.Name ?? WorkspaceDataFormatting.ShortId(item.PrincipalId),
                string.Join(" / ", new[]
                {
                    item.RoleId is Guid roleId ? roles.GetValueOrDefault(roleId)?.Name : null,
                    permissions.GetValueOrDefault(item.PermissionId)?.Name
                }.Where(value => !string.IsNullOrWhiteSpace(value))),
                resources.GetValueOrDefault(item.ResourceId ?? Guid.Empty)?.Name ?? item.Scope ?? "Account scope",
                item.AssignmentType.ToString(),
                "Demo Mode"));
            var live = _liveCloudScanService?.CurrentResult;
            rows = rows.Concat((live?.EffectiveAccessRecords ?? Array.Empty<LiveEffectiveAccessRecord>()).Select(item => new EntitlementGridRow(
                item.Provider, item.Principal, item.Role, item.Scope, item.AssignmentType, item.Source)));
            rows = rows.Concat((live?.RoleRecords ?? Array.Empty<DiscoveredCloudRole>()).Select(item => new EntitlementGridRow(
                item.Provider.ToString(), "Not mapped by provider scan", $"Role: {item.Role.Name}", item.Role.Scope ?? "Provider scope", "Inventory only", "Live scan")));
            rows = rows.Concat((live?.PermissionRecords ?? Array.Empty<DiscoveredCloudPermission>()).Select(item => new EntitlementGridRow(
                item.Provider.ToString(), "Not mapped by provider scan", $"Permission: {item.Permission.Name} ({item.Permission.Action ?? "action not supplied"})", item.Permission.Scope ?? "Provider scope", "Inventory only", "Live scan")));
            return rows.Where(row => WorkspaceDataFormatting.Matches(SearchText, row.Provider, row.Subject, row.Grant, row.Scope, row.Assignment, row.Source)).ToArray();
        }
    }
    public bool HasResults => Entitlements.Count > 0;

    public async Task RefreshAsync()
    {
        try
        {
            _snapshot = await _demoData.GetSnapshotAsync();
            OnPropertyChanged(nameof(Entitlements));
            OnPropertyChanged(nameof(HasResults));
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to load entitlements: {exception.Message}";
        }
    }
}

public sealed class EffectiveAccessViewModel : WorkspaceViewModel
{
    private readonly IDemoDataService _demoData;
    private readonly ILiveScanHistoryStore? _liveScanHistoryStore;
    private readonly ILiveCloudScanService? _liveCloudScanService;
    private readonly CloudConnectionState? _cloudConnectionState;
    private readonly IEffectiveAccessEngine _effectiveAccessEngine;
    private DemoSnapshot? _snapshot;
    private IReadOnlyList<LiveScanHistoryEntry> _liveHistory = Array.Empty<LiveScanHistoryEntry>();
    private string _searchText = string.Empty;
    private Guid? _simulatorPrincipalId;
    private Guid? _simulatorResourceId;
    private Guid? _simulatorPermissionId;
    private PermissionSimulationRow? _simulationResult;
    private string _simulationStatus = "Load normalized data to run a permission simulation.";
    private bool _isSimulating;

    public EffectiveAccessViewModel(IDemoDataService demoData, ILiveCloudScanService? liveCloudScanService = null, ILiveScanHistoryStore? liveScanHistoryStore = null, CloudConnectionState? cloudConnectionState = null, IEffectiveAccessEngine? effectiveAccessEngine = null) : base(demoData)
    {
        _demoData = demoData;
        _liveScanHistoryStore = liveScanHistoryStore;
        _liveCloudScanService = liveCloudScanService;
        _cloudConnectionState = cloudConnectionState;
        _effectiveAccessEngine = effectiveAccessEngine ?? new EffectiveAccessEngine();
        SimulatePermissionCommand = new AsyncRelayCommand(SimulatePermissionAsync);
        _demoData.DataChanged += async (_, _) => await RefreshAsync();
        if (liveCloudScanService is not null)
        {
            liveCloudScanService.ScanCompleted += async (_, _) => await RefreshAsync();
        }
        _ = RefreshAsync();
    }

    public override string Title => "Effective Access";
    public override string Subtitle => "Trace modeled direct and inherited access paths.";
    public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) { OnPropertyChanged(nameof(AccessRecords)); OnPropertyChanged(nameof(HasResults)); } } }
    public Guid? SimulatorPrincipalId { get => _simulatorPrincipalId; set { if (SetProperty(ref _simulatorPrincipalId, value)) { OnPropertyChanged(nameof(SimulatorResources)); OnPropertyChanged(nameof(SimulatorPermissions)); OnPropertyChanged(nameof(CanSimulatePermission)); SimulationResult = null; } } }
    public Guid? SimulatorResourceId { get => _simulatorResourceId; set { if (SetProperty(ref _simulatorResourceId, value)) { OnPropertyChanged(nameof(CanSimulatePermission)); SimulationResult = null; } } }
    public Guid? SimulatorPermissionId { get => _simulatorPermissionId; set { if (SetProperty(ref _simulatorPermissionId, value)) { OnPropertyChanged(nameof(CanSimulatePermission)); SimulationResult = null; } } }
    public PermissionSimulationRow? SimulationResult { get => _simulationResult; private set { if (SetProperty(ref _simulationResult, value)) OnPropertyChanged(nameof(HasSimulationResult)); } }
    public string SimulationStatus { get => _simulationStatus; private set => SetProperty(ref _simulationStatus, value); }
    public bool IsSimulating { get => _isSimulating; private set { if (SetProperty(ref _isSimulating, value)) OnPropertyChanged(nameof(CanSimulatePermission)); } }
    public bool CanSimulatePermission => !IsSimulating && _demoData.IsDemoMode && _snapshot is { Entitlements.Count: > 0 } && SimulatorPrincipalId.HasValue && SimulatorResourceId.HasValue && SimulatorPermissionId.HasValue;
    public bool HasSimulationResult => SimulationResult is not null;
    public string SimulatorCoverageMessage => _demoData.IsDemoMode
        ? "Evaluates the normalized local snapshot. Conditional grants return Conditional unless their runtime condition is evaluable."
        : "Live CLI scans currently report assignments but do not provide normalized principal-to-permission grants. Load Demo Mode data to try the simulator; live simulation is unavailable until provider normalization is connected.";
    public IReadOnlyList<PermissionSimulatorOption> SimulatorPrincipals => (_snapshot?.Principals ?? Array.Empty<CloudPrincipal>())
        .Where(principal => principal.IdentityType != IdentityType.Group && HasSimulatorGrant(principal.Id))
        .Select(principal => new PermissionSimulatorOption(principal.Id, $"{principal.CloudProvider}: {principal.Name}"))
        .ToArray();
    public IReadOnlyList<PermissionSimulatorOption> SimulatorResources => (_snapshot?.Resources ?? Array.Empty<CloudResource>())
        .Where(resource => !SimulatorPrincipalId.HasValue || resource.AccountId == _snapshot?.Principals.FirstOrDefault(principal => principal.Id == SimulatorPrincipalId.Value)?.AccountId)
        .Select(resource => new PermissionSimulatorOption(resource.Id, resource.Name))
        .ToArray();
    public IReadOnlyList<PermissionSimulatorOption> SimulatorPermissions => (_snapshot?.Permissions ?? Array.Empty<CloudPermission>())
        .Where(permission => !SimulatorPrincipalId.HasValue || permission.AccountId == _snapshot?.Principals.FirstOrDefault(principal => principal.Id == SimulatorPrincipalId.Value)?.AccountId)
        .Select(permission => new PermissionSimulatorOption(permission.Id, $"{permission.Name} ({permission.Action ?? "action not supplied"})"))
        .ToArray();
    public AsyncRelayCommand SimulatePermissionCommand { get; }
    public string DataCoverageMessage => "Last used and last changed are separate provider-reported signals. Missing audit data remains unknown; conditional inheritance and explicit-deny evaluation are not simulated.";
    public IReadOnlyList<EffectiveAccessGridRow> AccessRecords
    {
        get
        {
            var snapshot = _snapshot;
            var principals = snapshot?.Principals.ToDictionary(item => item.Id) ?? new Dictionary<Guid, CloudPrincipal>();
            var roles = snapshot?.Roles.ToDictionary(item => item.Id) ?? new Dictionary<Guid, CloudRole>();
            var permissions = snapshot?.Permissions.ToDictionary(item => item.Id) ?? new Dictionary<Guid, CloudPermission>();
            var resources = snapshot?.Resources.ToDictionary(item => item.Id) ?? new Dictionary<Guid, CloudResource>();
            var demoRows = (snapshot?.EffectiveAccess ?? Array.Empty<CloudEntitle.Domain.Entities.EffectiveAccess>()).Select(item => new EffectiveAccessGridRow(
                item.Provider.ToString(),
                principals.GetValueOrDefault(item.PrincipalId)?.Name ?? WorkspaceDataFormatting.ShortId(item.PrincipalId),
                item.RoleId is Guid roleId ? roles.GetValueOrDefault(roleId)?.Name ?? WorkspaceDataFormatting.ShortId(roleId) : "Direct permission",
                permissions.GetValueOrDefault(item.PermissionId)?.Name ?? WorkspaceDataFormatting.ShortId(item.PermissionId),
                resources.GetValueOrDefault(item.ResourceId ?? Guid.Empty)?.Name ?? item.Scope ?? "Account scope",
                item.Decision.ToString(),
                item.IsInherited ? "Inherited" : item.IsDirect ? "Direct" : "Other",
                item.AccessSource ?? "Demo Mode model",
                "Not recorded",
                "Not recorded",
                item.IsPrivileged));
            var connectedProviders = _cloudConnectionState?.ConnectedProviders ?? new HashSet<CloudProviderType>();
            var liveAccess = _liveCloudScanService?.CurrentResult?.EffectiveAccessRecords;
            if (liveAccess is null)
            {
                var history = _liveHistory.AsEnumerable();
                if (connectedProviders.Count == 1)
                {
                    var connectedProvider = connectedProviders.Single().ToString();
                    history = history.Where(scan => scan.EffectiveAccess.Any(item => item.Provider.Equals(connectedProvider, StringComparison.OrdinalIgnoreCase)));
                }
                liveAccess = history.OrderByDescending(scan => scan.StartedAt).FirstOrDefault()?.EffectiveAccess ?? Array.Empty<LiveEffectiveAccessRecord>();
            }

            var liveRows = liveAccess.Select(item => new EffectiveAccessGridRow(
                item.Provider,
                item.Principal,
                item.Role,
                item.Role,
                item.Scope,
                item.AssignmentType,
                item.AssignmentType,
                item.Source,
                item.LastUsedAt?.ToLocalTime().ToString("g") ?? "Not reported",
                item.LastChangedAt?.ToLocalTime().ToString("g") ?? "Not reported",
                item.IsPrivileged));
            var rows = demoRows.Concat(liveRows);
            if (connectedProviders.Count == 1)
            {
                var connectedProvider = connectedProviders.Single().ToString();
                rows = rows.Where(row => row.Provider.Equals(connectedProvider, StringComparison.OrdinalIgnoreCase));
            }
            return rows
                .Where(row => WorkspaceDataFormatting.Matches(SearchText, row.Provider, row.Principal, row.Role, row.Permission, row.Resource, row.Decision, row.Path))
                .ToArray();
        }
    }
    public bool HasResults => AccessRecords.Count > 0;

    public async Task RefreshAsync()
    {
        try
        {
            _snapshot = await _demoData.GetSnapshotAsync();
            _liveHistory = _liveScanHistoryStore is null ? Array.Empty<LiveScanHistoryEntry>() : await _liveScanHistoryStore.GetRecentAsync();
            if (_snapshot.Entitlements.Count > 0)
            {
                SimulatorPrincipalId ??= SimulatorPrincipals.FirstOrDefault()?.Id;
                SimulatorResourceId ??= SimulatorResources.FirstOrDefault()?.Id;
                SimulatorPermissionId ??= SimulatorPermissions.FirstOrDefault()?.Id;
                SimulationStatus = $"Normalized snapshot ready: {_snapshot.Entitlements.Count} entitlement(s).";
            }
            else
            {
                SimulatorStatusForUnavailableSnapshot();
            }
            OnPropertyChanged(nameof(AccessRecords));
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(SimulatorPrincipals));
            OnPropertyChanged(nameof(SimulatorResources));
            OnPropertyChanged(nameof(SimulatorPermissions));
            OnPropertyChanged(nameof(SimulatorCoverageMessage));
            OnPropertyChanged(nameof(CanSimulatePermission));
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to load effective access: {exception.Message}";
        }
    }

    public async Task SimulatePermissionAsync()
    {
        if (!CanSimulatePermission || _snapshot is null) return;
        var account = _snapshot.Accounts.FirstOrDefault(item => item.Id == _snapshot.Principals.First(principal => principal.Id == SimulatorPrincipalId).AccountId);
        if (account is null)
        {
            SimulationStatus = "The selected principal has no account in the normalized snapshot.";
            SimulationResult = null;
            return;
        }

        IsSimulating = true;
        SimulationResult = null;
        try
        {
            var normalized = BuildNormalizedSnapshot(_snapshot);
            var request = new AccessEvaluationRequest(normalized, _snapshot.Resources, _snapshot.GroupMemberships, _snapshot.Principals,
                SimulatorPrincipalId, SimulatorResourceId, SimulatorPermissionId);
            var result = await _effectiveAccessEngine.ExplainAsync(request, SimulatorPrincipalId!.Value, SimulatorResourceId!.Value, SimulatorPermissionId!.Value);
            var principal = _snapshot.Principals.First(item => item.Id == SimulatorPrincipalId.Value);
            var resource = _snapshot.Resources.First(item => item.Id == SimulatorResourceId.Value);
            var permission = _snapshot.Permissions.First(item => item.Id == SimulatorPermissionId.Value);
            SimulationResult = new PermissionSimulationRow(
                account.CloudProvider.ToString(), principal.Name, resource.Name, permission.Name,
                result.Decision.ToString(), result.EvaluationStatus.ToString(), result.AccessSource,
                string.Join(" | ", result.Evidence.Select(evidence => $"{evidence.SourceType}: {evidence.SourceName ?? evidence.SourceId}")),
                string.Join(" | ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
            SimulationStatus = result.Decision == AccessDecision.NotApplicable
                ? "No applicable normalized grant was found; this is not a provider-side access denial."
                : $"Evaluated {result.ApplicableEntitlements.Count} normalized entitlement(s).";
        }
        catch (Exception exception)
        {
            SimulationStatus = $"Simulation failed: {exception.Message}";
        }
        finally
        {
            IsSimulating = false;
        }
    }

    private void SimulatorStatusForUnavailableSnapshot()
    {
        SimulationStatus = _demoData.IsDemoMode
            ? "No normalized entitlements are available in the current snapshot."
            : "No normalized entitlement dataset is loaded.";
    }

    private bool HasSimulatorGrant(Guid principalId)
    {
        if (_snapshot is null) return false;
        var reachablePrincipalIds = new HashSet<Guid> { principalId };
        var pending = new Stack<Guid>();
        pending.Push(principalId);
        while (pending.Count > 0)
        {
            var memberId = pending.Pop();
            foreach (var membership in _snapshot.GroupMemberships.Where(item => item.PrincipalId == memberId))
            {
                if (reachablePrincipalIds.Add(membership.GroupId)) pending.Push(membership.GroupId);
            }
        }
        return _snapshot.Entitlements.Any(entitlement => reachablePrincipalIds.Contains(entitlement.PrincipalId));
    }

    private static NormalizationResult BuildNormalizedSnapshot(DemoSnapshot snapshot)
    {
        var accounts = snapshot.Accounts.ToDictionary(account => account.Id);
        var normalizedPermissions = snapshot.Permissions.Select(permission =>
        {
            var provider = GetProvider(permission.AccountId);
            var evidence = new ProviderEvidence(provider, "SnapshotPermission", permission.Id.ToString(), permission.Name, null, permission.Description, permission.Scope, permission.Action, DateTime.UtcNow, new Dictionary<string, string>());
            var pattern = permission.Resource ?? "*";
            var patternType = pattern == "*" ? ResourcePatternType.Global : pattern.Contains('*') ? ResourcePatternType.Pattern : ResourcePatternType.Exact;
            return new NormalizedPermission(permission.Id, provider, permission.AccountId, permission.Service ?? "unknown", permission.Action ?? permission.Name, pattern, permission.AccessType, permission.Name, PermissionCategory.Unknown, patternType, new Dictionary<string, string>(), evidence);
        }).ToArray();

        var normalizedEntitlements = snapshot.Entitlements.Select(entitlement =>
        {
            var provider = GetProvider(entitlement.CloudAccountId);
            var account = accounts[entitlement.CloudAccountId];
            var permission = normalizedPermissions.FirstOrDefault(item => item.Id == entitlement.PermissionId);
            var scopeText = entitlement.Scope ?? account.ExternalId ?? account.Name;
            var inherited = entitlement.AssignmentType == AssignmentType.Inherited;
            var conditional = entitlement.AssignmentType == AssignmentType.Conditional;
            var unresolvedGroupGrant = entitlement.AssignmentType == AssignmentType.Group
                && !snapshot.GroupMemberships.Any(membership => membership.GroupId == entitlement.PrincipalId);
            var accessType = conditional ? NormalizedAccessType.Conditional
                : unresolvedGroupGrant ? NormalizedAccessType.Unknown
                : permission?.AccessType == AccessType.Deny ? NormalizedAccessType.Deny
                : permission?.AccessType == AccessType.Allow ? NormalizedAccessType.Allow
                : NormalizedAccessType.Unknown;
            var sourceId = entitlement.Id.ToString();
            var evidence = new ProviderEvidence(provider, "SnapshotEntitlement", sourceId, entitlement.Source, null, null, scopeText, entitlement.AssignmentType.ToString(), entitlement.GrantedAt, new Dictionary<string, string>());
            var scopeType = scopeText.Contains("project", StringComparison.OrdinalIgnoreCase) ? NormalizedScopeType.Project
                : scopeText.Contains("subscription", StringComparison.OrdinalIgnoreCase) ? NormalizedScopeType.Subscription
                : NormalizedScopeType.Unknown;
            var condition = conditional ? new NormalizedCondition("Runtime condition", "Condition details are not present in this snapshot.", "Unknown", null) : null;
            return new NormalizedEntitlement(entitlement.Id, provider, entitlement.CloudAccountId, entitlement.PrincipalId, entitlement.ResourceId,
                entitlement.RoleId, entitlement.PolicyId, entitlement.PermissionId,
                new NormalizedScope(provider, scopeType, scopeText, null, null, inherited), entitlement.AssignmentType, accessType,
                entitlement.AssignmentType == AssignmentType.Direct, inherited, conditional, condition,
                unresolvedGroupGrant ? NormalizationStatus.PartiallyNormalized : conditional ? NormalizationStatus.PartiallyNormalized : NormalizationStatus.Normalized,
                evidence, entitlement.GrantedAt, entitlement.ExpiresAt, permission?.ResourcePattern);
        }).ToArray();

            return new NormalizationResult(snapshot.Principals, snapshot.Roles, Array.Empty<CloudPolicy>(), snapshot.Permissions, normalizedEntitlements,
            Array.Empty<NormalizationWarning>(), Array.Empty<string>(), new(normalizedEntitlements.Length, normalizedEntitlements.Length,
                normalizedEntitlements.Count(item => item.Status == NormalizationStatus.PartiallyNormalized), 0, 0));

        CloudProvider GetProvider(Guid accountId) => accounts.TryGetValue(accountId, out var account) ? account.Provider : CloudProvider.Kubernetes;
    }
}

public sealed class RiskFindingsViewModel : WorkspaceViewModel
{
    private readonly IDemoDataService _demoData;
    private readonly ILiveCloudScanService? _liveCloudScanService;
    private DemoSnapshot? _snapshot;
    private string _searchText = string.Empty;

    public RiskFindingsViewModel(IDemoDataService demoData, ILiveCloudScanService? liveCloudScanService = null) : base(demoData)
    {
        _demoData = demoData;
        _liveCloudScanService = liveCloudScanService;
        _demoData.DataChanged += async (_, _) => await RefreshAsync();
        if (_liveCloudScanService is not null)
        {
            _liveCloudScanService.ScanCompleted += async (_, _) => await RefreshAsync();
        }
        _ = RefreshAsync();
    }

    public override string Title => "Risk Findings";
    public override string Subtitle => "Review findings with provider evidence and recommended actions.";
    public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) { OnPropertyChanged(nameof(Findings)); OnPropertyChanged(nameof(HasResults)); } } }
    public IReadOnlyList<RiskFindingGridRow> Findings
    {
        get
        {
            var snapshot = _snapshot;
            var principals = snapshot?.Principals.ToDictionary(item => item.Id) ?? new Dictionary<Guid, CloudPrincipal>();
            var resources = snapshot?.Resources.ToDictionary(item => item.Id) ?? new Dictionary<Guid, CloudResource>();
            var rows = (snapshot?.RiskFindings ?? Array.Empty<RiskFindingModel>()).Select(item => new RiskFindingGridRow(
                item.CloudProvider.ToString(), item.Severity.ToString(), item.Title,
                principals.GetValueOrDefault(item.PrincipalId)?.Name ?? WorkspaceDataFormatting.ShortId(item.PrincipalId),
                resources.GetValueOrDefault(item.ResourceId ?? Guid.Empty)?.Name ?? "Not specified",
                item.Evidence.Summary, item.Recommendation ?? "Review finding"));
            rows = rows.Concat((_liveCloudScanService?.CurrentResult?.Findings ?? Array.Empty<CloudEntitle.Domain.RiskFinding>()).Select(item => new RiskFindingGridRow(
                item.CloudProvider.ToString(), item.Severity.ToString(), item.Title,
                item.Principal, item.Resource, item.Evidence, item.Recommendation)));
            return rows.Where(row => WorkspaceDataFormatting.Matches(SearchText, row.Provider, row.Severity, row.Title, row.Principal, row.Resource, row.Evidence, row.Recommendation)).ToArray();
        }
    }
    public bool HasResults => Findings.Count > 0;

    public async Task RefreshAsync()
    {
        try
        {
            _snapshot = await _demoData.GetSnapshotAsync();
            OnPropertyChanged(nameof(Findings));
            OnPropertyChanged(nameof(HasResults));
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to load risk findings: {exception.Message}";
        }
    }
}
public sealed class RecommendationsViewModel : WorkspaceViewModel
{
    private readonly IRecommendationStore _store;
    private readonly ILiveCloudScanService? _liveCloudScanService;
    private readonly IDialogService? _dialogService;
    private string _searchText = string.Empty;
    private RecommendationGridRow? _selectedRecommendation;
    private string _exportStatusMessage = string.Empty;
    public RecommendationsViewModel(IDemoDataService demoData, IRecommendationStore store, ILiveCloudScanService? liveCloudScanService = null, IDialogService? dialogService = null) : base(demoData)
    {
        _store = store;
        _liveCloudScanService = liveCloudScanService;
        _dialogService = dialogService;
        RefreshRecommendationsCommand = new AsyncRelayCommand(LoadAsync);
        ExportDraftCommand = new AsyncRelayCommand(ExportDraftAsync);
        demoData.DataChanged += async (_, _) => await LoadAsync();
        if (_liveCloudScanService is not null)
        {
            _liveCloudScanService.ScanCompleted += async (_, _) => await LoadAsync();
        }
        _ = LoadAsync();
    }
    public override string Title => "Recommendations";
    public override string Subtitle => "Review evidence-backed least-privilege proposals before any future remediation.";
    public IReadOnlyList<RecommendationGridRow> Recommendations { get; private set; } = Array.Empty<RecommendationGridRow>();
    public bool HasResults => Recommendations.Count > 0;
    public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) RefreshRecommendationsCommand.Execute(null); } }
    public RecommendationGridRow? SelectedRecommendation
    {
        get => _selectedRecommendation;
        set
        {
            if (SetProperty(ref _selectedRecommendation, value))
            {
                OnPropertyChanged(nameof(CanExportDraft));
                ExportStatusMessage = string.Empty;
            }
        }
    }
    public bool CanExportDraft => _dialogService is not null && SelectedRecommendation?.Model is not null;
    public string ExportStatusMessage { get => _exportStatusMessage; private set => SetProperty(ref _exportStatusMessage, value); }
    public AsyncRelayCommand RefreshRecommendationsCommand { get; }
    public AsyncRelayCommand ExportDraftCommand { get; }
    public async Task RefreshAsync() => await LoadAsync();

    public async Task ExportDraftAsync()
    {
        var recommendation = SelectedRecommendation?.Model;
        if (recommendation is null || _dialogService is null) return;
        var path = _dialogService.ChoosePolicyDraftPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            ExportStatusMessage = "Draft export canceled.";
            return;
        }

        var draft = new
        {
            schemaVersion = 1,
            generatedAtUtc = DateTime.UtcNow,
            executable = false,
            approvalRequired = true,
            provider = recommendation.Provider.ToString(),
            proposalType = recommendation.RecommendationType.ToString(),
            title = recommendation.Title,
            evaluationState = recommendation.EvaluationState.ToString(),
            currentState = recommendation.CurrentState,
            proposedState = recommendation.SuggestedState,
            impactState = recommendation.ImpactState.ToString(),
            impactAssessment = recommendation.Impact,
            reason = recommendation.Reason,
            evidence = recommendation.Evidence
        };

        try
        {
            var json = JsonSerializer.Serialize(draft, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json);
            ExportStatusMessage = $"Review draft exported to {path}";
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to export policy draft: {exception.Message}";
        }
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var stored = await _store.GetAsync(SearchText);
            var rows = stored.Select(item => new RecommendationGridRow(
                item,
                item.RecommendationType.ToString(), item.Title, item.Provider.ToString(), item.EvaluationState.ToString(),
                item.Status.ToString(), item.CurrentState, item.SuggestedState, item.Priority.ToString(), item.Impact, item.Reason, item.Evidence ?? "No evidence recorded."));
            var live = (_liveCloudScanService?.CurrentResult?.Findings ?? Array.Empty<CloudEntitle.Domain.RiskFinding>())
                .Where(item => WorkspaceDataFormatting.Matches(SearchText, item.Title, item.Permission, item.Recommendation, item.Evidence))
                .Select(item => new RecommendationGridRow(
                    null, "Risk remediation", item.Title, item.CloudProvider.ToString(), "Review required", "New",
                    item.Permission, item.Recommendation, item.Severity.ToString(), "Impact not evaluated", "Live finding requires a normalized recommendation before policy review.", item.Evidence));
            Recommendations = rows.Concat(live).ToArray();
            if (SelectedRecommendation is not null && !Recommendations.Contains(SelectedRecommendation)) SelectedRecommendation = null;
            OnPropertyChanged(nameof(Recommendations));
            OnPropertyChanged(nameof(HasResults));
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to load recommendations: {exception.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}

public sealed class ScanHistoryViewModel : WorkspaceViewModel
{
    private readonly IDemoDataService _demoData;
    private readonly ILiveCloudScanService? _liveCloudScanService;
    private readonly ILiveScanHistoryStore? _liveScanHistoryStore;
    private DemoSnapshot? _snapshot;
    private IReadOnlyList<LiveScanHistoryEntry> _liveHistory = Array.Empty<LiveScanHistoryEntry>();
    private string _searchText = string.Empty;

    public ScanHistoryViewModel(IDemoDataService demoData, ILiveCloudScanService? liveCloudScanService = null, ILiveScanHistoryStore? liveScanHistoryStore = null) : base(demoData)
    {
        _demoData = demoData;
        _liveCloudScanService = liveCloudScanService;
        _liveScanHistoryStore = liveScanHistoryStore;
        _demoData.DataChanged += async (_, _) => await RefreshAsync();
        if (_liveCloudScanService is not null)
        {
            _liveCloudScanService.ScanCompleted += async (_, _) => await RefreshAsync();
        }
        _ = RefreshAsync();
    }

    public override string Title => "Scan History";
    public override string Subtitle => "Review scan history and access changes observed across retained live scans.";
    public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) { OnPropertyChanged(nameof(Scans)); OnPropertyChanged(nameof(HasResults)); OnPropertyChanged(nameof(AccessChanges)); OnPropertyChanged(nameof(HasAccessChanges)); OnPropertyChanged(nameof(Coverage)); OnPropertyChanged(nameof(HasCoverage)); OnPropertyChanged(nameof(PostureTrends)); OnPropertyChanged(nameof(DriftAlerts)); OnPropertyChanged(nameof(HasDriftAlerts)); } } }
    public IReadOnlyList<ScanHistoryGridRow> Scans
    {
        get
        {
            var accounts = _snapshot?.Accounts.ToDictionary(item => item.Id) ?? new Dictionary<Guid, CloudAccount>();
            var cutoff = DateTime.UtcNow.AddDays(-7);
            var rows = (_snapshot?.Scans ?? Array.Empty<Scan>()).Where(item => (item.CompletedAt ?? item.StartedAt) >= cutoff).Select(item => new ScanHistoryGridRow(
                accounts.GetValueOrDefault(item.CloudAccountId)?.CloudProvider.ToString() ?? "Demo",
                item.StartedAt, item.CompletedAt, item.Status.ToString(), item.IdentitiesDiscovered,
                item.ItemsDiscovered, item.PermissionsDiscovered, item.FindingsGenerated,
                _snapshot?.EffectiveAccess.Count(access => access.CloudAccountId == item.CloudAccountId) ?? 0));
            var live = _liveHistory.Select(item => new ScanHistoryGridRow(
                item.Provider, item.StartedAt, item.CompletedAt, "Completed", item.Identities,
                item.Accounts + item.Identities + item.Resources + item.Roles + item.Permissions,
                item.Permissions, item.Findings, item.EffectiveAccess.Count));
            rows = rows.Concat(live);
            return rows.Where(row => WorkspaceDataFormatting.Matches(SearchText, row.Provider, row.Status)).OrderByDescending(row => row.StartedAt).ToArray();
        }
    }
    public bool HasResults => Scans.Count > 0;
    public string AccessChangeCoverageMessage => "Local scan history is retained for seven days. Provider audit retention and permissions also limit attribution. Scan completion is the detection time; provider change time, actor, and audit source appear only when matching audit data is available. Empty provider results are not treated as proof of removal.";
    public IReadOnlyList<AccessChangeTimelineRow> AccessChanges => BuildAccessChanges();
    public bool HasAccessChanges => AccessChanges.Count > 0;
    public IReadOnlyList<ScanCoverageGridRow> Coverage
    {
        get
        {
            var latest = _liveHistory.OrderByDescending(scan => scan.CompletedAt).FirstOrDefault();
            return (latest?.Coverage ?? Array.Empty<ScanCoverageRecord>())
                .Select(item => new ScanCoverageGridRow(item.Provider, item.DataSource, item.Status.ToString(), item.Records, item.Details))
                .Where(row => WorkspaceDataFormatting.Matches(SearchText, row.Provider, row.DataSource, row.Status, row.Details))
                .ToArray();
        }
    }
    public bool HasCoverage => Coverage.Count > 0;
    public IReadOnlyList<PostureTrendGridRow> PostureTrends => _liveHistory
        .Where(scan => scan.PostureTrend is not null)
        .Select(scan => scan.PostureTrend!)
        .Where(trend => WorkspaceDataFormatting.Matches(SearchText, trend.Provider))
        .OrderByDescending(trend => trend.CompletedAt)
        .Select(trend => new PostureTrendGridRow(trend.CompletedAt, trend.Provider, trend.Accounts, trend.Identities, trend.Resources, trend.Permissions, trend.Findings, trend.CriticalFindings, trend.HighFindings, trend.EffectiveAccess, trend.Added, trend.Removed, trend.Changed))
        .ToArray();
    public IReadOnlyList<PostureDriftGridRow> DriftAlerts => _liveHistory
        .SelectMany(scan => scan.PostureChanges)
        .Where(change => WorkspaceDataFormatting.Matches(SearchText, change.Provider, change.Type.ToString(), change.EntityType, change.Severity, change.Summary))
        .OrderByDescending(change => change.DetectedAt)
        .Select(change => new PostureDriftGridRow(change.DetectedAt, change.Provider, change.Type.ToString(), change.EntityType, change.Severity, change.Summary, change.AlertEligible))
        .ToArray();
    public bool HasDriftAlerts => DriftAlerts.Count > 0;
    public string NotificationDeliveryMessage => _liveHistory.OrderByDescending(scan => scan.CompletedAt).FirstOrDefault()?.NotificationDelivery
        ?? "No posture notification status is recorded yet.";
    public string CoverageMessage => _liveHistory.Count == 0
        ? "Scan coverage is available after a live provider scan."
        : _liveHistory.OrderByDescending(scan => scan.CompletedAt).First().Coverage.Count == 0
            ? "This retained scan predates detailed coverage reporting. Run a new scan to see source permissions and gaps."
            : "Collected and NoData mean the source query succeeded. Denied, Unavailable, Unsupported, and Skipped identify gaps; zero findings alone do not prove complete coverage.";

    private IReadOnlyList<AccessChangeTimelineRow> BuildAccessChanges()
    {
        var rows = new List<AccessChangeTimelineRow>();
        var providers = _liveHistory.SelectMany(scan => scan.EffectiveAccess).Select(item => item.Provider).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            var snapshots = _liveHistory
                .Select(scan => (Scan: scan, Records: scan.EffectiveAccess.Where(item => item.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase)).ToArray()))
                .Where(snapshot => snapshot.Records.Length > 0)
                .OrderBy(snapshot => snapshot.Scan.CompletedAt)
                .ToArray();
            for (var index = 1; index < snapshots.Length; index++)
            {
                var previousScan = snapshots[index - 1];
                var currentScan = snapshots[index];
                var previous = previousScan.Records.GroupBy(AccessKey, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                var current = currentScan.Records.GroupBy(AccessKey, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

                foreach (var item in current)
                {
                    if (!previous.TryGetValue(item.Key, out var old))
                    {
                        rows.Add(ToTimelineRow("Added", currentScan.Scan, item.Value, "No matching grant in preceding comparable scan", FormatAccess(item.Value), item.Value.LastChangedAt, item.Value.LastChangedBy ?? "Not reported", item.Value.Source));
                    }
                    else if (old.IsPrivileged != item.Value.IsPrivileged
                        || old.Source != item.Value.Source
                        || old.LastChangedAt != item.Value.LastChangedAt
                        || old.LastChangedBy != item.Value.LastChangedBy)
                    {
                        rows.Add(ToTimelineRow("Changed", currentScan.Scan, item.Value, FormatAccess(old), FormatAccess(item.Value), item.Value.LastChangedAt, item.Value.LastChangedBy ?? "Not reported", item.Value.Source));
                    }
                }

                foreach (var item in previous.Where(item => !current.ContainsKey(item.Key)))
                {
                    rows.Add(ToTimelineRow("Removed", currentScan.Scan, item.Value, FormatAccess(item.Value), "Not present in comparable scan", null, "Not reported", $"Scan comparison; prior source: {item.Value.Source}"));
                }
            }
        }

        return rows
            .Where(row => WorkspaceDataFormatting.Matches(SearchText, row.Provider, row.Change, row.Principal, row.Assignment, row.Scope, row.ChangedBy, row.Source, row.PreviousState, row.CurrentState))
            .OrderByDescending(row => row.DetectedAt)
            .ThenBy(row => row.Provider, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        static string AccessKey(LiveEffectiveAccessRecord item) => string.Join("\u001f", item.Provider, item.Principal, item.Role, item.Scope, item.AssignmentType);
        static string FormatAccess(LiveEffectiveAccessRecord item) => $"{item.Role} ({item.AssignmentType}) at {item.Scope}";
        static AccessChangeTimelineRow ToTimelineRow(string change, LiveScanHistoryEntry scan, LiveEffectiveAccessRecord item, string before, string after, DateTime? changedAt, string changedBy, string source)
            => new(item.Provider, change, scan.CompletedAt, changedAt, changedBy, item.Principal, $"{item.Role} ({item.AssignmentType})", item.Scope, before, after, source);
    }

    public async Task RefreshAsync()
    {
        try
        {
            _snapshot = await _demoData.GetSnapshotAsync();
            _liveHistory = _liveScanHistoryStore is null ? Array.Empty<LiveScanHistoryEntry>() : await _liveScanHistoryStore.GetRecentAsync();
            OnPropertyChanged(nameof(Scans));
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(AccessChanges));
            OnPropertyChanged(nameof(HasAccessChanges));
            OnPropertyChanged(nameof(AccessChangeCoverageMessage));
            OnPropertyChanged(nameof(Coverage));
            OnPropertyChanged(nameof(HasCoverage));
            OnPropertyChanged(nameof(CoverageMessage));
            OnPropertyChanged(nameof(PostureTrends));
            OnPropertyChanged(nameof(DriftAlerts));
            OnPropertyChanged(nameof(HasDriftAlerts));
            OnPropertyChanged(nameof(NotificationDeliveryMessage));
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to load scan history: {exception.Message}";
        }
    }
}

public sealed class FeedbackViewModel : WorkspaceViewModel
{
    private readonly IUserFeedbackService _feedbackService;
    private string _feedbackText = string.Empty;
    private string _suggestionsText = string.Empty;
    private string _submissionMessage = string.Empty;

    public FeedbackViewModel(IUserFeedbackService feedbackService)
    {
        _feedbackService = feedbackService;
        SubmitCommand = new AsyncRelayCommand(SubmitAsync);
    }

    public override string Title => "Feedback";
    public override string Subtitle => "Share product feedback and ideas for improvement.";
    public string FeedbackText
    {
        get => _feedbackText;
        set
        {
            if (SetProperty(ref _feedbackText, value))
            {
                OnPropertyChanged(nameof(CanSubmit));
                SubmissionMessage = string.Empty;
            }
        }
    }
    public string SuggestionsText
    {
        get => _suggestionsText;
        set
        {
            if (SetProperty(ref _suggestionsText, value))
            {
                OnPropertyChanged(nameof(CanSubmit));
                SubmissionMessage = string.Empty;
            }
        }
    }
    public string SubmissionMessage { get => _submissionMessage; private set => SetProperty(ref _submissionMessage, value); }
    public bool CanSubmit => !IsLoading && !string.IsNullOrWhiteSpace(FeedbackText) && !string.IsNullOrWhiteSpace(SuggestionsText);
    public AsyncRelayCommand SubmitCommand { get; }

    public async Task SubmitAsync()
    {
        if (!CanSubmit)
        {
            return;
        }

        IsLoading = true;
        OnPropertyChanged(nameof(CanSubmit));
        ErrorMessage = null;
        SubmissionMessage = string.Empty;
        try
        {
            await _feedbackService.SubmitAsync(FeedbackText.Trim(), SuggestionsText.Trim());
            FeedbackText = string.Empty;
            SuggestionsText = string.Empty;
            SubmissionMessage = "Thank you. Your anonymous feedback was submitted on this device.";
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to submit feedback: {exception.Message}";
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(CanSubmit));
        }
    }
}

internal static class WorkspaceDataFormatting
{
    public static bool Matches(string search, params string?[] values) =>
        string.IsNullOrWhiteSpace(search) || values.Any(value => value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true);

    public static string ShortId(Guid id) => id.ToString("N")[..8];
}
public sealed class SettingsViewModel : WorkspaceViewModel
{
    private readonly IDemoDataService _demoData;
    private string _demoModeState = "Demo Mode is off";
    public SettingsViewModel(IDemoDataService demoData) { _demoData = demoData; LoadDemoDataCommand = new AsyncRelayCommand(LoadDemoDataAsync); ResetDemoDataCommand = new AsyncRelayCommand(ResetDemoDataAsync); _demoData.DataChanged += (_, _) => { DemoModeState = _demoData.IsDemoMode ? "DEMO MODE active: synthetic data only" : "Demo Mode is off"; }; }
    public override string Title => "Settings";
    public override string Subtitle => "Application, database, connection, and security preferences.";
    public string DataLocation => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public string DemoModeState { get => _demoModeState; private set => SetProperty(ref _demoModeState, value); }
    public AsyncRelayCommand LoadDemoDataCommand { get; }
    public AsyncRelayCommand ResetDemoDataCommand { get; }
    private async Task LoadDemoDataAsync() { IsLoading = true; ErrorMessage = null; try { await _demoData.LoadAsync(); DemoModeState = "DEMO MODE active: synthetic data only"; } catch { ErrorMessage = "Unable to load Demo Mode data."; } finally { IsLoading = false; } }
    private async Task ResetDemoDataAsync() { IsLoading = true; ErrorMessage = null; try { await _demoData.ResetAsync(); DemoModeState = "Demo Mode is off"; } catch { ErrorMessage = "Unable to reset Demo Mode data."; } finally { IsLoading = false; } }
}
