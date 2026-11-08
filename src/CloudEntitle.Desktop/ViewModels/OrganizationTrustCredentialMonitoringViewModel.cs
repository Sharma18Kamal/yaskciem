using CloudEntitle.Desktop.Services;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.Integration.Services;
using CloudEntitle.OrganizationManagement.Models;

namespace CloudEntitle.Desktop.ViewModels;

public sealed partial class OrganizationViewModel
{
    private readonly ITrustCredentialEvidenceCollector? _trustCredentialCollector;
    private CancellationTokenSource? _trustCredentialCancellation;
    private long _trustCredentialContextVersion;
    private IReadOnlyList<TrustCredentialSnapshotSummary> _trustCredentialHistory = Array.Empty<TrustCredentialSnapshotSummary>();
    private IReadOnlyList<TrustCredentialObservation> _trustCredentialObservations = Array.Empty<TrustCredentialObservation>();
    private IReadOnlyList<TrustCredentialChange> _trustCredentialChanges = Array.Empty<TrustCredentialChange>();
    private IReadOnlyList<EffectivePrivilegeEscalationSignal> _privilegeEscalationSignals = Array.Empty<EffectivePrivilegeEscalationSignal>();
    private IReadOnlyList<SharedScanSummaryDto> _privilegeScanOptions = Array.Empty<SharedScanSummaryDto>();
    private TenantBinding? _trustCredentialBinding;
    private TenantBinding? _privilegeScanBinding;
    private TrustCredentialSnapshotSummary? _selectedTrustCredentialSnapshot;
    private SharedScanSummaryDto? _selectedPrivilegeBaselineScan;
    private SharedScanSummaryDto? _selectedPrivilegeCurrentScan;
    private string _trustCredentialMonitoringStatus = "Select a bound Azure, AWS or GCP account and explicitly authorize an on-demand read.";
    private string _trustCredentialCoverageDetails = "";
    private bool _trustCredentialReadConsent;
    private bool _trustCredentialBusy;
    private AsyncRelayCommand? _collectTrustCredentialCommand;
    private AsyncRelayCommand? _loadTrustCredentialHistoryCommand;
    private AsyncRelayCommand? _loadTrustCredentialSnapshotCommand;
    private AsyncRelayCommand? _analyzePrivilegeScansCommand;
    private AsyncRelayCommand? _refreshPrivilegeScanOptionsCommand;

    public bool CanReadTrustCredentialMonitoring => CanReadMonitoring
        && _sharedOrganizationApi is ISharedTrustCredentialMonitoringApi
        && IsAllowed(OrganizationAction.ManagePolicies);

    public IReadOnlyList<TenantBinding> TrustCredentialBindings
        => Bindings.Where(item => item.Provider is CloudProvider.Azure or CloudProvider.Aws or CloudProvider.Gcp).ToArray();

    public IReadOnlyList<TenantBinding> PrivilegeScanBindings
        => Bindings.Where(item => item.Provider is CloudProvider.Azure or CloudProvider.Aws or CloudProvider.Gcp).ToArray();

    public TenantBinding? SelectedPrivilegeScanBinding
    {
        get => _privilegeScanBinding;
        set
        {
            if (!SetProperty(ref _privilegeScanBinding, value)) return;
            PrivilegeScanOptions = Array.Empty<SharedScanSummaryDto>();
            SelectedPrivilegeBaselineScan = null;
            SelectedPrivilegeCurrentScan = null;
        }
    }

    public TenantBinding? SelectedTrustCredentialBinding
    {
        get => _trustCredentialBinding;
        set
        {
            if (!SetProperty(ref _trustCredentialBinding, value)) return;
            ResetTrustCredentialMonitoring();
        }
    }

    public bool TrustCredentialReadConsent
    {
        get => _trustCredentialReadConsent;
        set => SetProperty(ref _trustCredentialReadConsent, value);
    }

    public bool TrustCredentialBusy
    {
        get => _trustCredentialBusy;
        private set => SetProperty(ref _trustCredentialBusy, value);
    }

    public string TrustCredentialMonitoringStatus
    {
        get => _trustCredentialMonitoringStatus;
        private set => SetProperty(ref _trustCredentialMonitoringStatus, value);
    }

    public string TrustCredentialCoverageDetails
    {
        get => _trustCredentialCoverageDetails;
        private set => SetProperty(ref _trustCredentialCoverageDetails, value);
    }

    public IReadOnlyList<TrustCredentialSnapshotSummary> TrustCredentialHistory
    {
        get => _trustCredentialHistory;
        private set => SetProperty(ref _trustCredentialHistory, value);
    }

    public IReadOnlyList<TrustCredentialObservation> TrustCredentialObservations
    {
        get => _trustCredentialObservations;
        private set => SetProperty(ref _trustCredentialObservations, value);
    }

    public IReadOnlyList<TrustCredentialChange> TrustCredentialChanges
    {
        get => _trustCredentialChanges;
        private set => SetProperty(ref _trustCredentialChanges, value);
    }

    public IReadOnlyList<EffectivePrivilegeEscalationSignal> PrivilegeEscalationSignals
    {
        get => _privilegeEscalationSignals;
        private set => SetProperty(ref _privilegeEscalationSignals, value);
    }

    public IReadOnlyList<SharedScanSummaryDto> PrivilegeScanOptions
    {
        get => _privilegeScanOptions;
        private set => SetProperty(ref _privilegeScanOptions, value);
    }

    public TrustCredentialSnapshotSummary? SelectedTrustCredentialSnapshot
    {
        get => _selectedTrustCredentialSnapshot;
        set
        {
            if (!SetProperty(ref _selectedTrustCredentialSnapshot, value)) return;
            TrustCredentialObservations = Array.Empty<TrustCredentialObservation>();
            TrustCredentialChanges = Array.Empty<TrustCredentialChange>();
            PrivilegeEscalationSignals = Array.Empty<EffectivePrivilegeEscalationSignal>();
            TrustCredentialCoverageDetails = "";
            TrustCredentialReadConsent = false;
        }
    }

    public SharedScanSummaryDto? SelectedPrivilegeBaselineScan
    {
        get => _selectedPrivilegeBaselineScan;
        set => SetProperty(ref _selectedPrivilegeBaselineScan, value);
    }

    public SharedScanSummaryDto? SelectedPrivilegeCurrentScan
    {
        get => _selectedPrivilegeCurrentScan;
        set => SetProperty(ref _selectedPrivilegeCurrentScan, value);
    }

    public string TrustCredentialSetupInstructions => SelectedTrustCredentialBinding?.Provider switch
    {
        CloudProvider.Azure => "Connect Azure CLI to the tenant owning this subscription on demand and grant read-only Microsoft Graph Application.Read.All admin consent. Reads Entra service-principal credentials and local application federation metadata only.",
        CloudProvider.Aws => "Connect AWS CLI to this exact account on demand with IAM read permissions for role trust policies and get-credential-report. The client never generates or refreshes a credential report.",
        CloudProvider.Gcp => "Connect gcloud to this exact project on demand with read permissions for service accounts, user-managed keys, and project/service-account IAM policies.",
        _ => "Select a bound Azure, AWS or GCP account. The source is contacted only after explicit consent."
    } + " No secret values/private keys are requested or stored; no cloud writes are performed. Coverage is partial.";

    public string TrustCredentialConsentLabel => SelectedTrustCredentialBinding?.Provider switch
    {
        CloudProvider.Azure => "I authorize one on-demand Azure CLI / Microsoft Graph metadata read",
        CloudProvider.Aws => "I authorize one on-demand AWS IAM metadata read",
        CloudProvider.Gcp => "I authorize one on-demand gcloud IAM metadata read",
        _ => "I authorize one on-demand cloud metadata read"
    };

    public AsyncRelayCommand CollectTrustCredentialCommand
        => _collectTrustCredentialCommand ??= new(CollectTrustCredentialEvidenceAsync);

    public AsyncRelayCommand LoadTrustCredentialHistoryCommand
        => _loadTrustCredentialHistoryCommand ??= new(LoadTrustCredentialHistoryAsync);

    public AsyncRelayCommand LoadTrustCredentialSnapshotCommand
        => _loadTrustCredentialSnapshotCommand ??= new(LoadSelectedTrustCredentialSnapshotAsync);

    public AsyncRelayCommand RefreshPrivilegeScanOptionsCommand
        => _refreshPrivilegeScanOptionsCommand ??= new(RefreshPrivilegeScanOptionsAsync);

    public AsyncRelayCommand AnalyzePrivilegeScansCommand
        => _analyzePrivilegeScansCommand ??= new(AnalyzePrivilegeScansAsync);

    private void ResetTrustCredentialMonitoring()
    {
        _trustCredentialContextVersion++;
        _trustCredentialCancellation?.Cancel();
        _trustCredentialCancellation = null;
        TrustCredentialBusy = false;
        TrustCredentialReadConsent = false;
        TrustCredentialHistory = Array.Empty<TrustCredentialSnapshotSummary>();
        TrustCredentialObservations = Array.Empty<TrustCredentialObservation>();
        TrustCredentialChanges = Array.Empty<TrustCredentialChange>();
        PrivilegeEscalationSignals = Array.Empty<EffectivePrivilegeEscalationSignal>();
        TrustCredentialCoverageDetails = "";
        PrivilegeScanOptions = Array.Empty<SharedScanSummaryDto>();
        SelectedTrustCredentialSnapshot = null;
        SelectedPrivilegeScanBinding = null;
        SelectedPrivilegeBaselineScan = null;
        SelectedPrivilegeCurrentScan = null;
        TrustCredentialMonitoringStatus = SelectedTrustCredentialBinding is null
            ? "Select an Azure, AWS or GCP binding. Configure/connect the cloud CLI only when you choose to collect."
            : "No trust/credential snapshot loaded. The source is contacted only after explicit consent.";
        OnPropertyChanged(nameof(TrustCredentialBindings));
        OnPropertyChanged(nameof(PrivilegeScanBindings));
        OnPropertyChanged(nameof(TrustCredentialSetupInstructions));
        OnPropertyChanged(nameof(TrustCredentialConsentLabel));
        OnPropertyChanged(nameof(CanReadTrustCredentialMonitoring));
    }

    public async Task CollectTrustCredentialEvidenceAsync()
    {
        if (!CanReadTrustCredentialMonitoring || SelectedOrganization is null || SelectedTrustCredentialBinding is null
            || _sharedOrganizationApi is not ISharedTrustCredentialMonitoringApi api || _trustCredentialCollector is null)
        {
            TrustCredentialMonitoringStatus = "Shared inventory/finding access, ManagePolicies, an Azure/AWS/GCP binding and the trust/credential source reader are required.";
            return;
        }
        if (!TrustCredentialReadConsent)
        {
            TrustCredentialMonitoringStatus = "Explicitly authorize this on-demand read before continuing.";
            return;
        }
        if (TrustCredentialBusy)
        {
            TrustCredentialMonitoringStatus = "A trust/credential collection is already running.";
            return;
        }
        var organization = SelectedOrganization;
        var binding = SelectedTrustCredentialBinding;
        var identity = _identity;
        var contextVersion = _trustCredentialContextVersion;
        using var operation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        _trustCredentialCancellation = operation;
        TrustCredentialBusy = true;
        try
        {
            var snapshot = await _trustCredentialCollector.CollectAsync(new(binding.Provider, binding.ExternalTenantId), operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (contextVersion != _trustCredentialContextVersion || SelectedOrganization != organization || SelectedTrustCredentialBinding != binding || _identity != identity) return;
            await api.UploadTrustCredentialSnapshotAsync(organization.Id, snapshot, operation.Token);
            var report = await api.AnalyzeTrustCredentialMonitoringAsync(organization.Id,
                new(binding.Provider, binding.ExternalTenantId, snapshot.SnapshotId, null, null), operation.Token);
            if (contextVersion != _trustCredentialContextVersion || SelectedOrganization != organization || SelectedTrustCredentialBinding != binding || _identity != identity) return;
            TrustCredentialObservations = snapshot.Observations;
            TrustCredentialCoverageDetails = string.Join(Environment.NewLine, snapshot.Limitations.Select(item => $"• {item}"));
            TrustCredentialChanges = report.TrustCredentialChanges;
            PrivilegeEscalationSignals = report.PrivilegeEscalations;
            var history = await LoadTrustCredentialHistoryCoreAsync(api, organization.Id, binding.Provider, binding.ExternalTenantId, operation.Token);
            if (contextVersion != _trustCredentialContextVersion || SelectedOrganization != organization
                || SelectedTrustCredentialBinding != binding || _identity != identity) return;
            TrustCredentialHistory = history;
            SelectedTrustCredentialSnapshot = TrustCredentialHistory.FirstOrDefault(item => item.SnapshotId == snapshot.SnapshotId);
            TrustCredentialObservations = snapshot.Observations;
            TrustCredentialCoverageDetails = string.Join(Environment.NewLine, snapshot.Limitations.Select(item => $"• {item}"));
            TrustCredentialChanges = report.TrustCredentialChanges;
            TrustCredentialMonitoringStatus = $"Saved {snapshot.Observations.Count} partial-coverage {binding.Provider} trust/credential observation(s); {report.TrustCredentialChanges.Count} observed change(s). {report.Notices.FirstOrDefault()}";
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (contextVersion == _trustCredentialContextVersion && SelectedOrganization == organization && SelectedTrustCredentialBinding == binding && _identity == identity)
                TrustCredentialMonitoringStatus = "Trust/credential collection stopped. No cloud write was performed.";
        }
        catch (Exception exception)
        {
            if (contextVersion == _trustCredentialContextVersion && SelectedOrganization == organization && SelectedTrustCredentialBinding == binding && _identity == identity)
                TrustCredentialMonitoringStatus = $"Trust/credential monitoring failed: {exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(_trustCredentialCancellation, operation))
                _trustCredentialCancellation = null;
            if (contextVersion == _trustCredentialContextVersion && SelectedOrganization == organization && SelectedTrustCredentialBinding == binding && _identity == identity)
            {
                TrustCredentialBusy = false;
                TrustCredentialReadConsent = false;
            }
        }
    }

    public async Task LoadTrustCredentialHistoryAsync()
    {
        if (!CanReadMonitoring || SelectedOrganization is null || SelectedTrustCredentialBinding is null
            || _sharedOrganizationApi is not ISharedTrustCredentialMonitoringApi api)
        {
            TrustCredentialMonitoringStatus = "Select a bound Azure, AWS or GCP account and ensure shared inventory/finding access is available.";
            return;
        }
        var organization = SelectedOrganization;
        var binding = SelectedTrustCredentialBinding;
        var identity = _identity;
        var contextVersion = _trustCredentialContextVersion;
        try
        {
            var history = await LoadTrustCredentialHistoryCoreAsync(api, organization.Id, binding.Provider, binding.ExternalTenantId, CancellationToken.None);
            if (contextVersion != _trustCredentialContextVersion || SelectedOrganization != organization || SelectedTrustCredentialBinding != binding || _identity != identity) return;
            TrustCredentialHistory = history;
            SelectedTrustCredentialSnapshot = null;
            TrustCredentialMonitoringStatus = TrustCredentialHistory.Count == 0
                ? "No saved trust/credential observations exist. Connect and collect only when explicitly authorized."
                : $"Loaded {TrustCredentialHistory.Count} immutable snapshot(s). Select a snapshot to inspect its observed metadata.";
        }
        catch (Exception exception)
        {
            if (contextVersion == _trustCredentialContextVersion && SelectedOrganization == organization && SelectedTrustCredentialBinding == binding && _identity == identity)
                TrustCredentialMonitoringStatus = $"Trust/credential history refresh failed: {exception.Message}";
        }
    }

    private Task<IReadOnlyList<TrustCredentialSnapshotSummary>> LoadTrustCredentialHistoryCoreAsync(ISharedTrustCredentialMonitoringApi api, Guid organizationId,
        CloudProvider provider, string accountId, CancellationToken token)
        => api.GetTrustCredentialHistoryAsync(organizationId, provider, accountId, token);

    public async Task LoadSelectedTrustCredentialSnapshotAsync()
    {
        if (!CanReadMonitoring || SelectedOrganization is null || SelectedTrustCredentialBinding is null
            || SelectedTrustCredentialSnapshot is null || _sharedOrganizationApi is not ISharedTrustCredentialMonitoringApi api)
        {
            TrustCredentialMonitoringStatus = "Select a saved trust/credential snapshot and confirm shared inventory/finding access.";
            return;
        }
        var organization = SelectedOrganization;
        var binding = SelectedTrustCredentialBinding;
        var selected = SelectedTrustCredentialSnapshot;
        var identity = _identity;
        var contextVersion = _trustCredentialContextVersion;
        try
        {
            var snapshot = await api.GetTrustCredentialSnapshotAsync(organization.Id, binding.Provider, binding.ExternalTenantId, selected.SnapshotId);
            if (contextVersion != _trustCredentialContextVersion || SelectedOrganization != organization || SelectedTrustCredentialBinding != binding
                || SelectedTrustCredentialSnapshot != selected || _identity != identity) return;
            TrustCredentialObservations = snapshot.Observations;
            TrustCredentialCoverageDetails = string.Join(Environment.NewLine, snapshot.Limitations.Select(item => $"• {item}"));
            TrustCredentialMonitoringStatus = $"Loaded {snapshot.Observations.Count} saved metadata observation(s). Secrets and private key material are not present.";
        }
        catch (Exception exception)
        {
            if (contextVersion == _trustCredentialContextVersion && SelectedOrganization == organization && SelectedTrustCredentialBinding == binding && _identity == identity)
                TrustCredentialMonitoringStatus = $"Saved trust/credential snapshot read failed: {exception.Message}";
        }
    }

    public async Task RefreshPrivilegeScanOptionsAsync()
    {
        if (!CanReadMonitoring || _sharedOrganizationApi is null || SelectedOrganization is null || SelectedPrivilegeScanBinding is null)
        {
            TrustCredentialMonitoringStatus = "Select a bound cloud account and ensure shared inventory/finding access is available.";
            return;
        }
        var organization = SelectedOrganization;
        var binding = SelectedPrivilegeScanBinding;
        var identity = _identity;
        try
        {
            var summaries = await _sharedOrganizationApi.GetScanSummariesAsync(organization.Id, 50);
            if (SelectedOrganization != organization || SelectedPrivilegeScanBinding != binding || _identity != identity) return;
            PrivilegeScanOptions = summaries.Where(item => item.Accounts.Any(account => account.Provider == binding.Provider
                    && account.ExternalAccountId.Equals(binding.ExternalTenantId, StringComparison.OrdinalIgnoreCase))
                && item.Coverage.Any(coverage => coverage.Provider.Equals(binding.Provider.ToString(), StringComparison.OrdinalIgnoreCase)
                    && coverage.DataSource.Equals("Effective access", StringComparison.OrdinalIgnoreCase)
                    && coverage.Status is "Collected" or "NoData")).ToArray();
            TrustCredentialMonitoringStatus = $"Loaded {PrivilegeScanOptions.Count} saved scan(s) with successful effective-access collection. Assignments from these scans can only produce Potential signals.";
        }
        catch (Exception exception)
        {
            if (SelectedOrganization == organization && SelectedPrivilegeScanBinding == binding && _identity == identity)
                TrustCredentialMonitoringStatus = $"Effective-access scan refresh failed: {exception.Message}";
        }
    }

    public async Task AnalyzePrivilegeScansAsync()
    {
        if (!CanReadTrustCredentialMonitoring || SelectedOrganization is null || SelectedPrivilegeScanBinding is null
            || _sharedOrganizationApi is not ISharedTrustCredentialMonitoringApi api
            || SelectedPrivilegeBaselineScan is null || SelectedPrivilegeCurrentScan is null)
        {
            TrustCredentialMonitoringStatus = "Select a saved baseline and current scan for the same bound account.";
            return;
        }
        var baseline = SelectedPrivilegeBaselineScan;
        var current = SelectedPrivilegeCurrentScan;
        if (baseline.CompletedAtUtc >= current.CompletedAtUtc)
        {
            TrustCredentialMonitoringStatus = "The baseline scan must precede the current scan.";
            return;
        }
        var organization = SelectedOrganization;
        var binding = SelectedPrivilegeScanBinding;
        var identity = _identity;
        try
        {
            var report = await api.AnalyzeTrustCredentialMonitoringAsync(organization.Id,
                new(binding.Provider, binding.ExternalTenantId, null, baseline.ScanId, current.ScanId));
            if (SelectedOrganization != organization || SelectedPrivilegeScanBinding != binding || _identity != identity) return;
            TrustCredentialChanges = report.TrustCredentialChanges;
            PrivilegeEscalationSignals = report.PrivilegeEscalations;
            TrustCredentialMonitoringStatus = report.PrivilegeEscalations.Count == 0
                ? report.Notices.FirstOrDefault() ?? "No potential privileged assignment changes were observed."
                : $"Found {report.PrivilegeEscalations.Count} Potential privileged assignment change(s). No verified escalation is claimed because provider-effective before/after decisions are unavailable.";
        }
        catch (Exception exception)
        {
            if (SelectedOrganization == organization && SelectedPrivilegeScanBinding == binding && _identity == identity)
                TrustCredentialMonitoringStatus = $"Effective-access comparison failed: {exception.Message}";
        }
    }
}
