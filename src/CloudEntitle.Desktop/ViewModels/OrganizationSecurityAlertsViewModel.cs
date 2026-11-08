using CloudEntitle.Desktop.Services;
using CloudEntitle.Integration.Contracts;

namespace CloudEntitle.Desktop.ViewModels;

public sealed partial class OrganizationViewModel
{
    private string _securityAlertProduct = "Splunk", _securityAlertChannel = "security";
    private string _securityAlertStatus = "Please provision an authenticated client bridge on demand. Imported SIEM alerts do not approve or execute cloud changes.";
    private IReadOnlyList<StoredSecurityAlert> _securityAlerts = Array.Empty<StoredSecurityAlert>();
    private AsyncRelayCommand? _refreshSecurityAlerts;
    private bool _securityAlertBusy;
    private int _securityAlertRevision;
    public IReadOnlyList<string> SecurityAlertProducts => InboundSecurityProducts.Supported;
    public string SecurityAlertProduct { get => _securityAlertProduct; set { if (SetProperty(ref _securityAlertProduct, value)) ResetSecurityAlerts(); } }
    public string SecurityAlertChannel { get => _securityAlertChannel; set { if (SetProperty(ref _securityAlertChannel, value)) ResetSecurityAlerts(); } }
    public string SecurityAlertStatus { get => _securityAlertStatus; private set => SetProperty(ref _securityAlertStatus, value); }
    public IReadOnlyList<StoredSecurityAlert> SecurityAlerts { get => _securityAlerts; private set => SetProperty(ref _securityAlerts, value); }
    public AsyncRelayCommand RefreshSecurityAlertsCommand => _refreshSecurityAlerts ??= new(RefreshSecurityAlertsAsync);

    private void ResetSecurityAlerts()
    {
        _securityAlertRevision++;
        SecurityAlerts = Array.Empty<StoredSecurityAlert>();
        SecurityAlertStatus = "Please create/configure the client SIEM bridge and enable its server organization alias on demand. Refresh reads saved alerts only; no live SIEM endpoint is contacted.";
    }

    public async Task RefreshSecurityAlertsAsync()
    {
        if (!CanReadMonitoring || SelectedOrganization is null || _sharedMonitoringApi is not ISharedSecurityAlertApi api)
        { SecurityAlertStatus = "Shared mode and inventory/finding access are required to read inbound alerts."; return; }
        if (!InboundSecurityProducts.Supported.Contains(SecurityAlertProduct) || !InboundSecurityProducts.IsChannel(SecurityAlertChannel))
        { SecurityAlertStatus = "Choose a supported product and a safe configured channel alias."; return; }
        if (_securityAlertBusy) { SecurityAlertStatus = "An alert read is already running."; return; }
        var revision = _securityAlertRevision;
        var organization = SelectedOrganization;
        var identity = _identity;
        _securityAlertBusy = true;
        try
        {
            var result = await api.GetSecurityAlertsAsync(organization.Id, SecurityAlertProduct, SecurityAlertChannel);
            if (_securityAlertRevision != revision || SelectedOrganization != organization || _identity != identity) return;
            SecurityAlerts = result;
            SecurityAlertStatus = $"{result.Count} saved latest alert revisions. Untrusted SIEM evidence only: no role, permission, finding resolution or response approval was changed.";
        }
        catch (Exception exception)
        {
            if (_securityAlertRevision == revision && SelectedOrganization == organization && _identity == identity)
                SecurityAlertStatus = $"Inbound alert read failed: {exception.Message}";
        }
        finally { _securityAlertBusy = false; }
    }
}
