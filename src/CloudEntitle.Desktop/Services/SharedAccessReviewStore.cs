using CloudEntitle.IdentityGovernance.Models;
using CloudEntitle.Integration.Contracts;

namespace CloudEntitle.Desktop.Services;

public sealed class SharedAccessReviewStore(ISharedOrganizationApi api, AuthenticatedOrganizationContext authenticatedContext) : IAccessReviewStore
{
    public string? AuthenticatedActorEmail => authenticatedContext.Identity?.Email;
    public bool SupportsGovernance => api is ISharedGovernanceApi;
    private Guid OrganizationId => authenticatedContext.OrganizationId
        ?? throw new UnauthorizedAccessException("Select and sign in to an organization to access shared governance.");
    private ISharedGovernanceApi GovernanceApi => api as ISharedGovernanceApi
        ?? throw new NotSupportedException("This server adapter does not support shared governance.");

    public Task<GovernanceReportDto> GetGovernanceReportAsync(CancellationToken cancellationToken = default)
        => GovernanceApi.GetGovernanceReportAsync(OrganizationId, cancellationToken);

    public Task<GovernanceAssigneesDto> GetGovernanceAssigneesAsync(CancellationToken cancellationToken = default)
        => GovernanceApi.GetGovernanceAssigneesAsync(OrganizationId, cancellationToken);

    public Task<AccessReviewCampaign> ReassignAsync(Guid campaignId, Guid itemId, ReassignReviewItemRequest request, CancellationToken cancellationToken = default)
        => GovernanceApi.ReassignReviewItemAsync(OrganizationId, campaignId, itemId, request, cancellationToken);

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
