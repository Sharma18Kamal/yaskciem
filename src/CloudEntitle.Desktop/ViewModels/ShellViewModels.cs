using CloudEntitle.Desktop.Services;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Analytics.Models;
using CloudEntitle.Analytics.Services;

namespace CloudEntitle.Desktop.ViewModels;

public sealed record NavigationItem(string Title, string Icon, Type ViewModelType);

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private object _currentViewModel;
    private NavigationItem _currentNavigationItem;
    private string _statusMessage = "Ready";

    public MainWindowViewModel(INavigationService navigation)
    {
        _navigation = navigation;
        _navigation.CurrentViewModelChanged += (_, _) =>
        {
            CurrentViewModel = _navigation.CurrentViewModel;
            CurrentNavigationItem = NavigationItems!.FirstOrDefault(item => item.ViewModelType == CurrentViewModel.GetType()) ?? NavigationItems![0];
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
            new("Risk Findings", "!", typeof(RiskFindingsViewModel)),
            new("Recommendations", "✓", typeof(RecommendationsViewModel)),
            new("Scan History", "◷", typeof(ScanHistoryViewModel)),
            new("Settings", "⚙", typeof(SettingsViewModel))
        ];
        _currentViewModel = navigation.CurrentViewModel;
        _currentNavigationItem = NavigationItems[0];
        NavigateCommand = new ParameterizedRelayCommand(parameter =>
        {
            if (parameter is NavigationItem item)
            {
                Navigate(item);
            }
        });
    }

    public string ApplicationName => "CloudEntitle CIEM";
    public string ApplicationVersion => typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    public string DatabaseStatus => "Connected";
    public string CloudConnectionStatus => "Not connected";
    public IReadOnlyList<NavigationItem> NavigationItems { get; }
    public NavigationItem SettingsNavigationItem => NavigationItems[^1];
    public object CurrentViewModel { get => _currentViewModel; private set => SetProperty(ref _currentViewModel, value); }
    public NavigationItem CurrentNavigationItem { get => _currentNavigationItem; private set => SetProperty(ref _currentNavigationItem, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public ParameterizedRelayCommand NavigateCommand { get; }

    public void Navigate(NavigationItem item)
    {
        var method = typeof(INavigationService).GetMethod(nameof(INavigationService.NavigateTo))!.MakeGenericMethod(item.ViewModelType);
        method.Invoke(_navigation, null);
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
    private AnalyticsSnapshot _analyticsSnapshot = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, Array.Empty<AnalyticsMetric>(), Array.Empty<AnalyticsMetric>(), Array.Empty<AnalyticsMetric>(), Array.Empty<AnalyticsMetric>(), Array.Empty<AnalyticsMetric>());
    public DashboardViewModel(IDemoDataService demoData, IAnalyticsService analytics) { _demoData = demoData; _analytics = analytics; _demoData.DataChanged += async (_, _) => await RefreshAsync(); _ = RefreshAsync(); }
    public override string Title => "Dashboard";
    public override string Subtitle => "A clear view of your cloud entitlement posture.";
    public int CloudAccounts => _analyticsSnapshot.CloudAccounts;
    public int Identities => _analyticsSnapshot.Identities;
    public int Resources => _analyticsSnapshot.Resources;
    public int Entitlements => _analyticsSnapshot.Entitlements;
    public int PrivilegedIdentities => _analyticsSnapshot.PrivilegedIdentities;
    public int RiskFindings => _analyticsSnapshot.RiskFindings;
    public int CriticalFindings => _analyticsSnapshot.CriticalFindings;
    public int HighFindings => _analyticsSnapshot.HighFindings;
    public int DormantIdentities => _analyticsSnapshot.DormantIdentities;
    public int ExternalIdentities => _analyticsSnapshot.ExternalIdentities;
    public string LastScan => _analyticsSnapshot.LastScan?.ToLocalTime().ToString("g") ?? "No scans yet";
    public IReadOnlyList<AnalyticsMetric> FindingsByProvider => _analyticsSnapshot.FindingsByProvider;
    public IReadOnlyList<AnalyticsMetric> FindingsBySeverity => _analyticsSnapshot.FindingsBySeverity;
    public IReadOnlyList<AnalyticsMetric> AccessByDecision => _analyticsSnapshot.AccessByDecision;
    private async Task RefreshAsync() { _analyticsSnapshot = _analytics.Calculate(await _demoData.GetSnapshotAsync()); foreach (var property in new[] { nameof(CloudAccounts), nameof(Identities), nameof(Resources), nameof(Entitlements), nameof(PrivilegedIdentities), nameof(RiskFindings), nameof(CriticalFindings), nameof(HighFindings), nameof(DormantIdentities), nameof(ExternalIdentities), nameof(LastScan), nameof(FindingsByProvider), nameof(FindingsBySeverity), nameof(AccessByDecision) }) OnPropertyChanged(property); }
}
public sealed class CloudAccountsViewModel : WorkspaceViewModel { public CloudAccountsViewModel(IDemoDataService demoData) : base(demoData) { } public override string Title => "Cloud Accounts"; public override string Subtitle => "Connect read-only cloud inventory sources when ready."; }
public sealed class IdentitiesViewModel : WorkspaceViewModel { public IdentitiesViewModel(IDemoDataService demoData) : base(demoData) { } public override string Title => "Identities"; public override string Subtitle => "Review users, groups, service identities, and privilege signals."; }
public sealed class ResourcesViewModel : WorkspaceViewModel { public ResourcesViewModel(IDemoDataService demoData) : base(demoData) { } public override string Title => "Resources"; public override string Subtitle => "Search normalized cloud resources by provider and scope."; public string SearchText { get; set; } = string.Empty; }
public sealed class EntitlementsViewModel : WorkspaceViewModel { public EntitlementsViewModel(IDemoDataService demoData) : base(demoData) { } public override string Title => "Entitlements"; public override string Subtitle => "Understand which roles, policies, and permissions are granted."; }
public sealed class EffectiveAccessViewModel : WorkspaceViewModel { public EffectiveAccessViewModel(IDemoDataService demoData) : base(demoData) { } public override string Title => "Effective Access"; public override string Subtitle => "Trace direct and inherited access paths."; }
public sealed class RiskFindingsViewModel : WorkspaceViewModel { public RiskFindingsViewModel(IDemoDataService demoData) : base(demoData) { } public override string Title => "Risk Findings"; public override string Subtitle => "Evidence-backed findings will appear here after analysis is enabled."; }
public sealed class RecommendationsViewModel : WorkspaceViewModel
{
    private readonly IRecommendationStore _store;
    private string _searchText = string.Empty;
    public RecommendationsViewModel(IDemoDataService demoData, IRecommendationStore store) : base(demoData) { _store = store; RefreshRecommendationsCommand = new AsyncRelayCommand(LoadAsync); _ = LoadAsync(); }
    public override string Title => "Recommendations";
    public override string Subtitle => "Review evidence-backed least-privilege proposals before any future remediation.";
    public IReadOnlyList<RecommendationModel> Recommendations { get; private set; } = Array.Empty<RecommendationModel>();
    public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) RefreshRecommendationsCommand.Execute(null); } }
    public AsyncRelayCommand RefreshRecommendationsCommand { get; }
    private async Task LoadAsync() { IsLoading = true; ErrorMessage = null; try { Recommendations = await _store.GetAsync(SearchText); OnPropertyChanged(nameof(Recommendations)); } catch { ErrorMessage = "Unable to load recommendations."; } finally { IsLoading = false; } }
}
public sealed class ScanHistoryViewModel : WorkspaceViewModel { public ScanHistoryViewModel(IDemoDataService demoData) : base(demoData) { } public override string Title => "Scan History"; public override string Subtitle => "Review the history of completed and interrupted scans."; }
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
