using CloudEntitle.Domain;
using CloudEntitle.OrganizationManagement.Models;
using CloudEntitle.OrganizationManagement.Services;
using Xunit;

namespace CloudEntitle.OrganizationManagement.Tests;

public sealed class OrganizationManagementTests
{
    [Fact]
    public void Organization_ShouldBindAccountsAndExposeProviderScope()
    {
        var service = new OrganizationManagementService();
        var organization = service.CreateOrganization("Acme Security", "acme-tenant");
        var accountId = Guid.NewGuid();
        service.BindCloudAccount(organization.Id, CloudProvider.Azure, accountId, "tenant-1", "Production Azure");
        service.BindCloudAccount(organization.Id, CloudProvider.Aws, Guid.NewGuid(), "org-1", "Production AWS");

        var scope = service.GetScope(organization.Id);

        Assert.Equal(2, scope.CloudAccountIds.Count);
        Assert.Contains(CloudProvider.Azure, scope.Providers);
        Assert.Contains(CloudProvider.Aws, scope.Providers);
    }

    [Fact]
    public void Roles_ShouldAuthorizeOnlyPermittedOrganizationActions()
    {
        var service = new OrganizationManagementService();
        var organization = service.CreateOrganization("Acme Security", "acme-tenant");
        var viewerId = Guid.NewGuid();
        var analystId = Guid.NewGuid();
        service.AddMember(organization.Id, viewerId, "viewer@example.invalid", OrganizationMemberRole.Viewer);
        service.AddMember(organization.Id, analystId, "analyst@example.invalid", OrganizationMemberRole.SecurityAnalyst);

        Assert.True(service.Authorize(organization.Id, viewerId, OrganizationAction.ViewFindings).Allowed);
        Assert.False(service.Authorize(organization.Id, viewerId, OrganizationAction.RunScan).Allowed);
        Assert.True(service.Authorize(organization.Id, analystId, OrganizationAction.RunScan).Allowed);
        Assert.False(service.Authorize(organization.Id, analystId, OrganizationAction.ManageMembers).Allowed);
    }

    [Fact]
    public void UnknownUser_ShouldBeDeniedWithoutCloudMutation()
    {
        var service = new OrganizationManagementService();
        var organization = service.CreateOrganization("Acme Security", "acme-tenant");

        var decision = service.Authorize(organization.Id, Guid.NewGuid(), OrganizationAction.ManageConnections);

        Assert.False(decision.Allowed);
        Assert.Contains("not an active", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
