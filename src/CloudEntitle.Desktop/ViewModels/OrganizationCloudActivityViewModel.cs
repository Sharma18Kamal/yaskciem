using CloudEntitle.Desktop.Services;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.OrganizationManagement.Models;

namespace CloudEntitle.Desktop.ViewModels;

public sealed partial class OrganizationViewModel
{
    private readonly ICloudActivityCollector? _activityCollector;
    private TenantBinding? _activityBinding;
    private string _activityRegion = "", _activityProfile = "", _activityStatus = "Select a bound account. Activity is collected only after explicit consent.";
    private bool _activityConsent, _activityBusy, _endpointConfirmed;
    private CancellationTokenSource? _activityCancellation;
    private IReadOnlyList<CloudActivityActionUsage> _activityActions = Array.Empty<CloudActivityActionUsage>();
    private AsyncRelayCommand? _collectActivityCommand, _loadActivityCommand, _cancelActivityCommand;

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
            OnPropertyChanged(nameof(ActivitySetupInstructions));
        }
    }
    public string ActivityRegion { get => _activityRegion; set { if (SetProperty(ref _activityRegion, value)) ActivityReadConsent = false; } }
    public string ActivityProfile { get => _activityProfile; set { if (SetProperty(ref _activityProfile, value)) ActivityReadConsent = false; } }
    public bool ActivityReadConsent { get => _activityConsent; set => SetProperty(ref _activityConsent, value); }
    public bool ActivityBusy { get => _activityBusy; private set => SetProperty(ref _activityBusy, value); }
    public string ActivityStatus { get => _activityStatus; private set => SetProperty(ref _activityStatus, value); }
    public IReadOnlyList<CloudActivityActionUsage> ActivityActions { get => _activityActions; private set => SetProperty(ref _activityActions, value); }
    public bool NotificationEndpointConfirmed { get => _endpointConfirmed; set => SetProperty(ref _endpointConfirmed, value); }
    public string ActivitySetupInstructions => SelectedActivityBinding?.Provider switch
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
        ActivityReadConsent = false;
        ActivityActions = Array.Empty<CloudActivityActionUsage>();
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
        using var cancellation = new CancellationTokenSource();
        _activityCancellation = cancellation;
        ActivityBusy = true;
        ActivityReadConsent = false;
        ActivityStatus = "Reading the selected account's last 90 days. No endpoints or permissions will be created or changed.";
        try
        {
            var snapshot = await _activityCollector.CollectAsync(new(binding.Provider, binding.ExternalTenantId,
                ActivityRegion.Trim(), ActivityProfile.Trim(), DateTimeOffset.UtcNow), cancellation.Token);
            if (SelectedOrganization != organization || SelectedActivityBinding != binding || _identity != identity) return;
            await api.UploadCloudActivityAsync(organization.Id, snapshot, cancellation.Token);
            if (SelectedOrganization != organization || SelectedActivityBinding != binding || _identity != identity) return;
            ActivityActions = CloudActivityAnalysis.Analyze(snapshot);
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
        var organization = SelectedOrganization;
        var binding = SelectedActivityBinding;
        var identity = _identity;
        using var cancellation = new CancellationTokenSource();
        _activityCancellation = cancellation;
        ActivityBusy = true;
        try
        {
            var snapshot = await api.GetCloudActivityAsync(organization.Id, binding.Provider, binding.ExternalTenantId, cancellation.Token);
            if (SelectedOrganization != organization || SelectedActivityBinding != binding || _identity != identity) return;
            ActivityActions = CloudActivityAnalysis.Analyze(snapshot);
            ActivityStatus = $"Saved evidence through {snapshot.ThroughUtc:u}. {snapshot.CoverageDetails} Truncated: {snapshot.Truncated}. No live cloud call was made.";
        }
        catch (Exception exception)
        {
            if (SelectedOrganization == organization && SelectedActivityBinding == binding && _identity == identity)
                ActivityStatus = $"Saved activity read failed: {exception.Message}";
        }
        finally { _activityCancellation = null; ActivityBusy = false; }
    }
}
