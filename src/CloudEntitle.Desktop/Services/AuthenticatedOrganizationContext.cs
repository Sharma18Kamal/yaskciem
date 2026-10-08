using CloudEntitle.OrganizationManagement.Models;

namespace CloudEntitle.Desktop.Services;

public sealed class AuthenticatedOrganizationContext
{
    public event EventHandler? Changed;
    public OrganizationIdentity? Identity { get; private set; }
    public Guid? OrganizationId { get; private set; }

    public void Set(OrganizationIdentity? identity, Guid? organizationId)
    {
        Identity = identity;
        OrganizationId = identity is null ? null : organizationId;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
