using CloudEntitle.Desktop.Services;
using CloudEntitle.OrganizationManagement.Models;

namespace CloudEntitle.Desktop.ViewModels;

public sealed partial class OrganizationViewModel
{
    private AuthoritativeDecisionsViewModel? _authoritativeDecisions;
    public AuthoritativeDecisionsViewModel AuthoritativeDecisions
    {
        get
        {
            if (_authoritativeDecisions is null)
            {
                _authoritativeDecisions = new(_sharedOrganizationApi as ISharedEffectiveDecisionApi);
                PropertyChanged += (_, _) => UpdateAuthoritativeDecisionContext();
                UpdateAuthoritativeDecisionContext();
            }
            return _authoritativeDecisions;
        }
    }
    private void UpdateAuthoritativeDecisionContext()
    {
        var scope = string.Join("|", Bindings.OrderBy(b => b.Provider).ThenBy(b => b.ExternalTenantId)
            .Select(b => $"{b.Provider}:{b.ExternalTenantId}"));
        var identity = _identity is null ? "" : $"{_identity.TenantId}|{_identity.UserId}|{scope}";
        _authoritativeDecisions?.SetContext(_identity is null ? null : SelectedOrganization?.Id, identity,
            CanReadMonitoring, CanReadMonitoring && IsAllowed(OrganizationAction.ManagePolicies));
    }

    private void ResetAuthoritativeDecisions()
        => _authoritativeDecisions?.SetContext(null, "", false, false);
}
