using CloudEntitle.IdentityGovernance.Models;

namespace CloudEntitle.Desktop.Services;

public sealed class SharedAccessReviewStore(ISharedOrganizationApi api, AuthenticatedOrganizationContext authenticatedContext) : IAccessReviewStore
{
    public string? AuthenticatedActorEmail => authenticatedContext.Identity?.Email;

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

    public async Task<IReadOnlyList<string>?> GetEligibleReviewerEmailsAsync(CancellationToken cancellationToken = default)
    {
        var organizationId = authenticatedContext.OrganizationId
            ?? throw new UnauthorizedAccessException("Select and sign in to an organization to load shared reviewers.");
        return await api.GetEligibleAccessReviewersAsync(organizationId, cancellationToken);
    }
}
