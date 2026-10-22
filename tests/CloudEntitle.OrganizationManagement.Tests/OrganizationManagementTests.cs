using CloudEntitle.Domain;
using CloudEntitle.OrganizationManagement.Models;
using CloudEntitle.OrganizationManagement.Services;
using Xunit;

namespace CloudEntitle.OrganizationManagement.Tests;

public sealed class OrganizationManagementTests
{
    [Fact]
    public void ReviewerInvitation_ShouldRequireMatchingSignInAndExcludeSuspendedMembers()
    {
        var service = new OrganizationManagementService();
        var owner = CreateIdentity(Guid.NewGuid().ToString(), "owner@example.invalid");
        var organization = service.CreateOrganization("Review team", "review-team", owner.TenantId, owner);
        var reviewer = CreateIdentity(owner.TenantId, "reviewer@example.invalid");
        var invitation = service.AddMember(organization.Id, owner, reviewer.Email, OrganizationMemberRole.SecurityAnalyst);

        OrganizationMember[] Reviewers() => service.GetMembersAuthorizedForAction(
            organization.Id, owner, OrganizationAction.ManageMembers, OrganizationAction.ManageRemediations).ToArray();

        Assert.True(invitation.IsPending);
        Assert.DoesNotContain(Reviewers(), member => member.Id == invitation.Id);
        Assert.False(service.Authorize(organization.Id, reviewer, OrganizationAction.ManageRemediations).Allowed);
        Assert.Null(service.AcceptInvitation(organization.Id, CreateIdentity(Guid.NewGuid().ToString(), reviewer.Email)));
        Assert.Null(service.AcceptInvitation(organization.Id, CreateIdentity(owner.TenantId, "other@example.invalid")));

        var accepted = Assert.IsType<OrganizationMember>(service.AcceptInvitation(organization.Id, reviewer));
        Assert.False(accepted.IsPending);
        Assert.Equal(reviewer.UserId, accepted.UserId);
        Assert.Contains(Reviewers(), member => member.Id == invitation.Id);
        Assert.True(service.Authorize(organization.Id, reviewer, OrganizationAction.ManageRemediations).Allowed);
        Assert.Null(service.AcceptInvitation(organization.Id, reviewer));
        Assert.Single(service.GetAuditTrail(organization.Id, owner), entry => entry.Action == "InvitationAccepted");
        Assert.Throws<UnauthorizedAccessException>(() =>
            service.AddMember(organization.Id, reviewer, "extra@example.invalid", OrganizationMemberRole.Viewer));

        service.SetMemberActive(organization.Id, owner, invitation.Id, false);
        Assert.DoesNotContain(Reviewers(), member => member.Id == invitation.Id);
        Assert.False(service.Authorize(organization.Id, reviewer, OrganizationAction.ManageRemediations).Allowed);
        Assert.Contains(service.GetAuditTrail(organization.Id, owner), entry => entry.Action == "MemberDeactivated");
    }

    [Fact]
    public void SuspendedInvitation_ShouldNotBeAcceptedUntilAdministratorReactivatesIt()
    {
        var service = new OrganizationManagementService();
        var owner = CreateIdentity(Guid.NewGuid().ToString(), "owner@example.invalid");
        var organization = service.CreateOrganization("Review team", "review-team", owner.TenantId, owner);
        var reviewer = CreateIdentity(owner.TenantId, "reviewer@example.invalid");
        var invitation = service.AddMember(organization.Id, owner, reviewer.Email, OrganizationMemberRole.SecurityAnalyst);
        service.SetMemberActive(organization.Id, owner, invitation.Id, false);
        Assert.Null(service.AcceptInvitation(organization.Id, reviewer));
        service.SetMemberActive(organization.Id, owner, invitation.Id, true);
        Assert.NotNull(service.AcceptInvitation(organization.Id, reviewer));
    }

    [Fact]
    public void RemediationPermission_ShouldPreserveExistingPersistedActionValues()
    {
        Assert.Equal(6, (int)OrganizationAction.ExportReports);
        Assert.Equal(7, (int)OrganizationAction.ManageRemediations);
    }

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
        Assert.True(service.Authorize(organization.Id, analyst, OrganizationAction.ManageRemediations).Allowed);
        Assert.False(service.Authorize(organization.Id, viewer, OrganizationAction.ManageRemediations).Allowed);
        var reviewManagers = service.GetMembersAuthorizedForAction(
            organization.Id, owner, OrganizationAction.ViewFindings, OrganizationAction.ManageRemediations);
        Assert.Contains(reviewManagers, member => member.Email == analyst.Email);
        Assert.DoesNotContain(reviewManagers, member => member.Email == viewer.Email);
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
            var role = service.CreateCustomRole(organization.Id, owner, "Read Only", new[] { OrganizationAction.ViewInventory });
            service.AddMember(organization.Id, owner, "reader@example.invalid", new OrganizationRoleOption(role.Name, null, role.Id));
            var binding = service.BindCloudAccount(organization.Id, owner, CloudProvider.Azure, Guid.NewGuid(), "tenant-1", "Production Azure");

            var restored = new OrganizationManagementService(new LocalOrganizationStore(filePath));

            Assert.Equal(organization, Assert.Single(restored.GetOrganizations()));
            Assert.Equal(member, Assert.Single(restored.GetMembers(organization.Id, owner), item => item.Id == member.Id));
            Assert.Equal("Read Only", Assert.Single(restored.GetCustomRoles(organization.Id, owner)).Name);
            Assert.Equal("Read Only", Assert.Single(restored.GetMembers(organization.Id, owner), item => item.CustomRoleId is not null).AssignedRoleName);
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
    public void AccountBoundToAnOrganization_ShouldNotBeAvailableToAnother()
    {
        var service = new OrganizationManagementService();
        var firstOwner = CreateIdentity(Guid.NewGuid().ToString(), "first@example.invalid");
        var secondOwner = CreateIdentity(Guid.NewGuid().ToString(), "second@example.invalid");
        var firstOrganization = service.CreateOrganization("First", "first", firstOwner.TenantId, firstOwner);
        var secondOrganization = service.CreateOrganization("Second", "second", secondOwner.TenantId, secondOwner);
        service.BindCloudAccount(firstOrganization.Id, firstOwner, CloudProvider.Aws, Guid.NewGuid(), "123456789012", "AWS Account");

        Assert.False(service.IsAccountAvailable(secondOrganization.Id, secondOwner, CloudProvider.Aws, "123456789012"));
        Assert.True(service.IsAccountAvailable(secondOrganization.Id, secondOwner, CloudProvider.Aws, "210987654321"));
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

    [Fact]
    public void CustomRole_ShouldGrantOnlyConfiguredActionsAndCannotBeDeletedWhileAssigned()
    {
        var service = new OrganizationManagementService();
        var tenantId = Guid.NewGuid().ToString();
        var owner = CreateIdentity(tenantId, "owner@example.invalid");
        var organization = service.CreateOrganization("Acme Security", "acme-tenant", tenantId, owner);
        var role = service.CreateCustomRole(organization.Id, owner, "Findings Reviewer", new[]
        {
            OrganizationAction.ViewInventory,
            OrganizationAction.ViewFindings,
            OrganizationAction.ExportReports,
            OrganizationAction.ManageRemediations
        });
        var member = service.AddMember(organization.Id, owner, "reviewer@example.invalid", new OrganizationRoleOption(role.Name, null, role.Id));
        var reviewer = CreateIdentity(tenantId, member.Email);
        Assert.NotNull(service.AcceptInvitation(organization.Id, reviewer));

        Assert.True(service.Authorize(organization.Id, reviewer, OrganizationAction.ViewInventory).Allowed);
        Assert.True(service.Authorize(organization.Id, reviewer, OrganizationAction.ViewFindings).Allowed);
        Assert.True(service.Authorize(organization.Id, reviewer, OrganizationAction.ExportReports).Allowed);
        Assert.True(service.Authorize(organization.Id, reviewer, OrganizationAction.ManageRemediations).Allowed);
        Assert.False(service.Authorize(organization.Id, reviewer, OrganizationAction.RunScan).Allowed);
        Assert.False(service.Authorize(organization.Id, reviewer, OrganizationAction.ManageMembers).Allowed);
        Assert.Equal("Findings Reviewer", Assert.Single(service.GetMembers(organization.Id, owner), item => item.Id == member.Id).AssignedRoleName);
        Assert.Throws<InvalidOperationException>(() => service.DeleteCustomRole(organization.Id, owner, role.Id));
    }

    [Fact]
    public void CustomRoleChanges_ShouldUpdateAssignedPermissionsAndMemberLabel()
    {
        var service = new OrganizationManagementService();
        var tenantId = Guid.NewGuid().ToString();
        var owner = CreateIdentity(tenantId, "owner@example.invalid");
        var organization = service.CreateOrganization("Acme Security", "acme-tenant", tenantId, owner);
        var role = service.CreateCustomRole(organization.Id, owner, "Read Only", new[] { OrganizationAction.ViewInventory });
        var member = service.AddMember(organization.Id, owner, "reader@example.invalid", new OrganizationRoleOption(role.Name, null, role.Id));
        var reader = CreateIdentity(tenantId, member.Email);
        Assert.NotNull(service.AcceptInvitation(organization.Id, reader));

        service.UpdateCustomRole(organization.Id, owner, role.Id, "Scan Operator", new[] { OrganizationAction.RunScan });

        Assert.Equal("Scan Operator", Assert.Single(service.GetMembers(organization.Id, owner), item => item.Id == member.Id).AssignedRoleName);
        Assert.False(service.Authorize(organization.Id, reader, OrganizationAction.ViewInventory).Allowed);
        Assert.True(service.Authorize(organization.Id, reader, OrganizationAction.RunScan).Allowed);
        Assert.Contains(service.GetAuditTrail(organization.Id, owner), entry => entry.Action == "CustomRoleUpdated");
    }

    private static OrganizationIdentity CreateIdentity(string tenantId, string email)
        => new(Guid.NewGuid(), Guid.NewGuid().ToString("N"), tenantId, email);
}
