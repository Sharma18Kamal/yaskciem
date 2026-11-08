using System.Globalization;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.OrganizationManagement.Models;

namespace CloudEntitle.Desktop.ViewModels;

public sealed partial class OrganizationViewModel
{
    private string _emergencyPrincipal = "", _emergencyStart = "", _emergencyEnd = "", _emergencyReason = "", _emergencyReference = "",
        _emergencyReviewReason = "", _emergencyClosureReference = "", _emergencyStatus = "Use client-owned emergency accounts during an outage; report and review after reconnecting. This app never issues offline access.";
    private bool _emergencyBusy, _emergencyClientClosure;
    private Guid _emergencyReportId = Guid.NewGuid();
    private CreateBreakGlassReportRequest? _pendingEmergencyInput;
    private CancellationTokenSource? _emergencyCancellation;
    private BreakGlassReport? _selectedEmergencyReport;
    private IReadOnlyList<BreakGlassReport> _emergencyReports = [];
    private AsyncRelayCommand? _reportEmergency, _refreshEmergency, _reviewEmergency;
    public string EmergencyPrincipal { get => _emergencyPrincipal; set { if (SetProperty(ref _emergencyPrincipal, value)) _emergencyReportId = Guid.NewGuid(); } }
    public string EmergencyStartUtc { get => _emergencyStart; set { if (SetProperty(ref _emergencyStart, value)) _emergencyReportId = Guid.NewGuid(); } }
    public string EmergencyEndUtc { get => _emergencyEnd; set { if (SetProperty(ref _emergencyEnd, value)) _emergencyReportId = Guid.NewGuid(); } }
    public string EmergencyReason { get => _emergencyReason; set { if (SetProperty(ref _emergencyReason, value)) _emergencyReportId = Guid.NewGuid(); } }
    public string EmergencyIncidentReference { get => _emergencyReference; set { if (SetProperty(ref _emergencyReference, value)) _emergencyReportId = Guid.NewGuid(); } }
    public string EmergencyReviewReason { get => _emergencyReviewReason; set { if (SetProperty(ref _emergencyReviewReason, value)) EmergencyClientVerifiedClosure = false; } }
    public string EmergencyClosureReference { get => _emergencyClosureReference; set { if (SetProperty(ref _emergencyClosureReference, value)) EmergencyClientVerifiedClosure = false; } }
    public bool EmergencyClientVerifiedClosure { get => _emergencyClientClosure; set => SetProperty(ref _emergencyClientClosure, value); }
    public bool EmergencyReportBusy { get => _emergencyBusy; private set => SetProperty(ref _emergencyBusy, value); }
    public string EmergencyReportStatus { get => _emergencyStatus; private set => SetProperty(ref _emergencyStatus, value); }
    public IReadOnlyList<BreakGlassReport> EmergencyReports { get => _emergencyReports; private set => SetProperty(ref _emergencyReports, value); }
    public BreakGlassReport? SelectedEmergencyReport
    {
        get => _selectedEmergencyReport;
        set { if (SetProperty(ref _selectedEmergencyReport, value)) { _emergencyCancellation?.Cancel(); EmergencyClientVerifiedClosure = false; } }
    }
    public AsyncRelayCommand ReportEmergencyCommand => _reportEmergency ??= new(() => RunBreakGlassAsync("report"));
    public AsyncRelayCommand RefreshEmergencyCommand => _refreshEmergency ??= new(() => RunBreakGlassAsync("refresh"));
    public AsyncRelayCommand ReviewEmergencyCommand => _reviewEmergency ??= new(() => RunBreakGlassAsync("review"));

    private void ResetBreakGlassReports()
    {
        _emergencyCancellation?.Cancel();
        SelectedEmergencyReport = null;
        EmergencyReports = [];
        EmergencyPrincipal = "";
        EmergencyStartUtc = "";
        EmergencyEndUtc = "";
        EmergencyReason = "";
        EmergencyIncidentReference = "";
        EmergencyReviewReason = "";
        EmergencyClosureReference = "";
        EmergencyClientVerifiedClosure = false;
        _pendingEmergencyInput = null;
        EmergencyReportStatus = "No emergency reports for this selection. Client-owned access only; no offline approval bypass.";
    }

    public async Task RunBreakGlassAsync(string operation)
    {
        if (operation is not ("report" or "refresh" or "review")) throw new ArgumentException("Unknown emergency-report operation.", nameof(operation));
        if (!CanReadMonitoring || _sharedMonitoringApi is not ISharedBreakGlassApi api || SelectedOrganization is null)
        { EmergencyReportStatus = "Select a shared organization with inventory/finding access."; return; }
        if (EmergencyReportBusy) { EmergencyReportStatus = "An emergency-report operation is already running."; return; }
        if (operation != "refresh" && !IsAllowed(OrganizationAction.ManageRemediations))
        { EmergencyReportStatus = "ManageRemediations is required to report or review client emergency access."; return; }
        var selected = SelectedEmergencyReport;
        if (operation == "review" && (selected is null || selected.Review is not null || selected.ReportedBy == _identity?.UserId))
        { EmergencyReportStatus = "Select an unreviewed report submitted by a different identity; configured security-reviewer authorization is required."; return; }
        var organization = SelectedOrganization;
        var identity = _identity;
        var binding = SelectedActivityBinding;
        CreateBreakGlassReportRequest? create = null;
        if (operation == "report")
        {
            if (binding is null || string.IsNullOrWhiteSpace(EmergencyPrincipal) || string.IsNullOrWhiteSpace(EmergencyReason)
                || string.IsNullOrWhiteSpace(EmergencyIncidentReference)
                || !DateTimeOffset.TryParseExact(EmergencyStartUtc, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start)
                || !DateTimeOffset.TryParseExact(EmergencyEndUtc, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var end)
                || start >= end || end > DateTimeOffset.UtcNow.AddMinutes(1) || end - start > TimeSpan.FromDays(7))
            { EmergencyReportStatus = "Choose a bound cloud account, exact external principal, reason, opaque incident reference and completed UTC interval (yyyy-MM-ddTHH:mm:ssZ; at most seven days)."; return; }
            create = new(_emergencyReportId, binding.Provider, binding.ExternalTenantId, EmergencyPrincipal.Trim(), start, end,
                EmergencyReason.Trim(), EmergencyIncidentReference.Trim(), _loadedCloudActivitySnapshot?.SnapshotId);
            if (_pendingEmergencyInput is not null && create != _pendingEmergencyInput)
            {
                _emergencyReportId = Guid.NewGuid();
                create = create with { ReportId = _emergencyReportId };
            }
            _pendingEmergencyInput = create;
        }
        var review = new ReviewBreakGlassReportRequest(EmergencyReviewReason.Trim(), EmergencyClosureReference.Trim(), EmergencyClientVerifiedClosure);
        using var cancellation = new CancellationTokenSource();
        _emergencyCancellation = cancellation;
        EmergencyReportBusy = true;
        EmergencyClientVerifiedClosure = false;
        bool Current() => !cancellation.IsCancellationRequested && SelectedOrganization == organization && _identity == identity
            && (operation != "report" || SelectedActivityBinding == binding);
        try
        {
            if (operation == "refresh")
            {
                var reports = await api.GetBreakGlassReportsAsync(organization.Id, cancellation.Token);
                if (!Current()) return;
                SelectedEmergencyReport = null;
                EmergencyReports = reports;
            }
            else
            {
                var result = operation == "report" ? await api.ReportBreakGlassAsync(organization.Id, create!, cancellation.Token)
                    : await api.ReviewBreakGlassAsync(organization.Id, selected!.ReportId, review, cancellation.Token);
                if (!Current()) return;
                SelectedEmergencyReport = null;
                EmergencyReports = EmergencyReports.Where(item => item.ReportId != result.ReportId).Prepend(result).Take(100).ToArray();
                if (operation == "report") { _emergencyReportId = Guid.NewGuid(); _pendingEmergencyInput = null; }
            }
            EmergencyReportStatus = "Saved client-owned emergency report/review. Activity matches and client closure attestations do not prove native permission or session revocation.";
        }
        catch (OperationCanceledException)
        { if (SelectedOrganization == organization && _identity == identity) EmergencyReportStatus = "Emergency-report operation cancelled. Refresh before repeating an uncertain submission."; }
        catch (Exception exception)
        { if (SelectedOrganization == organization && _identity == identity) EmergencyReportStatus = $"Emergency-report operation failed: {exception.Message}. Refresh to reconcile uncertain submission; report ID {create?.ReportId}."; }
        finally { _emergencyCancellation = null; EmergencyReportBusy = false; }
    }
}
