using CloudEntitle.Desktop.Services;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.OrganizationManagement.Models;

namespace CloudEntitle.Desktop.ViewModels;

public sealed partial class OrganizationViewModel
{
    private CancellationTokenSource? _priorityCancellation;
    private bool _priorityBusy, _jitEmergency, _jitLiveConsent, _jitRevokeConsent;
    private string _jitTarget = "", _jitReason = "", _priorityStatus = "Configure/connect Azure PIM, an AWS human-session broker or GCP native PAM on the server. No live JIT request starts automatically.";
    private int _jitMinutes = 60;
    private PrivilegedAccessRequest? _selectedJitRequest;
    private IReadOnlyList<PrivilegedAccessRequest> _jitRequests = [];
    private IReadOnlyList<PrivilegedAccessAudit> _jitAudit = [];
    private IReadOnlyList<IdentityBehaviorAnomaly> _behaviorAnomalies = [];
    private IReadOnlyList<ObservedEntitlementChange> _observedChanges = [];
    private AsyncRelayCommand? _requestJit, _refreshJit, _approveJit, _rejectJit, _activateJit, _revokeJit, _handoffJit, _auditJit, _analyzeSecurity;
    public string JitTarget { get => _jitTarget; set { if (SetProperty(ref _jitTarget, value)) JitLiveConsent = false; } }
    public string JitReason { get => _jitReason; set { if (SetProperty(ref _jitReason, value)) { JitLiveConsent = false; JitRevokeConsent = false; } } }
    public int JitDurationMinutes { get => _jitMinutes; set { if (SetProperty(ref _jitMinutes, value)) JitLiveConsent = false; } }
    public bool JitEmergency { get => _jitEmergency; set { if (SetProperty(ref _jitEmergency, value)) JitLiveConsent = false; } }
    public bool JitLiveConsent { get => _jitLiveConsent; set => SetProperty(ref _jitLiveConsent, value); }
    public bool JitRevokeConsent { get => _jitRevokeConsent; set => SetProperty(ref _jitRevokeConsent, value); }
    public bool PriorityTwoBusy { get => _priorityBusy; private set => SetProperty(ref _priorityBusy, value); }
    public string PriorityTwoStatus { get => _priorityStatus; private set => SetProperty(ref _priorityStatus, value); }
    public PrivilegedAccessRequest? SelectedJitRequest
    {
        get => _selectedJitRequest;
        set { if (SetProperty(ref _selectedJitRequest, value)) { _priorityCancellation?.Cancel(); JitLiveConsent = false; JitRevokeConsent = false; JitAudit = []; } }
    }
    public IReadOnlyList<PrivilegedAccessRequest> JitRequests { get => _jitRequests; private set => SetProperty(ref _jitRequests, value); }
    public IReadOnlyList<PrivilegedAccessAudit> JitAudit { get => _jitAudit; private set => SetProperty(ref _jitAudit, value); }
    public IReadOnlyList<IdentityBehaviorAnomaly> BehaviorAnomalies { get => _behaviorAnomalies; private set => SetProperty(ref _behaviorAnomalies, value); }
    public IReadOnlyList<ObservedEntitlementChange> ObservedDocumentChanges { get => _observedChanges; private set => SetProperty(ref _observedChanges, value); }
    public AsyncRelayCommand RequestJitCommand => _requestJit ??= new(() => RunPriorityTwoAsync("request"));
    public AsyncRelayCommand RefreshJitCommand => _refreshJit ??= new(() => RunPriorityTwoAsync("refresh"));
    public AsyncRelayCommand ApproveJitCommand => _approveJit ??= new(() => RunPriorityTwoAsync("approve"));
    public AsyncRelayCommand RejectJitCommand => _rejectJit ??= new(() => RunPriorityTwoAsync("reject"));
    public AsyncRelayCommand ActivateJitCommand => _activateJit ??= new(() => RunPriorityTwoAsync("activate"));
    public AsyncRelayCommand RevokeJitCommand => _revokeJit ??= new(() => RunPriorityTwoAsync("revoke"));
    public AsyncRelayCommand OpenJitSessionCommand => _handoffJit ??= new(() => RunPriorityTwoAsync("handoff"));
    public AsyncRelayCommand JitAuditCommand => _auditJit ??= new(() => RunPriorityTwoAsync("audit"));
    public AsyncRelayCommand AnalyzeSavedSecurityCommand => _analyzeSecurity ??= new(() => RunPriorityTwoAsync("analyze"));

    private void ResetPriorityTwo()
    {
        _priorityCancellation?.Cancel();
        JitLiveConsent = false;
        JitRevokeConsent = false;
        SelectedJitRequest = null;
        JitRequests = [];
        JitAudit = [];
        BehaviorAnomalies = [];
        ObservedDocumentChanges = [];
        PriorityTwoStatus = "No JIT or behavior evidence for this selection. Client configuration and explicit connection are required.";
    }

    public async Task RunPriorityTwoAsync(string operation)
    {
        if (operation is not ("request" or "refresh" or "approve" or "reject" or "activate" or "revoke" or "handoff" or "audit" or "analyze"))
            throw new ArgumentException("Unknown Priority Two operation.", nameof(operation));
        if (!CanReadMonitoring || SelectedOrganization is null || _sharedMonitoringApi is not ISharedPriorityTwoApi api)
        { PriorityTwoStatus = "Shared inventory/finding access and a selected organization are required."; return; }
        if (PriorityTwoBusy) { PriorityTwoStatus = "A Priority Two operation is already running."; return; }
        if (operation is "approve" or "reject" or "analyze" && !IsAllowed(OrganizationAction.ManageRemediations))
        { PriorityTwoStatus = "ManageRemediations is required."; return; }
        var selected = SelectedJitRequest;
        if (operation is "approve" or "reject" or "activate" or "revoke" or "handoff" or "audit" && selected is null)
        { PriorityTwoStatus = "Select a saved JIT request first."; return; }
        if (operation == "activate" && (!JitLiveConsent || selected!.RequestedBy != _identity?.UserId || selected.Status != "Approved"))
        { PriorityTwoStatus = "Only the requester can activate after both approvals and explicit live consent."; return; }
        if (operation == "revoke" && (!JitRevokeConsent || string.IsNullOrWhiteSpace(JitReason)
            || selected!.RequestedBy != _identity?.UserId && !IsAllowed(OrganizationAction.ManageRemediations)))
        { PriorityTwoStatus = "Ending access needs explicit consent, a reason, and requester or authorized configured approver access."; return; }
        if (operation == "handoff")
        {
            if (selected!.Provider != Domain.CloudProvider.Aws || selected.Status != "Active" || selected.RevokeId is not null
                || selected.RequestedBy != _identity?.UserId || !Uri.TryCreate(selected.BrokerEndpoint, UriKind.Absolute, out var endpoint)
                || endpoint.Scheme != "https" || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            { PriorityTwoStatus = "Only the requester can open an active AWS broker human session."; return; }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(endpoint.ToString().TrimEnd('/') + $"/sessions/{selected.RequestId:D}") { UseShellExecute = true });
            PriorityTwoStatus = "Authenticate directly to the client-owned broker as the requester. No cloud credentials are returned to this app.";
            return;
        }
        if (operation == "request" && (string.IsNullOrWhiteSpace(JitTarget) || string.IsNullOrWhiteSpace(JitReason)
            || JitDurationMinutes is < 1 or > 60 || JitEmergency && JitDurationMinutes > 15))
        { PriorityTwoStatus = "Enter a connected target alias, justification and duration (1-60 minutes; emergency at most 15)."; return; }
        var organization = SelectedOrganization;
        var identity = _identity;
        var binding = SelectedActivityBinding;
        var activity = _loadedCloudActivitySnapshot?.SnapshotId;
        var document = _loadedEntitlementDocuments?.SnapshotId;
        var create = new CreatePrivilegedAccessRequest(JitTarget.Trim(), JitDurationMinutes, JitEmergency, JitReason.Trim());
        var decision = new PrivilegedAccessDecision(selected?.Version ?? 0, JitReason.Trim(), operation == "revoke" ? JitRevokeConsent : JitLiveConsent);
        using var cancellation = new CancellationTokenSource();
        _priorityCancellation = cancellation;
        PriorityTwoBusy = true;
        JitLiveConsent = false;
        JitRevokeConsent = false;
        bool Current() => !cancellation.IsCancellationRequested && SelectedOrganization == organization && _identity == identity
            && (operation != "analyze" || SelectedActivityBinding == binding);
        try
        {
            if (operation == "refresh")
            {
                var rows = await api.GetPrivilegedAccessAsync(organization.Id, cancellation.Token);
                if (!Current()) return;
                SelectedJitRequest = null;
                JitRequests = rows;
            }
            else if (operation == "audit")
            {
                var rows = await api.GetPrivilegedAccessAuditAsync(organization.Id, selected!.RequestId, cancellation.Token);
                if (!Current()) return;
                JitAudit = rows;
            }
            else if (operation == "analyze")
            {
                if (binding is null || activity is null && document is null)
                    throw new InvalidOperationException("Load saved activity or permission documents for the selected binding first.");
                var report = await api.AnalyzeSecurityEvidenceAsync(organization.Id, new(binding.Provider, binding.ExternalTenantId, activity, document), cancellation.Token);
                if (!Current()) return;
                BehaviorAnomalies = report.Anomalies;
                ObservedDocumentChanges = report.Changes;
                PriorityTwoStatus = string.Join(" ", report.Notices) + " Review events are available to existing SIEM/ticketing rules.";
                return;
            }
            else
            {
                var result = operation == "request" ? await api.RequestPrivilegedAccessAsync(organization.Id, create, cancellation.Token)
                    : await api.DecidePrivilegedAccessAsync(organization.Id, selected!.RequestId, operation, decision, cancellation.Token);
                if (!Current()) return;
                SelectedJitRequest = null;
                JitRequests = JitRequests.Where(item => item.RequestId != result.RequestId).Prepend(result).Take(100).ToArray();
            }
            PriorityTwoStatus = "Saved JIT workflow refreshed. Approval/acceptance is not verified active access; revocation requires native provider/broker evidence.";
        }
        catch (OperationCanceledException) { if (SelectedOrganization == organization && _identity == identity) PriorityTwoStatus = "Operation cancelled. Refresh requests/events to reconcile an uncertain write; do not assume activation failed."; }
        catch (Exception exception) { if (SelectedOrganization == organization && _identity == identity) PriorityTwoStatus = $"Priority Two operation failed: {exception.Message}. Refresh to reconcile an uncertain write."; }
        finally { _priorityCancellation = null; PriorityTwoBusy = false; }
    }
}
