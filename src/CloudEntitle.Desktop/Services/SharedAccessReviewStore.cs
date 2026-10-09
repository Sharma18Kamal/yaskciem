using CloudEntitle.IdentityGovernance.Models;

namespace CloudEntitle.Desktop.Services;

public sealed class SharedAccessReviewStore(ISharedOrganizationApi api, AuthenticatedOrganizationContext authenticatedContext) : IAccessReviewStore
{
    public Task<IReadOnlyList<AccessReviewCampaign>> GetAsync(CancellationToken cancellationToken = default)
    {
        var organizationId = authenticatedContext.OrganizationId
            ?? throw new UnauthorizedAccessException("Select and sign in to an organization to access shared reviews.");
        return api.GetAccessReviewsAsync(organizationId, cancellationToken);
    }

    public Task SaveAsync(AccessReviewCampaign campaign, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        var organizationId = authenticatedContext.OrganizationId
            ?? throw new UnauthorizedAccessException("Select and sign in to an organization to modify shared reviews.");
        return api.SaveAccessReviewAsync(organizationId, campaign, cancellationToken);
    }
}
