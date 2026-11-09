using System.Text;
using CloudEntitle.Analytics.Models;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Integration.Contracts;
using Microsoft.Win32;
using Serilog;

namespace CloudEntitle.Desktop.ViewModels;

public sealed record PermissionUsageDisplayRow(
    string Provider,
    string Identity,
    string Permission,
    string Action,
    string Resource,
    string State30Days,
    string State60Days,
    string State90Days,
    int Actions30Days,
    int Actions60Days,
    int Actions90Days,
    string FirstUsedUtc,
    string LastUsedUtc,
    string Correlation);

public sealed record ActivityHistoryDisplayRow(
    string Provider,
    string Identity,
    string Action,
    string PermissionAction,
    string Resource,
    string Outcome,
    DateTimeOffset EventTimestamp,
    string Source);

public sealed class PermissionUsageViewModel : WorkspaceViewModel
{
    private readonly IPermissionUsageWorkspaceService _workspace;
    private readonly IDemoDataService _demoData;
    private IReadOnlyList<CloudAccount> _accounts = Array.Empty<CloudAccount>();
    private IReadOnlyList<PermissionUsageDisplayRow> _permissionRows = Array.Empty<PermissionUsageDisplayRow>();
    private IReadOnlyList<ActivityHistoryDisplayRow> _historyRows = Array.Empty<ActivityHistoryDisplayRow>();
    private IReadOnlyList<PermissionUsageTrend> _trendRows = Array.Empty<PermissionUsageTrend>();
    private IReadOnlyList<PermissionObservationWindow> _observationWindows = Array.Empty<PermissionObservationWindow>();
    private IReadOnlyList<UsageAnalyticsFindingRow> _findingRows = Array.Empty<UsageAnalyticsFindingRow>();
    private IReadOnlyList<UsageAnalyticsRecommendationRow> _recommendationRows = Array.Empty<UsageAnalyticsRecommendationRow>();
    private CloudAccount? _selectedAccount;
    private CloudPrincipal? _selectedIdentity;
    private string _searchText = string.Empty;
    private string _providerFilter = "All";
    private string _notice = "Select a saved account and analyze its local observations. Cloud collection occurs only after explicit consent.";
    private string _activityStatus = "Live collection has not been started.";
    private string _region = string.Empty;
    private string _profile = string.Empty;
    private string _destination = string.Empty;
    private string _container = string.Empty;
    private string _prefix = string.Empty;
    private int _selectedObservationDays = 90;
    private DateTime? _eventStartDate = DateTime.UtcNow.Date.AddDays(-90);
    private DateTime? _eventEndDate = DateTime.UtcNow.Date;
    private bool _liveCollectionConsent;
    private CloudActivitySource _selectedActivitySource;
    private IReadOnlyList<CloudPrincipal> _identities = Array.Empty<CloudPrincipal>();

    public PermissionUsageViewModel(IPermissionUsageWorkspaceService workspace, IDemoDataService demoData)
        : base(demoData)
    {
        _workspace = workspace;
        _demoData = demoData;
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync);
        CollectCommand = new AsyncRelayCommand(CollectAsync);
        RefreshAccountsCommand = new AsyncRelayCommand(RefreshAccountsAsync);
        LoadDemoCommand = new AsyncRelayCommand(LoadDemoAsync);
        ExportCommand = new RelayCommand(ExportCsv);
        _ = RefreshAccountsAsync();
    }

    public override string Title => "Permission Usage";
    public override string Subtitle => "Compare effective access with observed activity, evidence coverage, trends and review-only recommendations.";

    public IReadOnlyList<CloudAccount> Accounts { get => _accounts; private set => SetProperty(ref _accounts, value); }
    public IReadOnlyList<CloudPrincipal> Identities { get => _identities; private set => SetProperty(ref _identities, value); }
    public IReadOnlyList<PermissionUsageDisplayRow> PermissionRows => _permissionRows;
    public IReadOnlyList<PermissionUsageDisplayRow> FilteredPermissionRows => _permissionRows
        .Where(row => _providerFilter == "All" || row.Provider.Equals(_providerFilter, StringComparison.OrdinalIgnoreCase))
        .Where(row => _selectedIdentity is null || row.Identity == _selectedIdentity.Name)
        .Where(row => string.IsNullOrWhiteSpace(_searchText)
            || $"{row.Provider} {row.Identity} {row.Permission} {row.Action} {row.Resource} {row.Correlation}"
                .Contains(_searchText, StringComparison.OrdinalIgnoreCase))
        .ToArray();
    public IReadOnlyList<ActivityHistoryDisplayRow> FilteredHistoryRows => _historyRows
        .Where(row => _eventStartDate is null || row.EventTimestamp.UtcDateTime.Date >= _eventStartDate.Value.Date)
        .Where(row => _eventEndDate is null || row.EventTimestamp.UtcDateTime.Date <= _eventEndDate.Value.Date)
        .Where(row => _providerFilter == "All" || row.Provider.Equals(_providerFilter, StringComparison.OrdinalIgnoreCase))
        .Where(row => _selectedIdentity is null || row.Identity == _selectedIdentity.Name)
        .Where(row => string.IsNullOrWhiteSpace(_searchText)
            || $"{row.Provider} {row.Identity} {row.Action} {row.PermissionAction} {row.Resource} {row.Outcome} {row.Source}"
                .Contains(_searchText, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(row => row.EventTimestamp)
        .ToArray();
    public IReadOnlyList<PermissionUsageTrend> FilteredTrendRows => _trendRows
        .Where(row => _eventStartDate is null || row.BucketStartUtc.Date >= _eventStartDate.Value.Date)
        .Where(row => _eventEndDate is null || row.BucketStartUtc.Date <= _eventEndDate.Value.Date)
        .Where(row => _providerFilter == "All" || row.Provider.ToString().Equals(_providerFilter, StringComparison.OrdinalIgnoreCase))
        .Where(row => _selectedIdentity is null || row.IdentityId == _selectedIdentity.Id)
        .Where(row => string.IsNullOrWhiteSpace(_searchText)
            || $"{row.Provider} {row.BucketType} {row.BucketStartUtc:O}"
                .Contains(_searchText, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(row => row.BucketStartUtc)
        .ToArray();
    public IReadOnlyList<PermissionObservationWindow> ObservationWindows => _observationWindows;
    public IReadOnlyList<UsageAnalyticsFindingRow> FindingRows => _findingRows;
    public IReadOnlyList<UsageAnalyticsRecommendationRow> RecommendationRows => _recommendationRows;
    public IReadOnlyList<string> ProviderFilters { get; } = ["All", "Azure", "AWS", "GCP"];
    public IReadOnlyList<int> ObservationPeriods { get; } = [30, 60, 90];
    public IReadOnlyList<CloudActivitySource> ActivitySources => _selectedAccount?.CloudProvider switch
    {
        CloudProviderType.Aws => [CloudActivitySource.Default, CloudActivitySource.AwsCloudTrailLake, CloudActivitySource.AwsS3Archive],
        CloudProviderType.Azure => [CloudActivitySource.Default, CloudActivitySource.AzureLogAnalytics, CloudActivitySource.AzureBlobArchive],
        _ => [CloudActivitySource.Default]
    };

    public CloudAccount? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (!SetProperty(ref _selectedAccount, value)) return;
            SelectedIdentity = null;
            OnPropertyChanged(nameof(IsDemoAccount));
            OnPropertyChanged(nameof(ActivitySources));
            if (ActivitySources.Count > 0) SelectedActivitySource = ActivitySources[0];
        }
    }

    public CloudPrincipal? SelectedIdentity
    {
        get => _selectedIdentity;
        set
        {
            if (!SetProperty(ref _selectedIdentity, value)) return;
            RaiseFilterProperties();
        }
    }

    public string ProviderFilter
    {
        get => _providerFilter;
        set { if (SetProperty(ref _providerFilter, value)) RaiseFilterProperties(); }
    }

    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) RaiseFilterProperties(); }
    }

    public int SelectedObservationDays
    {
        get => _selectedObservationDays;
        set { if (SetProperty(ref _selectedObservationDays, value)) OnPropertyChanged(nameof(WindowLabel)); }
    }

    public DateTime? EventStartDate
    {
        get => _eventStartDate;
        set
        {
            if (!SetProperty(ref _eventStartDate, value)) return;
            OnPropertyChanged(nameof(FilteredHistoryRows));
            OnPropertyChanged(nameof(FilteredTrendRows));
        }
    }

    public DateTime? EventEndDate
    {
        get => _eventEndDate;
        set
        {
            if (!SetProperty(ref _eventEndDate, value)) return;
            OnPropertyChanged(nameof(FilteredHistoryRows));
            OnPropertyChanged(nameof(FilteredTrendRows));
        }
    }

    public string Region { get => _region; set => SetProperty(ref _region, value); }
    public string Profile { get => _profile; set => SetProperty(ref _profile, value); }
    public string Destination { get => _destination; set => SetProperty(ref _destination, value); }
    public string Container { get => _container; set => SetProperty(ref _container, value); }
    public string Prefix { get => _prefix; set => SetProperty(ref _prefix, value); }
    public string Notice { get => _notice; private set => SetProperty(ref _notice, value); }
    public string ActivityStatus { get => _activityStatus; private set => SetProperty(ref _activityStatus, value); }
    public bool LiveCollectionConsent { get => _liveCollectionConsent; set => SetProperty(ref _liveCollectionConsent, value); }
    public bool IsDemoAccount => SelectedAccount?.ExternalAccountId?.StartsWith("demo-", StringComparison.OrdinalIgnoreCase) == true;
    public string WindowLabel => $"Observation window: {SelectedObservationDays} days";
    public int TotalEffectivePermissions => _permissionRows.Count;
    public int UsedPermissions => _permissionRows.Count(row => row.State90Days == PermissionUsageEvidenceState.Used.ToString());
    public int NotObservedPermissions => _permissionRows.Count(row => row.State90Days == PermissionUsageEvidenceState.NotObserved.ToString());
    public int InconclusivePermissions => _permissionRows.Count(row => row.State90Days is nameof(PermissionUsageEvidenceState.InsufficientObservation)
        or nameof(PermissionUsageEvidenceState.UnknownMapping) or nameof(PermissionUsageEvidenceState.CollectionIncomplete));
    public string SourceNotice => Notice;
    public CloudActivitySource SelectedActivitySource
    {
        get => _selectedActivitySource;
        set => SetProperty(ref _selectedActivitySource, value);
    }
    public bool CanExport => _permissionRows.Count > 0;

    public AsyncRelayCommand AnalyzeCommand { get; }
    public AsyncRelayCommand CollectCommand { get; }
    public AsyncRelayCommand RefreshAccountsCommand { get; }
    public AsyncRelayCommand LoadDemoCommand { get; }
    public RelayCommand ExportCommand { get; }

    private async Task RefreshAccountsAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            Accounts = await _workspace.GetAccountsAsync(CancellationToken.None);
            SelectedAccount ??= Accounts.FirstOrDefault();
            Notice = Accounts.Count == 0
                ? "No saved cloud inventory found. Load Demo Mode data or run an inventory scan before analysis."
                : "Select an account to analyze saved local evidence. No live endpoint is contacted until you explicitly consent and collect.";
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Unable to load permission usage accounts");
            ErrorMessage = "Unable to load saved cloud accounts. Check the local database and application log.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task AnalyzeAsync()
    {
        if (SelectedAccount is null)
        {
            ErrorMessage = "Select a saved account before analyzing permission usage.";
            return;
        }
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var data = await _workspace.AnalyzeAsync(SelectedAccount.Id, SelectedObservationDays, CancellationToken.None);
            Identities = data.Principals;
            _permissionRows = data.Usage.Permissions.Select(item =>
            {
                var resource = item.ResourceId is { } resourceId
                    ? data.Resources.FirstOrDefault(candidate => candidate.Id == resourceId)?.Name ?? resourceId.ToString("D")
                    : "Any resource / account scope";
                return new PermissionUsageDisplayRow(
                    item.Provider.ToString(), data.Principals.FirstOrDefault(identity => identity.Id == item.PrincipalId)?.Name ?? item.PrincipalId.ToString("D"),
                    item.Permission, item.PermissionAction ?? item.Permission, resource,
                    item.Windows.Single(window => window.Days == 30).EvidenceState.ToString(),
                    item.Windows.Single(window => window.Days == 60).EvidenceState.ToString(),
                    item.Windows.Single(window => window.Days == 90).EvidenceState.ToString(),
                    item.Windows.Single(window => window.Days == 30).SuccessfulActionCount,
                    item.Windows.Single(window => window.Days == 60).SuccessfulActionCount,
                    item.Windows.Single(window => window.Days == 90).SuccessfulActionCount,
                    FormatTimestamp(item.FirstUsedAtUtc), FormatTimestamp(item.LastUsedAtUtc), item.CorrelationStatus);
            }).ToArray();
            _historyRows = data.Events.Select(item => new ActivityHistoryDisplayRow(
                item.Provider.ToString(), data.Principals.FirstOrDefault(identity => identity.Id == item.IdentityId)?.Name ?? item.ProviderIdentity,
                item.Action, item.PermissionAction ?? "Unknown", item.ResourceReference ?? "Unknown",
                item.Outcome, item.EventTimestamp, item.Source)).ToArray();
            _trendRows = data.Trends;
            _observationWindows = data.ObservationWindows;
            _findingRows = data.Findings;
            _recommendationRows = data.Recommendations;
            Notice = data.SourceNotice;
            EventStartDate = DateTime.UtcNow.Date.AddDays(-90);
            EventEndDate = DateTime.UtcNow.Date;
            OnPropertyChanged(nameof(PermissionRows));
            OnPropertyChanged(nameof(FilteredPermissionRows));
            OnPropertyChanged(nameof(FilteredHistoryRows));
            OnPropertyChanged(nameof(FilteredTrendRows));
            OnPropertyChanged(nameof(ObservationWindows));
            OnPropertyChanged(nameof(FindingRows));
            OnPropertyChanged(nameof(RecommendationRows));
            OnPropertyChanged(nameof(TotalEffectivePermissions));
            OnPropertyChanged(nameof(UsedPermissions));
            OnPropertyChanged(nameof(NotObservedPermissions));
            OnPropertyChanged(nameof(InconclusivePermissions));
            OnPropertyChanged(nameof(SourceNotice));
            OnPropertyChanged(nameof(CanExport));
            ActivityStatus = $"Analyzed {data.Usage.Permissions.Count} effective permission row(s), {data.Events.Count} stored audit event(s), and {data.Trends.Count} history bucket(s).";
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Permission usage analysis failed for account {CloudAccountId}", SelectedAccount.Id);
            ErrorMessage = "Permission usage analysis failed. Verify saved entitlement and observation evidence, then check the application log.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task CollectAsync()
    {
        if (SelectedAccount is null || IsDemoAccount)
        {
            ActivityStatus = "Select a non-demo saved account; synthetic demo accounts cannot be live collection targets.";
            return;
        }
        if (!LiveCollectionConsent)
        {
            ActivityStatus = "Explicit consent is required before a live audit-log read.";
            return;
        }
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var snapshot = await _workspace.CollectAndSaveAsync(SelectedAccount.Id, SelectedActivitySource,
                Region, Profile, Destination, Container, Prefix, CancellationToken.None);
            ActivityStatus = $"Read {snapshot.Events.Count} provider event(s) from {snapshot.Source}. {snapshot.CoverageDetails}";
            await AnalyzeAsync();
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Permission usage activity collection failed for account {CloudAccountId}", SelectedAccount.Id);
            ActivityStatus = "Collection failed. No successful snapshot was saved; verify provider configuration and read permissions, then check the application log.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadDemoAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            await _demoData.LoadAsync();
            await RefreshAccountsAsync();
            SelectedAccount = Accounts.FirstOrDefault(account => account.ExternalAccountId?.StartsWith("demo-", StringComparison.OrdinalIgnoreCase) == true);
            if (SelectedAccount is not null) await AnalyzeAsync();
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Unable to load synthetic permission usage Demo Mode data");
            ErrorMessage = "Unable to load Demo Mode data. Check the local database and application log.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ExportCsv()
    {
        if (!CanExport) return;
        var dialog = new SaveFileDialog { Filter = "CSV files (*.csv)|*.csv", FileName = "permission-usage.csv", AddExtension = true };
        if (dialog.ShowDialog() != true) return;
        var lines = new List<string>
        {
            "Provider,Identity,Permission,Action,Resource,State30Days,State60Days,State90Days,Actions30Days,Actions60Days,Actions90Days,FirstUsedUtc,LastUsedUtc,Correlation"
        };
        lines.AddRange(FilteredPermissionRows.Select(row => string.Join(',', new[]
        {
            row.Provider, row.Identity, row.Permission, row.Action, row.Resource, row.State30Days, row.State60Days,
            row.State90Days, row.Actions30Days.ToString(System.Globalization.CultureInfo.InvariantCulture),
            row.Actions60Days.ToString(System.Globalization.CultureInfo.InvariantCulture),
            row.Actions90Days.ToString(System.Globalization.CultureInfo.InvariantCulture), row.FirstUsedUtc,
            row.LastUsedUtc, row.Correlation
        }.Select(Csv))));
        System.IO.File.WriteAllText(dialog.FileName, string.Join(Environment.NewLine, lines), new UTF8Encoding(true));
        ActivityStatus = $"Exported {FilteredPermissionRows.Count} filtered permission usage row(s) to CSV.";
    }

    private void RaiseFilterProperties()
    {
        OnPropertyChanged(nameof(FilteredPermissionRows));
        OnPropertyChanged(nameof(FilteredHistoryRows));
        OnPropertyChanged(nameof(FilteredTrendRows));
    }

    private static string Csv(string? value) => $"\"{(value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    private static string FormatTimestamp(DateTime? value) => value?.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? "Not observed";
}
