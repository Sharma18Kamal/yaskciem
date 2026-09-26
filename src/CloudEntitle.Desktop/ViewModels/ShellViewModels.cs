using CloudEntitle.Desktop.Services;

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
    private bool _isLoading;
    private string? _errorMessage;

    public abstract string Title { get; }
    public abstract string Subtitle { get; }
    public bool IsLoading { get => _isLoading; protected set => SetProperty(ref _isLoading, value); }
    public string? ErrorMessage { get => _errorMessage; protected set { if (SetProperty(ref _errorMessage, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasEmptyState => !IsLoading && !HasError;
    public RelayCommand RefreshCommand { get; }

    protected WorkspaceViewModel()
    {
        RefreshCommand = new RelayCommand(() => OnPropertyChanged(nameof(HasEmptyState)));
    }
}

public sealed class DashboardViewModel : WorkspaceViewModel { public override string Title => "Dashboard"; public override string Subtitle => "A clear view of your cloud entitlement posture."; public int CloudAccounts => 0; public int Identities => 0; public int Resources => 0; public int Entitlements => 0; public int PrivilegedIdentities => 0; public int RiskFindings => 0; public int CriticalFindings => 0; public string LastScan => "No scans yet"; }
public sealed class CloudAccountsViewModel : WorkspaceViewModel { public override string Title => "Cloud Accounts"; public override string Subtitle => "Connect read-only cloud inventory sources when ready."; }
public sealed class IdentitiesViewModel : WorkspaceViewModel { public override string Title => "Identities"; public override string Subtitle => "Review users, groups, service identities, and privilege signals."; }
public sealed class ResourcesViewModel : WorkspaceViewModel { public override string Title => "Resources"; public override string Subtitle => "Search normalized cloud resources by provider and scope."; public string SearchText { get; set; } = string.Empty; }
public sealed class EntitlementsViewModel : WorkspaceViewModel { public override string Title => "Entitlements"; public override string Subtitle => "Understand which roles, policies, and permissions are granted."; }
public sealed class EffectiveAccessViewModel : WorkspaceViewModel { public override string Title => "Effective Access"; public override string Subtitle => "Trace direct and inherited access paths."; }
public sealed class RiskFindingsViewModel : WorkspaceViewModel { public override string Title => "Risk Findings"; public override string Subtitle => "Evidence-backed findings will appear here after analysis is enabled."; }
public sealed class RecommendationsViewModel : WorkspaceViewModel { public override string Title => "Recommendations"; public override string Subtitle => "Least-privilege recommendations will appear here."; }
public sealed class ScanHistoryViewModel : WorkspaceViewModel { public override string Title => "Scan History"; public override string Subtitle => "Review the history of completed and interrupted scans."; }
public sealed class SettingsViewModel : WorkspaceViewModel { public override string Title => "Settings"; public override string Subtitle => "Application, database, connection, and security preferences."; public string DataLocation => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); }
