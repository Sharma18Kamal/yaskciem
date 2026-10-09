using CloudEntitle.IdentityGovernance.Models;
using CloudEntitle.Integration.Clients;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.OrganizationManagement.Models;

namespace CloudEntitle.Desktop.Services;

public interface ISharedOrganizationApi
{
    Task<IReadOnlyList<OrganizationProfile>> GetOrganizationsAsync(CancellationToken cancellationToken = default);
    Task<OrganizationWorkspaceDto> GetWorkspaceAsync(Guid organizationId, CancellationToken cancellationToken = default);
    Task<OrganizationProfile> CreateOrganizationAsync(string name, string tenantKey, CancellationToken cancellationToken = default);
    Task<OrganizationMember> InviteMemberAsync(Guid organizationId, string email, OrganizationRoleOption role, CancellationToken cancellationToken = default);
    Task<OrganizationMember> UpdateMemberRoleAsync(Guid organizationId, Guid memberId, OrganizationRoleOption role, CancellationToken cancellationToken = default);
    Task<OrganizationMember> SetMemberActiveAsync(Guid organizationId, Guid memberId, bool isActive, CancellationToken cancellationToken = default);
    Task<TenantBinding> BindAccountAsync(Guid organizationId, BindOrganizationAccountRequest request, CancellationToken cancellationToken = default);
    Task<OrganizationCustomRole> SaveCustomRoleAsync(Guid organizationId, Guid? roleId, string name, IReadOnlyList<OrganizationAction> permissions, CancellationToken cancellationToken = default);
    Task DeleteCustomRoleAsync(Guid organizationId, Guid roleId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AccessReviewCampaign>> GetAccessReviewsAsync(Guid organizationId, CancellationToken cancellationToken = default);
    Task SaveAccessReviewAsync(Guid organizationId, AccessReviewCampaign campaign, CancellationToken cancellationToken = default);
    Task<SharedScanSummaryDto> UploadScanSummaryAsync(Guid organizationId, UploadSharedScanSummaryRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SharedScanSummaryDto>> GetScanSummariesAsync(Guid organizationId, int limit = 30, CancellationToken cancellationToken = default);
}

public sealed class SharedOrganizationApi(CiemApiClient client, IApiAccessTokenProvider tokenProvider) : ISharedOrganizationApi
{
    public async Task<IReadOnlyList<OrganizationProfile>> GetOrganizationsAsync(CancellationToken cancellationToken = default)
        => await RequireDataAsync(client.GetOrganizationsAsync(await TokenAsync(cancellationToken), cancellationToken));

    public async Task<OrganizationWorkspaceDto> GetWorkspaceAsync(Guid organizationId, CancellationToken cancellationToken = default)
        => await RequireDataAsync(client.GetOrganizationWorkspaceAsync(organizationId, await TokenAsync(cancellationToken), cancellationToken));

    public async Task<OrganizationProfile> CreateOrganizationAsync(string name, string tenantKey, CancellationToken cancellationToken = default)
        => await RequireDataAsync(client.CreateOrganizationAsync(new CreateOrganizationRequest(name, tenantKey), await TokenAsync(cancellationToken), cancellationToken));

    public async Task<OrganizationMember> InviteMemberAsync(Guid organizationId, string email, OrganizationRoleOption role, CancellationToken cancellationToken = default)
        => await RequireDataAsync(client.InviteOrganizationMemberAsync(organizationId, new InviteOrganizationMemberRequest(email, role), await TokenAsync(cancellationToken), cancellationToken));

    public async Task<OrganizationMember> UpdateMemberRoleAsync(Guid organizationId, Guid memberId, OrganizationRoleOption role, CancellationToken cancellationToken = default)
        => await RequireDataAsync(client.UpdateOrganizationMemberRoleAsync(organizationId, memberId, new UpdateOrganizationMemberRoleRequest(role), await TokenAsync(cancellationToken), cancellationToken));

    public async Task<OrganizationMember> SetMemberActiveAsync(Guid organizationId, Guid memberId, bool isActive, CancellationToken cancellationToken = default)
        => await RequireDataAsync(client.SetOrganizationMemberStatusAsync(organizationId, memberId, new SetOrganizationMemberStatusRequest(isActive), await TokenAsync(cancellationToken), cancellationToken));

    public async Task<TenantBinding> BindAccountAsync(Guid organizationId, BindOrganizationAccountRequest request, CancellationToken cancellationToken = default)
        => await RequireDataAsync(client.BindOrganizationAccountAsync(organizationId, request, await TokenAsync(cancellationToken), cancellationToken));

    public async Task<OrganizationCustomRole> SaveCustomRoleAsync(Guid organizationId, Guid? roleId, string name, IReadOnlyList<OrganizationAction> permissions, CancellationToken cancellationToken = default)
        => await RequireDataAsync(client.SaveOrganizationCustomRoleAsync(organizationId, roleId, new SaveOrganizationCustomRoleRequest(name, permissions), await TokenAsync(cancellationToken), cancellationToken));

    public async Task DeleteCustomRoleAsync(Guid organizationId, Guid roleId, CancellationToken cancellationToken = default)
        => await client.DeleteOrganizationCustomRoleAsync(organizationId, roleId, await TokenAsync(cancellationToken), cancellationToken);

    public async Task<IReadOnlyList<AccessReviewCampaign>> GetAccessReviewsAsync(Guid organizationId, CancellationToken cancellationToken = default)
        => await RequireDataAsync(client.GetSharedAccessReviewsAsync(organizationId, await TokenAsync(cancellationToken), cancellationToken));

    public async Task SaveAccessReviewAsync(Guid organizationId, AccessReviewCampaign campaign, CancellationToken cancellationToken = default)
        => _ = await RequireDataAsync(client.SaveSharedAccessReviewAsync(organizationId, campaign, await TokenAsync(cancellationToken), cancellationToken));

    public async Task<SharedScanSummaryDto> UploadScanSummaryAsync(Guid organizationId, UploadSharedScanSummaryRequest request, CancellationToken cancellationToken = default)
        => await RequireDataAsync(client.UploadSharedScanSummaryAsync(organizationId, request, await TokenAsync(cancellationToken), cancellationToken));

    public async Task<IReadOnlyList<SharedScanSummaryDto>> GetScanSummariesAsync(Guid organizationId, int limit = 30, CancellationToken cancellationToken = default)
        => await RequireDataAsync(client.GetSharedScanSummariesAsync(organizationId, limit, await TokenAsync(cancellationToken), cancellationToken));

    private Task<string> TokenAsync(CancellationToken cancellationToken) => tokenProvider.GetApiAccessTokenAsync(cancellationToken);

    private static async Task<T> RequireDataAsync<T>(Task<ApiEnvelope<T>?> response)
    {
        var envelope = await response;
        if (envelope is null) throw new InvalidOperationException("The shared API returned an empty response envelope.");
        return envelope.Data;
    }
}
