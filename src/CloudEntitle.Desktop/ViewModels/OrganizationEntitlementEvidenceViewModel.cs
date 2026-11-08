using CloudEntitle.Desktop.Services;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.Integration.Services;
using CloudEntitle.OrganizationManagement.Models;
using CloudEntitle.RiskEngine.Models;

namespace CloudEntitle.Desktop.ViewModels;

public sealed partial class OrganizationViewModel
{
    private readonly IEntitlementEvidenceCollector? _entitlementCollector;
    private bool _entitlementConsent;
    private string _entitlementStatus = "Configure/connect the selected account's CLI identity on demand before reading provider permission documents.";
    private IReadOnlyList<ObservedPermissionGrant> _observedGrants = Array.Empty<ObservedPermissionGrant>();
    private IReadOnlyList<EntitlementEvidenceHistoryEntry> _entitlementHistory = Array.Empty<EntitlementEvidenceHistoryEntry>();
    private IReadOnlyList<HistoricalGrantCorrelation> _historicalGrantCorrelations = Array.Empty<HistoricalGrantCorrelation>();
    private EntitlementEvidenceHistoryEntry? _selectedEntitlementSnapshot;
    private CloudActivitySnapshot? _loadedCloudActivitySnapshot;
    private EntitlementEvidenceSnapshot? _loadedEntitlementDocuments;
    private IReadOnlyList<CloudEntitle.Domain.Entities.RiskFindingModel> _documentRiskCandidates = Array.Empty<CloudEntitle.Domain.Entities.RiskFindingModel>();
    private AsyncRelayCommand? _evaluateDocumentRiskCommand;
    public IReadOnlyList<CloudEntitle.Domain.Entities.RiskFindingModel> DocumentRiskCandidates { get => _documentRiskCandidates; private set => SetProperty(ref _documentRiskCandidates, value); }
    public AsyncRelayCommand EvaluateDocumentRiskCommand => _evaluateDocumentRiskCommand ??= new(EvaluateDocumentRiskAsync);
    private AsyncRelayCommand? _collectEntitlementsCommand, _loadEntitlementHistoryCommand, _loadEntitlementSnapshotCommand, _correlateHistoricalGrantsCommand;
    public bool EntitlementReadConsent { get => _entitlementConsent; set => SetProperty(ref _entitlementConsent, value); }
    public string EntitlementEvidenceStatus { get => _entitlementStatus; private set => SetProperty(ref _entitlementStatus, value); }
    public IReadOnlyList<ObservedPermissionGrant> ObservedGrants { get => _observedGrants; private set => SetProperty(ref _observedGrants, value); }
    public IReadOnlyList<EntitlementEvidenceHistoryEntry> EntitlementHistory { get => _entitlementHistory; private set => SetProperty(ref _entitlementHistory, value); }
    public IReadOnlyList<HistoricalGrantCorrelation> HistoricalGrantCorrelations { get => _historicalGrantCorrelations; private set => SetProperty(ref _historicalGrantCorrelations, value); }
    public EntitlementEvidenceHistoryEntry? SelectedEntitlementSnapshot
    {
        get => _selectedEntitlementSnapshot;
        set
        {
            if (!SetProperty(ref _selectedEntitlementSnapshot, value)) return;
            _activityCancellation?.Cancel();
            ObservedGrants = Array.Empty<ObservedPermissionGrant>();
            _loadedEntitlementDocuments = null;
            DocumentRiskCandidates = Array.Empty<CloudEntitle.Domain.Entities.RiskFindingModel>();
            EntitlementReadConsent = false;
        }
    }
    public string EntitlementSetupInstructions => SelectedActivityBinding?.Provider switch
    {
        Domain.CloudProvider.Azure => "Configure/connect Azure CLI for the bound subscription on demand. Grant account read plus Microsoft.Authorization/roleAssignments/read and roleDefinitions/read. Reads subscription role assignments and actual Actions/NotActions/DataActions/NotDataActions; does not change roles, evaluate ABAC or prove historical validity.",
        Domain.CloudProvider.Aws => "Configure/connect the bound AWS account's CLI profile (Activity profile field) on demand. Grant iam:GetAccountAuthorizationDetails and sts:GetCallerIdentity. Reads identity inline/default managed policy versions and observed user group membership. Includes Allow/Deny and condition flags; SCP/RCP, boundary, session, resource and trust policy evaluation remain incomplete.",
        Domain.CloudProvider.Gcp => "Configure/connect gcloud for the bound project on demand. Grant project describe/getIamPolicy and required predefined/custom role get permissions. Reads project IAM etag and included role permissions; no organization/resource/deny/group expansion is inferred.",
        _ => "Select an Azure, AWS or GCP account binding. Configure CLI authentication through the existing cloud connection workflow; never enter tokens or keys here."
    };
    public AsyncRelayCommand CollectEntitlementsCommand => _collectEntitlementsCommand ??= new(CollectEntitlementEvidenceAsync);
    public AsyncRelayCommand LoadEntitlementHistoryCommand => _loadEntitlementHistoryCommand ??= new(LoadEntitlementHistoryAsync);
    public AsyncRelayCommand LoadEntitlementSnapshotCommand => _loadEntitlementSnapshotCommand ??= new(LoadEntitlementSnapshotAsync);
    public AsyncRelayCommand CorrelateHistoricalGrantsCommand => _correlateHistoricalGrantsCommand ??= new(CorrelateHistoricalGrantsAsync);

    private void ResetEntitlementEvidence()
    {
        EntitlementReadConsent = false;
        SelectedEntitlementSnapshot = null;
        ObservedGrants = Array.Empty<ObservedPermissionGrant>();
        EntitlementHistory = Array.Empty<EntitlementEvidenceHistoryEntry>();
        HistoricalGrantCorrelations = Array.Empty<HistoricalGrantCorrelation>();
        _loadedCloudActivitySnapshot = null;
        _loadedEntitlementDocuments = null;
        DocumentRiskCandidates = Array.Empty<CloudEntitle.Domain.Entities.RiskFindingModel>();
        EntitlementEvidenceStatus = "No entitlement evidence for this selection. Configure/connect the client account on demand, then authorize a document read or load saved evidence.";
        OnPropertyChanged(nameof(EntitlementSetupInstructions));
    }

    public async Task CollectEntitlementEvidenceAsync() => await RunEntitlementOperationAsync("collect");
    public async Task LoadEntitlementHistoryAsync() => await RunEntitlementOperationAsync("history");
    public async Task LoadEntitlementSnapshotAsync() => await RunEntitlementOperationAsync("snapshot");
    public async Task CorrelateHistoricalGrantsAsync() => await RunEntitlementOperationAsync("correlate");
    public async Task EvaluateDocumentRiskAsync() => await RunEntitlementOperationAsync("risk");

    private async Task RunEntitlementOperationAsync(string operation)
    {
        if (!CanReadMonitoring || SelectedOrganization is null || SelectedActivityBinding is null
            || _sharedMonitoringApi is not ISharedEntitlementEvidenceApi api)
        { EntitlementEvidenceStatus = "Shared inventory/finding access and a bound account are required."; return; }
        if (ActivityBusy) { EntitlementEvidenceStatus = "An evidence operation is already running."; return; }
        if (operation == "collect" && (!IsAllowed(OrganizationAction.ManagePolicies) || !EntitlementReadConsent || _entitlementCollector is null))
        { EntitlementEvidenceStatus = "ManagePolicies and explicit on-demand document-read consent are required. Please configure/connect the account first."; return; }
        if (operation == "snapshot" && SelectedEntitlementSnapshot is null)
        { EntitlementEvidenceStatus = "Refresh saved entitlement history and select a snapshot first."; return; }
        if (operation == "correlate" && _loadedCloudActivitySnapshot is null)
        { EntitlementEvidenceStatus = "Load selected/latest activity evidence first. Saved observations do not establish historical grant validity."; return; }
        if (operation == "risk" && _loadedEntitlementDocuments is null)
        { EntitlementEvidenceStatus = "Read or load saved permission documents before evaluating review candidates."; return; }
        var organization = SelectedOrganization;
        var binding = SelectedActivityBinding;
        var identity = _identity;
        var selected = SelectedEntitlementSnapshot;
        var activity = _loadedCloudActivitySnapshot;
        var documents = _loadedEntitlementDocuments;
        var query = new EntitlementEvidenceQuery(binding.Provider, binding.ExternalTenantId, ActivityProfile.Trim());
        using var cancellation = new CancellationTokenSource();
        _activityCancellation = cancellation;
        ActivityBusy = true;
        EntitlementReadConsent = false;
        bool Current() => !cancellation.IsCancellationRequested && SelectedOrganization == organization
            && SelectedActivityBinding == binding && _identity == identity;
        try
        {
            if (operation == "collect")
            {
                var snapshot = await _entitlementCollector!.CollectAsync(query, cancellation.Token);
                if (!Current()) return;
                await api.UploadEntitlementEvidenceAsync(organization.Id, snapshot, cancellation.Token);
                if (!Current()) return;
                ObservedGrants = snapshot.Grants;
                _loadedEntitlementDocuments = snapshot;
                DocumentRiskCandidates = Array.Empty<CloudEntitle.Domain.Entities.RiskFindingModel>();
                HistoricalGrantCorrelations = Array.Empty<HistoricalGrantCorrelation>();
                EntitlementEvidenceStatus = $"{snapshot.Grants.Count} provider permission statements saved at {snapshot.CollectedAtUtc:u}. {string.Join(" ", snapshot.Limitations)}";
            }
            else if (operation == "snapshot")
            {
                var snapshot = await api.GetEntitlementEvidenceAsync(organization.Id, binding.Provider, binding.ExternalTenantId, selected!.SnapshotId, cancellation.Token);
                if (!Current()) return;
                ObservedGrants = snapshot.Grants;
                _loadedEntitlementDocuments = snapshot;
                DocumentRiskCandidates = Array.Empty<CloudEntitle.Domain.Entities.RiskFindingModel>();
                EntitlementEvidenceStatus = $"Saved observation at {snapshot.CollectedAtUtc:u}. No live cloud call. {string.Join(" ", snapshot.Limitations)}";
            }
            else if (operation == "risk")
            {
                var inventory = await _activityInventory.GetSnapshotAsync(cancellation.Token);
                if (!Current()) return;
                var account = inventory.Accounts.SingleOrDefault(item => item.Id == binding.CloudAccountId && item.Provider == binding.Provider
                    && string.Equals(item.ExternalId, binding.ExternalTenantId, binding.Provider == Domain.CloudProvider.Azure ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                if (_activityInventory.IsDemoMode || account is null)
                    throw new InvalidOperationException("Matching non-demo account inventory is required. No role-name or display-name risk inference was performed.");
                var result = await new LiveResourceRiskGraphEvaluator().EvaluateDocumentsAsync(account, inventory.Principals, inventory.Resources, documents!, cancellation.Token);
                if (!Current()) return;
                DocumentRiskCandidates = result.Findings;
                EntitlementEvidenceStatus = $"{result.Findings.Count} document-based potential review candidates. {string.Join(" ", result.Diagnostics)}";
            }
            else
            {
                var history = await api.GetEntitlementEvidenceHistoryAsync(organization.Id, binding.Provider, binding.ExternalTenantId, cancellation.Token);
                if (!Current()) return;
                if (operation == "history")
                {
                    EntitlementHistory = history;
                    EntitlementEvidenceStatus = $"{history.Count} latest saved observation summaries (maximum 50). No live cloud call; timestamps are collection times, not grant validity dates.";
                }
                else
                {
                    var evidence = new List<EntitlementEvidenceSnapshot>();
                    foreach (var item in history.Where(item => item.CollectedAtUtc <= activity!.ThroughUtc).OrderByDescending(item => item.CollectedAtUtc).Take(5))
                    {
                        evidence.Add(await api.GetEntitlementEvidenceAsync(organization.Id, binding.Provider, binding.ExternalTenantId, item.SnapshotId, cancellation.Token));
                        if (!Current()) return;
                    }
                    var report = new HistoricalEntitlementAttributionService().Correlate(activity!, evidence);
                    HistoricalGrantCorrelations = report.Observations;
                    EntitlementEvidenceStatus = $"Compared at most five prior saved document snapshots from the latest 50 summaries. {report.Notice}";
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (SelectedOrganization == organization && SelectedActivityBinding == binding && _identity == identity)
                EntitlementEvidenceStatus = "Entitlement operation cancelled. Reload saved history to reconcile an upload whose outcome is unknown.";
        }
        catch (Exception exception)
        {
            if (SelectedOrganization == organization && SelectedActivityBinding == binding && _identity == identity)
                EntitlementEvidenceStatus = $"Entitlement evidence operation failed: {exception.Message}";
        }
        finally { _activityCancellation = null; ActivityBusy = false; }
    }
}
