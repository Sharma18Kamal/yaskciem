using CloudEntitle.Desktop.Services;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Integration.Services;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.OrganizationManagement.Models;

namespace CloudEntitle.Desktop.ViewModels;

public sealed partial class OrganizationViewModel
{
    private readonly ICloudActivityCollector? _activityCollector;
    private readonly IDemoDataService _activityInventory;
    private CloudActivitySource _activitySource;
    private string _activityDestination = "", _activityContainer = "", _activityPrefix = "", _activityAttributionStatus = "No current entitlement correlation loaded.";
    private IReadOnlyList<ActivityEntitlementCorrelation> _activityCorrelations = Array.Empty<ActivityEntitlementCorrelation>();
    private IReadOnlyList<CloudActivityProvenance> _activityProvenance = Array.Empty<CloudActivityProvenance>();
    private TenantBinding? _activityBinding;
    private string _activityRegion = "", _activityProfile = "", _activityStatus = "Select a bound account. Activity is collected only after explicit consent.";
    private bool _activityConsent, _activityBusy, _endpointConfirmed;
    private CancellationTokenSource? _activityCancellation;
    private IReadOnlyList<CloudActivityActionUsage> _activityActions = Array.Empty<CloudActivityActionUsage>();
    private AsyncRelayCommand? _collectActivityCommand, _loadActivityCommand, _cancelActivityCommand;
    private AsyncRelayCommand? _refreshActivityHistoryCommand, _olderActivityHistoryCommand;
    private IReadOnlyList<CloudActivityHistoryEntry> _activityHistory = Array.Empty<CloudActivityHistoryEntry>();
    private CloudActivityHistoryEntry? _selectedActivitySnapshot;
    private Guid? _activityHistoryCursor;
    public IReadOnlyList<CloudActivityHistoryEntry> ActivityHistory { get => _activityHistory; private set => SetProperty(ref _activityHistory, value); }
    public bool HasOlderActivityHistory => _activityHistoryCursor is not null;
    public CloudActivityHistoryEntry? SelectedActivitySnapshot
    {
        get => _selectedActivitySnapshot;
        set
        {
            if (!SetProperty(ref _selectedActivitySnapshot, value)) return;
            _activityCancellation?.Cancel();
            ActivityReadConsent = false;
            ActivityActions = Array.Empty<CloudActivityActionUsage>();
            _loadedCloudActivitySnapshot = null;
            HistoricalGrantCorrelations = Array.Empty<HistoricalGrantCorrelation>();
            ActivityCorrelations = Array.Empty<ActivityEntitlementCorrelation>();
            ActivityProvenance = Array.Empty<CloudActivityProvenance>();
            ActivityAttributionStatus = "No current entitlement correlation loaded.";
            ActivityStatus = value is null ? "Load latest saved evidence or select a historical snapshot."
                : "Historical snapshot selected. Load saved evidence; no live cloud call is required.";
        }
    }
    public AsyncRelayCommand RefreshActivityHistoryCommand => _refreshActivityHistoryCommand ??= new(() => LoadCloudActivityHistoryAsync(false));
    public AsyncRelayCommand OlderActivityHistoryCommand => _olderActivityHistoryCommand ??= new(() => LoadCloudActivityHistoryAsync(true));

    public AsyncRelayCommand CollectActivityCommand => _collectActivityCommand ??= new(CollectCloudActivityAsync);
    public AsyncRelayCommand LoadActivityCommand => _loadActivityCommand ??= new(LoadCloudActivityAsync);
    public AsyncRelayCommand CancelActivityCommand => _cancelActivityCommand ??= new(() => { _activityCancellation?.Cancel(); return Task.CompletedTask; });
    public TenantBinding? SelectedActivityBinding
    {
        get => _activityBinding;
        set
        {
            if (!SetProperty(ref _activityBinding, value)) return;
            ResetCloudActivity();
            ActivitySourceKind = CloudActivitySource.Default;
            OnPropertyChanged(nameof(ActivitySources));
            OnPropertyChanged(nameof(ActivitySetupInstructions));
        }
    }
    public string ActivityRegion { get => _activityRegion; set { if (SetProperty(ref _activityRegion, value)) ResetCloudActivity(); } }
    public string ActivityProfile { get => _activityProfile; set { if (SetProperty(ref _activityProfile, value)) ResetCloudActivity(); } }
    public IReadOnlyList<CloudActivitySource> ActivitySources => SelectedActivityBinding?.Provider switch
    {
        Domain.CloudProvider.Aws => new[] { CloudActivitySource.Default, CloudActivitySource.AwsCloudTrailLake, CloudActivitySource.AwsS3Archive },
        Domain.CloudProvider.Azure => new[] { CloudActivitySource.Default, CloudActivitySource.AzureLogAnalytics, CloudActivitySource.AzureBlobArchive },
        _ => new[] { CloudActivitySource.Default }
    };
    public CloudActivitySource ActivitySourceKind
    {
        get => _activitySource;
        set { if (SetProperty(ref _activitySource, value)) { ResetCloudActivity(); OnPropertyChanged(nameof(ActivitySetupInstructions)); } }
    }
    public string ActivityDestination { get => _activityDestination; set { if (SetProperty(ref _activityDestination, value)) ResetCloudActivity(); } }
    public string ActivityContainer { get => _activityContainer; set { if (SetProperty(ref _activityContainer, value)) ResetCloudActivity(); } }
    public string ActivityPrefix { get => _activityPrefix; set { if (SetProperty(ref _activityPrefix, value)) ResetCloudActivity(); } }
    public IReadOnlyList<ActivityEntitlementCorrelation> ActivityCorrelations { get => _activityCorrelations; private set => SetProperty(ref _activityCorrelations, value); }
    public IReadOnlyList<CloudActivityProvenance> ActivityProvenance { get => _activityProvenance; private set => SetProperty(ref _activityProvenance, value); }
    public string ActivityAttributionStatus { get => _activityAttributionStatus; private set => SetProperty(ref _activityAttributionStatus, value); }
    public bool ActivityReadConsent { get => _activityConsent; set => SetProperty(ref _activityConsent, value); }
    public bool ActivityBusy { get => _activityBusy; private set => SetProperty(ref _activityBusy, value); }
    public string ActivityStatus { get => _activityStatus; private set => SetProperty(ref _activityStatus, value); }
    public IReadOnlyList<CloudActivityActionUsage> ActivityActions { get => _activityActions; private set => SetProperty(ref _activityActions, value); }
    public bool NotificationEndpointConfirmed { get => _endpointConfirmed; set => SetProperty(ref _endpointConfirmed, value); }
    public string ActivitySetupInstructions => ActivitySourceKind switch
    {
        CloudActivitySource.AwsCloudTrailLake => "Please select an EXISTING CloudTrail Lake event data store UUID with required management/data selectors and retention. Lake closed to new customers on 31 May 2026; new clients should use S3 archives. Grant GetEventDataStore, StartQuery and GetQueryResults. Explicit read consent also authorizes a BILLABLE query; cancelling collection does not stop the AWS query. No event data store is created.",
        CloudActivitySource.AwsS3Archive => "Please create/configure a client-owned CloudTrail trail with required data events and an S3 archive. Destination = bucket NAME; Prefix = AWSLogs/<bound account>/CloudTrail/<region>/<YYYY>/<MM>/<DD>/ (plus your optional base prefix). Grant s3:ListBucket/GetObject and KMS decrypt if required. Same-account bucket ownership is enforced. Use a narrow date prefix; maximum 128 files per read.",
        CloudActivitySource.AzureLogAnalytics => "Please configure resource diagnostic settings to a client-owned workspace. Destination = exact workspace ARM resource ID in the bound subscription. Grant workspace read/query access. Reads StorageBlob/File/Queue/TableLogs and AzureDiagnostics, not all Azure services. API query POST is read-only. No workspace or diagnostic setting is created.",
        CloudActivitySource.AzureBlobArchive => "Please configure resource diagnostic settings to a client-owned storage account. Destination = storage account ARM ID in the bound subscription; Container = insights-logs-*; Prefix = narrow resource/date prefix. Grant storage-account read and Storage Blob Data Reader. Only native JSON records archives are supported; maximum 128 files. No account keys are requested.",
        _ => DefaultActivitySetupInstructions
    };
    private string DefaultActivitySetupInstructions => SelectedActivityBinding?.Provider switch
    {
        Domain.CloudProvider.Aws => "On demand: sign in to the bound AWS account with a read-only CLI profile; enter an explicit region. Grant cloudtrail:LookupEvents. No new endpoint is required for regional event history. Provision a trail/log archive separately for data events or multi-region coverage. Event history alone cannot prove unused IAM permissions.",
        Domain.CloudProvider.Azure => "On demand: authenticate Azure CLI in the subscription tenant and grant Microsoft.Insights/eventtypes/values/read. No new endpoint is required for Activity Log reads. If data-plane evidence is needed, please create diagnostic settings and a client-owned log destination; this app will not create them or modify permissions.",
        Domain.CloudProvider.Gcp => "On demand: authenticate gcloud for the bound project and grant logging.logEntries.list (and private log access for Data Access logs). Please enable required Data Access audit logging and retention through your administrator. Reads do not enable logging or create a sink; missing log categories remain unknown.",
        _ => "Select a bound cloud account to see the read permissions and any endpoint/log provisioning required. Do not enter secrets into these fields."
    };
    public string NotificationEndpointInstructions => NotificationConnectorKind switch
    {
        SecurityConnectorCatalog.Splunk => "Please create/enable a client-owned Splunk HEC event endpoint and restricted index token. Ask the deployment administrator to configure Kind=SplunkHec, Url, Token and Index under this tenant/organization channel alias. Keep the token server-side. Confirm readiness below only when this alias has been configured.",
        SecurityConnectorCatalog.QRadar => "Please create a QRadar TLS syslog log source with LEEF 2.0 and newline framing. Provide an approved mTLS client PFX to the deployment administrator. Configure Kind=QRadarTls, Url, LogSourceIdentifier and ClientCertificatePath/password under this organization alias. Never paste the PFX password here.",
        SecurityConnectorCatalog.Sentinel => "Please create a client-owned Log Analytics custom table and DCR ingestion endpoint with TimeGenerated/ Event columns. Grant the server managed identity Monitoring Metrics Publisher on that DCR. Ask the administrator to configure Kind=SentinelLogs, Url, RuleId, Stream and optional ManagedIdentityClientId. Cross-tenant hosting needs a client-tenant host/bridge.",
        "Jira" => "Please provision a Jira project and dedicated service account token, then ask the administrator to configure this organization Jira alias. Tickets do not authorize cloud changes.",
        "SignedWebhook" => "Please provision a client-owned HTTPS SIEM/SOAR receiver with signature verification and durable deduplication. Ask the administrator to configure the organization alias with Url and SigningKey. Keep secrets server-side; delivery never authorizes response actions.",
        _ => "This connector is not supported. Choose a supported product; Carbon Black requires client edition/API details first."
    };

    private void ResetCloudActivity()
    {
        _activityCancellation?.Cancel();
        ResetEntitlementEvidence();
        ActivityReadConsent = false;
        ActivityHistory = Array.Empty<CloudActivityHistoryEntry>();
        SelectedActivitySnapshot = null;
        _activityHistoryCursor = null;
        OnPropertyChanged(nameof(HasOlderActivityHistory));
        ActivityActions = Array.Empty<CloudActivityActionUsage>();
        ActivityCorrelations = Array.Empty<ActivityEntitlementCorrelation>();
        ActivityProvenance = Array.Empty<CloudActivityProvenance>();
        ActivityAttributionStatus = "No current entitlement correlation loaded.";
        ActivityStatus = "No activity loaded for this selection. Review setup and explicitly authorize a live read, or load saved evidence without contacting the cloud.";
    }

    public async Task CollectCloudActivityAsync()
    {
        if (!CanReadMonitoring || !IsAllowed(OrganizationAction.RunScan) || SelectedOrganization is null
            || SelectedActivityBinding is null || _activityCollector is null || _sharedMonitoringApi is not ISharedCloudActivityApi api)
        { ActivityStatus = "Shared mode, a bound account, and RunScan/inventory/findings permissions are required."; return; }
        if (!ActivityReadConsent)
        { ActivityStatus = "Please prepare the cloud logging/read permissions shown above, then authorize this on-demand read."; return; }
        if (ActivityBusy) { ActivityStatus = "An activity operation is already running."; return; }
        var organization = SelectedOrganization;
        var binding = SelectedActivityBinding;
        var identity = _identity;
        var query = new CloudActivityQuery(binding.Provider, binding.ExternalTenantId,
            ActivityRegion.Trim(), ActivityProfile.Trim(), DateTimeOffset.UtcNow)
        { SourceKind = ActivitySourceKind, Destination = ActivityDestination.Trim(), Container = ActivityContainer.Trim(), Prefix = ActivityPrefix.Trim() };
        SelectedActivitySnapshot = null;
        using var cancellation = new CancellationTokenSource();
        _activityCancellation = cancellation;
        ActivityBusy = true;
        ActivityReadConsent = false;
        ActivityStatus = "Reading the selected account's last 90 days. No endpoints or permissions will be created or changed.";
        try
        {
            var snapshot = await _activityCollector.CollectAsync(query, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (SelectedOrganization != organization || SelectedActivityBinding != binding || _identity != identity) return;
            await api.UploadCloudActivityAsync(organization.Id, snapshot, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (SelectedOrganization != organization || SelectedActivityBinding != binding || _identity != identity) return;
            ActivityActions = CloudActivityAnalysis.Analyze(snapshot);
            _loadedCloudActivitySnapshot = snapshot;
            await CorrelateActivityAsync(snapshot, binding, cancellation.Token);
            if (SelectedOrganization != organization || SelectedActivityBinding != binding || _identity != identity) return;
            ActivityStatus = $"{snapshot.Events.Count} normalized rows saved. Coverage: {snapshot.CoverageDetails} Truncated: {snapshot.Truncated}. No unused-permission conclusion is authorized by this evidence.";
        }
        catch (OperationCanceledException)
        {
            if (SelectedOrganization == organization && SelectedActivityBinding == binding && _identity == identity)
                ActivityStatus = "Activity operation cancelled. Reload saved evidence to check whether an upload completed.";
        }
        catch (Exception exception)
        {
            if (SelectedOrganization == organization && SelectedActivityBinding == binding && _identity == identity)
                ActivityStatus = $"Activity collection/upload failed: {exception.Message}";
        }
        finally { _activityCancellation = null; ActivityBusy = false; }
    }

    public async Task LoadCloudActivityAsync()
    {
        if (!CanReadMonitoring || SelectedOrganization is null || SelectedActivityBinding is null
            || _sharedMonitoringApi is not ISharedCloudActivityApi api)
        { ActivityStatus = "Select a bound account in shared mode to load saved activity."; return; }
        if (ActivityBusy) { ActivityStatus = "An activity operation is already running."; return; }
        var selectedSnapshot = SelectedActivitySnapshot;
        if (selectedSnapshot is not null && _sharedMonitoringApi is not ISharedCloudActivityHistoryApi)
        { ActivityStatus = "The shared API does not support historical activity reads."; return; }
        var organization = SelectedOrganization;
        var binding = SelectedActivityBinding;
        var identity = _identity;
        using var cancellation = new CancellationTokenSource();
        _activityCancellation = cancellation;
        ActivityBusy = true;
        try
        {
            var snapshot = selectedSnapshot is null
                ? await api.GetCloudActivityAsync(organization.Id, binding.Provider, binding.ExternalTenantId, cancellation.Token)
                : await ((ISharedCloudActivityHistoryApi)api).GetCloudActivitySnapshotAsync(organization.Id, binding.Provider,
                    binding.ExternalTenantId, selectedSnapshot.SnapshotId, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (SelectedOrganization != organization || SelectedActivityBinding != binding || _identity != identity) return;
            ActivityActions = CloudActivityAnalysis.Analyze(snapshot);
            _loadedCloudActivitySnapshot = snapshot;
            await CorrelateActivityAsync(snapshot, binding, cancellation.Token);
            if (SelectedOrganization != organization || SelectedActivityBinding != binding || _identity != identity) return;
            ActivityStatus = $"Saved evidence through {snapshot.ThroughUtc:u}. Source: {snapshot.SourceScope ?? snapshot.Source}. {snapshot.CoverageDetails} Truncated: {snapshot.Truncated}. No live cloud call was made.";
        }
        catch (Exception exception)
        {
            if (SelectedOrganization == organization && SelectedActivityBinding == binding && _identity == identity)
                ActivityStatus = $"Saved activity read failed: {exception.Message}";
        }
        finally { _activityCancellation = null; ActivityBusy = false; }
    }

    public async Task LoadCloudActivityHistoryAsync(bool older = false)
    {
        if (!CanReadMonitoring || SelectedOrganization is null || SelectedActivityBinding is null
            || _sharedMonitoringApi is not ISharedCloudActivityHistoryApi api)
        { ActivityStatus = "Select a bound account with shared inventory/finding access to browse saved activity history."; return; }
        if (ActivityBusy) { ActivityStatus = "An activity operation is already running."; return; }
        if (older && _activityHistoryCursor is null) { ActivityStatus = "No older page is available. Refresh history for newer saved evidence."; return; }
        if (!older) SelectedActivitySnapshot = null;
        var organization = SelectedOrganization;
        var binding = SelectedActivityBinding;
        var identity = _identity;
        using var cancellation = new CancellationTokenSource();
        _activityCancellation = cancellation;
        ActivityBusy = true;
        try
        {
            var page = await api.GetCloudActivityHistoryAsync(organization.Id, binding.Provider, binding.ExternalTenantId,
                older ? _activityHistoryCursor : null, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (SelectedOrganization != organization || SelectedActivityBinding != binding || _identity != identity) return;
            ActivityHistory = older ? ActivityHistory.Concat(page.Snapshots).DistinctBy(item => item.SnapshotId).ToArray() : page.Snapshots;
            _activityHistoryCursor = page.NextBeforeSnapshotId;
            OnPropertyChanged(nameof(HasOlderActivityHistory));
            ActivityStatus = $"{ActivityHistory.Count} saved snapshot summaries loaded. Select a snapshot then load saved evidence. No live cloud call or historical grant-use inference was made.";
        }
        catch (Exception exception)
        {
            if (SelectedOrganization == organization && SelectedActivityBinding == binding && _identity == identity)
                ActivityStatus = $"Saved activity history read failed: {exception.Message}";
        }
        finally { _activityCancellation = null; ActivityBusy = false; }
    }
    private async Task CorrelateActivityAsync(CloudActivitySnapshot snapshot, TenantBinding binding, CancellationToken token)
    {
        ActivityProvenance = snapshot.Provenance;
        var inventory = await _activityInventory.GetSnapshotAsync(token);
        token.ThrowIfCancellationRequested();
        if (SelectedActivityBinding != binding) return;
        var account = inventory.Accounts.SingleOrDefault(item => item.Id == binding.CloudAccountId && item.Provider == binding.Provider
            && string.Equals(item.ExternalId, binding.ExternalTenantId, binding.Provider == Domain.CloudProvider.Azure ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        if (_activityInventory.IsDemoMode || account is null)
        {
            ActivityCorrelations = Array.Empty<ActivityEntitlementCorrelation>();
            ActivityAttributionStatus = "Attribution unavailable: no matching non-demo provider/account inventory for this binding. No grant usage is inferred.";
            return;
        }
        var report = new ActivityEntitlementAttributionService().Correlate(snapshot, account, inventory.Principals,
            inventory.Resources, inventory.Permissions, inventory.EffectiveAccess);
        ActivityCorrelations = report.Observations;
        ActivityAttributionStatus = report.Notice;
    }
}
