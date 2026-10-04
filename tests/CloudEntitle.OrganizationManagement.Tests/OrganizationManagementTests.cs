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
        var owner = CreateIdentity(Guid.NewGuid().ToString(), "owner@example.invalid");
        var organization = service.CreateOrganization("Acme Security", "acme-tenant", owner.TenantId, owner);
        var accountId = Guid.NewGuid();
        service.BindCloudAccount(organization.Id, owner, CloudProvider.Azure, accountId, "tenant-1", "Production Azure");
        service.BindCloudAccount(organization.Id, owner, CloudProvider.Aws, Guid.NewGuid(), "org-1", "Production AWS");

        var scope = service.GetScope(organization.Id, owner);

        Assert.Equal(2, scope.CloudAccountIds.Count);
        Assert.Contains(CloudProvider.Azure, scope.Providers);
        Assert.Contains(CloudProvider.Aws, scope.Providers);
    }

    [Fact]
    public void Roles_ShouldAuthorizeOnlyPermittedOrganizationActions()
    {
        var service = new OrganizationManagementService();
        var tenantId = Guid.NewGuid().ToString();
        var owner = CreateIdentity(tenantId, "owner@example.invalid");
        var organization = service.CreateOrganization("Acme Security", "acme-tenant", tenantId, owner);
        var viewer = CreateIdentity(tenantId, "viewer@example.invalid");
        var analyst = CreateIdentity(tenantId, "analyst@example.invalid");
        service.AddMember(organization.Id, owner, viewer.Email, OrganizationMemberRole.Viewer);
        service.AddMember(organization.Id, owner, analyst.Email, OrganizationMemberRole.SecurityAnalyst);
        Assert.False(service.Authorize(organization.Id, viewer, OrganizationAction.ViewFindings).Allowed);
        Assert.NotNull(service.AcceptInvitation(organization.Id, viewer));
        Assert.NotNull(service.AcceptInvitation(organization.Id, analyst));

        Assert.True(service.Authorize(organization.Id, viewer, OrganizationAction.ViewFindings).Allowed);
        Assert.False(service.Authorize(organization.Id, viewer, OrganizationAction.RunScan).Allowed);
        Assert.True(service.Authorize(organization.Id, analyst, OrganizationAction.RunScan).Allowed);
        Assert.False(service.Authorize(organization.Id, analyst, OrganizationAction.ManageMembers).Allowed);
    }

    [Fact]
    public void UnknownUser_ShouldBeDeniedWithoutCloudMutation()
    {
        var service = new OrganizationManagementService();
        var owner = CreateIdentity(Guid.NewGuid().ToString(), "owner@example.invalid");
        var organization = service.CreateOrganization("Acme Security", "acme-tenant", owner.TenantId, owner);

        var decision = service.Authorize(organization.Id, CreateIdentity(owner.TenantId, "unknown@example.invalid"), OrganizationAction.ManageConnections);

        Assert.False(decision.Allowed);
        Assert.Contains("not an active", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OrganizationDirectory_ShouldPersistOrganizationsMembersAndBindings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "YaskCiemOrganizationTests", Guid.NewGuid().ToString("N"));
        var filePath = Path.Combine(directory, "organizations.json");
        try
        {
            var store = new LocalOrganizationStore(filePath);
            var service = new OrganizationManagementService(store);
            var owner = CreateIdentity(Guid.NewGuid().ToString(), "admin@example.invalid");
            var organization = service.CreateOrganization("Acme Security", "acme-tenant", owner.TenantId, owner);
            var member = Assert.Single(service.GetMembers(organization.Id, owner));
            var binding = service.BindCloudAccount(organization.Id, owner, CloudProvider.Azure, Guid.NewGuid(), "tenant-1", "Production Azure");

            var restored = new OrganizationManagementService(new LocalOrganizationStore(filePath));

            Assert.Equal(organization, Assert.Single(restored.GetOrganizations()));
            Assert.Equal(member, Assert.Single(restored.GetMembers(organization.Id, owner)));
            Assert.Equal(binding, Assert.Single(restored.GetTenantBindings(organization.Id, owner)));
            Assert.Contains(restored.GetAuditTrail(organization.Id, owner), entry => entry.Action == "AccountBound");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CrossTenantIdentity_ShouldNotReadOrBindOrganizationAccounts()
    {
        var service = new OrganizationManagementService();
        var owner = CreateIdentity(Guid.NewGuid().ToString(), "owner@example.invalid");
        var organization = service.CreateOrganization("Acme Security", "acme-tenant", owner.TenantId, owner);
        var outsider = CreateIdentity(Guid.NewGuid().ToString(), owner.Email);

        Assert.False(service.Authorize(organization.Id, outsider, OrganizationAction.ViewInventory).Allowed);
        Assert.Throws<UnauthorizedAccessException>(() => service.GetScope(organization.Id, outsider));
        Assert.Throws<UnauthorizedAccessException>(() => service.BindCloudAccount(organization.Id, outsider, CloudProvider.Azure, Guid.NewGuid(), "tenant-1", "Production Azure"));
    }

    [Fact]
    public void StableProviderAccountIdentity_ShouldPreventDuplicateBindingsAcrossScanIds()
    {
        var service = new OrganizationManagementService();
        var owner = CreateIdentity(Guid.NewGuid().ToString(), "owner@example.invalid");
        var organization = service.CreateOrganization("Acme Security", "acme-tenant", owner.TenantId, owner);
        service.BindCloudAccount(organization.Id, owner, CloudProvider.Azure, Guid.NewGuid(), "subscription-1", "Production Azure");

        Assert.Throws<InvalidOperationException>(() => service.BindCloudAccount(organization.Id, owner, CloudProvider.Azure, Guid.NewGuid(), "subscription-1", "Production Azure after rescan"));
    }

    [Fact]
    public void MemberManagement_ShouldRequireAdminAndRetainAnActiveAdmin()
    {
        var service = new OrganizationManagementService();
        var tenantId = Guid.NewGuid().ToString();
        var owner = CreateIdentity(tenantId, "owner@example.invalid");
        var organization = service.CreateOrganization("Acme Security", "acme-tenant", tenantId, owner);
        var member = service.AddMember(organization.Id, owner, "analyst@example.invalid", OrganizationMemberRole.SecurityAnalyst);
        var analyst = CreateIdentity(tenantId, member.Email);

        Assert.Throws<UnauthorizedAccessException>(() => service.AddMember(organization.Id, analyst, "other@example.invalid", OrganizationMemberRole.Viewer));
        Assert.False(service.Authorize(organization.Id, analyst, OrganizationAction.RunScan).Allowed);
        Assert.True(service.AcceptInvitation(organization.Id, analyst)?.IsPending == false);
        Assert.True(service.Authorize(organization.Id, analyst, OrganizationAction.RunScan).Allowed);
        Assert.Throws<InvalidOperationException>(() => service.UpdateMemberRole(organization.Id, owner, Assert.Single(service.GetMembers(organization.Id, owner), item => item.Role == OrganizationMemberRole.OrganizationAdmin).Id, OrganizationMemberRole.Viewer));
        Assert.Throws<InvalidOperationException>(() => service.SetMemberActive(organization.Id, owner, Assert.Single(service.GetMembers(organization.Id, owner), item => item.Role == OrganizationMemberRole.OrganizationAdmin).Id, false));
    }

    private static OrganizationIdentity CreateIdentity(string tenantId, string email)
        => new(Guid.NewGuid(), Guid.NewGuid().ToString("N"), tenantId, email);
}
