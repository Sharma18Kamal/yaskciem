using System.IO;
using CloudEntitle.Domain;
using CloudEntitle.IdentityGovernance.Models;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.LicenseServer.Services;
using CloudEntitle.OrganizationManagement.Models;
using CloudEntitle.OrganizationManagement.Services;
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed class SharedWorkspaceStoreTests
{
    [PostgreSqlFact]
    public async Task OrganizationDirectories_ShouldPersistPerTenantAndEnforceGlobalAccountBindingUniqueness()
    {
        var directory = Path.Combine(Path.GetTempPath(), "YaskCiemSharedWorkspaceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var owner = Identity(tenantId, "owner@example.invalid");
        var otherOwner = Identity(otherTenantId, "owner@other.invalid");
        try
        {
            var organization = new OrganizationManagementService(store.CreateOrganizationStore(tenantId))
                .CreateOrganization("Shared Org", "shared-org", tenantId.ToString("D"), owner);
            var sameTenantService = new OrganizationManagementService(store.CreateOrganizationStore(tenantId));
            sameTenantService.BindCloudAccount(organization.Id, owner, CloudProvider.Aws, Guid.NewGuid(), "123456789012", "Production AWS");

            var reloaded = new OrganizationManagementService(store.CreateOrganizationStore(tenantId));
            var persisted = Assert.Single(reloaded.GetOrganizationsForIdentity(owner));
            Assert.Equal(organization.Id, persisted.Id);
            Assert.Single(reloaded.GetTenantBindings(organization.Id, owner));

            var otherTenantService = new OrganizationManagementService(store.CreateOrganizationStore(otherTenantId));
            var otherOrganization = otherTenantService.CreateOrganization("Other Org", "other-org", otherTenantId.ToString("D"), otherOwner);
            Assert.Equal(otherOrganization.Id, Assert.Single(otherTenantService.GetOrganizationsForIdentity(otherOwner)).Id);
            Assert.Empty(otherTenantService.GetOrganizationsForIdentity(owner));
            Assert.Throws<InvalidOperationException>(() => otherTenantService.BindCloudAccount(otherOrganization.Id, otherOwner,
                CloudProvider.Aws, Guid.NewGuid(), "123456789012", "Duplicate AWS account"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [PostgreSqlFact]
    public async Task ReviewsAndScanSummaries_ShouldBeSharedButIsolatedByTenantAndOrganization()
    {
        var directory = Path.Combine(Path.GetTempPath(), "YaskCiemSharedWorkspaceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        var tenantId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var campaign = new AccessReviewCampaign(campaignId, "Q4 review", CampaignStatus.Open, now, now.AddDays(14), Array.Empty<Guid>(), Array.Empty<AccessReviewItem>());
        var scan = new SharedScanSummaryDto(Guid.NewGuid(), organizationId, tenantId, Guid.NewGuid(), now.AddMinutes(-1), now, "AWS",
            new[] { new SharedScanAccountDto(CloudProvider.Aws, "123456789012", "Production") }, 10, 20, 4, 30, 8, 2, 1, 1,
            new[] { new SharedScanCoverageDto("AWS", "Resources", "Partial", 20, "Tagged assets only") });

        try
        {
            await store.SaveAccessReviewAsync(tenantId, organizationId, campaign);
            await store.SaveScanSummaryAsync(scan);
            await store.SaveScanSummaryAsync(scan with { Findings = 99 });

            Assert.Equal(campaignId, Assert.Single(await store.GetAccessReviewsAsync(tenantId, organizationId)).Id);
            Assert.Empty(await store.GetAccessReviewsAsync(Guid.NewGuid(), organizationId));
            var loaded = Assert.Single(await store.GetScanSummariesAsync(tenantId, organizationId));
            Assert.Equal(2, loaded.Findings);
            Assert.Empty(await store.GetScanSummariesAsync(tenantId, Guid.NewGuid()));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [PostgreSqlFact]
    public async Task OrganizationStore_ShouldRejectStaleConcurrentWrites()
    {
        var directory = Path.Combine(Path.GetTempPath(), "YaskCiemSharedWorkspaceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        var tenantId = Guid.NewGuid();
        try
        {
            var first = store.CreateOrganizationStore(tenantId);
            var second = store.CreateOrganizationStore(tenantId);
            var firstState = first.Load();
            var secondState = second.Load();
            first.Save(firstState);
            Assert.Throws<InvalidOperationException>(() => second.Save(secondState));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static OrganizationIdentity Identity(Guid tenantId, string email)
        => new(Guid.NewGuid(), Guid.NewGuid().ToString("N"), tenantId.ToString("D"), email);
}
