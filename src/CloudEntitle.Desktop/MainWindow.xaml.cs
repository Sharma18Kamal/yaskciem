using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CloudEntitle.Infrastructure.Persistence;
using CloudEntitle.Infrastructure;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Application.Services;
using CloudEntitle.Cloud.Aws;
using CloudEntitle.Cloud.Azure;
using CloudEntitle.Cloud.Gcp;
using CloudEntitle.Domain;
using Microsoft.EntityFrameworkCore;
using System.Windows;

namespace CloudEntitle.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var composition = CreateComposition();
        DataContext = new DashboardViewModel(composition.ScanService, composition.ResultStore, composition.InventoryStore);
    }

    private static (ScanService ScanService, IScanResultStore ResultStore, ICloudInventoryStore InventoryStore) CreateComposition()
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YASK-CIEM");
        Directory.CreateDirectory(dataDirectory);

        var databasePath = Path.Combine(dataDirectory, "yask-ciem.db");
        var options = new DbContextOptionsBuilder<CloudEntitleDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;

        using var context = new CloudEntitleDbContext(options);
        context.Database.EnsureCreated();

        var rules = context.RiskRules.ToList();
        if (rules.Count == 0)
        {
            rules = RiskEngine.DefaultRules.ToList();
            context.RiskRules.AddRange(rules);
            context.SaveChanges();
        }

        var providers = new ICloudProvider[]
        {
            new AzureProvider(new ProcessCloudCommandRunner(), useLiveDiscovery: true),
            new AwsProvider(),
            new GcpProvider()
        };

        return (
            new ScanService(providers, new RiskEngine(rules)),
            new SqliteScanResultStore(options),
            new SqliteCloudInventoryStore(options));
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await ((DashboardViewModel)DataContext).LoadAsync();
    }
}

public sealed class DashboardViewModel : INotifyPropertyChanged
{
    private readonly ScanService _scanService;
    private readonly IScanResultStore _resultStore;
    private readonly ICloudInventoryStore _inventoryStore;
    private int _totalCloudAccounts;
    private int _highRiskFindings;
    private int _privilegedIdentities;
    private int _criticalFindings;
    private string _statusMessage = "Ready to scan connected cloud providers.";
    private string _selectedProviderFilter = "All Providers";
    private IReadOnlyList<RiskFinding> _allFindings = Array.Empty<RiskFinding>();
    private IReadOnlyList<CloudPrincipal> _allPrincipals = Array.Empty<CloudPrincipal>();
    public ObservableCollection<RiskFinding> Findings { get; } = new();
    public ObservableCollection<CloudPrincipal> Principals { get; } = new();
    public ObservableCollection<CloudRole> Roles { get; } = new();
    public ObservableCollection<CloudPermission> Permissions { get; } = new();
    public IReadOnlyList<string> ProviderFilters { get; } = new[] { "All Providers", "Azure", "Aws", "Gcp" };

    public DashboardViewModel(ScanService scanService, IScanResultStore resultStore, ICloudInventoryStore inventoryStore)
    {
        _scanService = scanService;
        _resultStore = resultStore;
        _inventoryStore = inventoryStore;
        ScanCommand = new AsyncCommand(ScanAsync);
    }

    public int TotalCloudAccounts
    {
        get => _totalCloudAccounts;
        private set => SetField(ref _totalCloudAccounts, value);
    }

    public int HighRiskFindings
    {
        get => _highRiskFindings;
        private set => SetField(ref _highRiskFindings, value);
    }

    public int PrivilegedIdentities
    {
        get => _privilegedIdentities;
        private set => SetField(ref _privilegedIdentities, value);
    }

    public int CriticalFindings
    {
        get => _criticalFindings;
        private set => SetField(ref _criticalFindings, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public string SelectedProviderFilter
    {
        get => _selectedProviderFilter;
        set
        {
            if (!SetField(ref _selectedProviderFilter, value))
            {
                return;
            }

            ApplyProviderFilter();
        }
    }

    public ICommand ScanCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task LoadAsync()
    {
        var findings = await _resultStore.GetRecentFindingsAsync();
        var inventory = await _inventoryStore.GetSummaryAsync();
        _allFindings = findings;
        _allPrincipals = await _inventoryStore.GetPrincipalsAsync();
        ApplyProviderFilter();
        ReplaceCollection(Roles, await _inventoryStore.GetRolesAsync());
        ReplaceCollection(Permissions, await _inventoryStore.GetPermissionsAsync());
        TotalCloudAccounts = inventory.AccountCount;
        PrivilegedIdentities = inventory.PrincipalCount;
        StatusMessage = findings.Count == 0
            ? "Ready to scan connected cloud providers."
            : $"Loaded {findings.Count} finding(s) from the last scan.";
    }

    private async Task ScanAsync()
    {
        StatusMessage = "Scanning Azure, AWS, and GCP providers...";
        var findings = new List<RiskFinding>();
        var accounts = new List<CloudAccount>();
        var principals = new List<CloudPrincipal>();
        var roles = new List<CloudRole>();
        var permissions = new List<CloudPermission>();

        foreach (var provider in _scanService.Providers)
        {
            var providerAccounts = await provider.DiscoverAccountsAsync();
            accounts.AddRange(providerAccounts);

            foreach (var account in providerAccounts)
            {
                principals.AddRange(await provider.DiscoverIdentitiesAsync(account.Id));
                roles.AddRange(await provider.DiscoverRolesAsync(account.Id));
                permissions.AddRange(await provider.DiscoverPermissionsAsync(account.Id));
                findings.AddRange(await _scanService.RunProviderScanAsync(provider, account.Id));
            }
        }

        TotalCloudAccounts = accounts.Count;
        PrivilegedIdentities = principals.Count;
        HighRiskFindings = findings.Count(finding => finding.Severity is RiskSeverity.High or RiskSeverity.Critical);
        CriticalFindings = findings.Count(finding => finding.Severity == RiskSeverity.Critical);
        await _resultStore.ReplaceFindingsAsync(findings);
        await _inventoryStore.ReplaceInventoryAsync(accounts, principals, roles, permissions);
        _allFindings = findings;
        _allPrincipals = principals;
        ApplyProviderFilter();
        ReplaceCollection(Roles, roles);
        ReplaceCollection(Permissions, permissions);
        StatusMessage = $"Last scan completed at {DateTime.Now:g}. Found {findings.Count} risk finding(s).";
    }

    private void ApplyProviderFilter()
    {
        var hasProviderFilter = Enum.TryParse<CloudProviderType>(SelectedProviderFilter, true, out var provider);
        ReplaceCollection(Findings, hasProviderFilter
            ? _allFindings.Where(finding => finding.CloudProvider == provider)
            : _allFindings);
        ReplaceCollection(Principals, hasProviderFilter
            ? _allPrincipals.Where(principal => principal.CloudProvider == provider)
            : _allPrincipals);
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> _execute;
    private bool _isExecuting;

    public AsyncCommand(Func<Task> execute)
    {
        _execute = execute;
    }

    public bool CanExecute(object? parameter) => !_isExecuting;

    public event EventHandler? CanExecuteChanged;

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isExecuting = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await _execute();
        }
        finally
        {
            _isExecuting = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
