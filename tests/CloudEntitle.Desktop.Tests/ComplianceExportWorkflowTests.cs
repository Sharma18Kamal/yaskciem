using System.IO;
using System.IO.Compression;
using System.Text.Json;
using CloudEntitle.Compliance.Models;
using CloudEntitle.Compliance.Services;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.IdentityGovernance.Services;
using CloudEntitle.Infrastructure.Persistence;
using CloudEntitle.OrganizationManagement.Models;
using CloudEntitle.OrganizationManagement.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CloudEntitle.Desktop.Tests;

public sealed class ComplianceExportWorkflowTests
{
    [Fact]
    public async Task CreateBundleAsync_ShouldIncludeOnlyTheSignedInOrganizationsBoundAccounts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "YaskCiemComplianceExportTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var options = new DbContextOptionsBuilder<CloudEntitleDbContext>()
            .UseSqlite($"Data Source={Path.Combine(directory, "compliance.db")};Pooling=False").Options;
        var factory = new TestDbContextFactory(options);
        var boundAccount = new CloudAccount
        {
            Id = Guid.NewGuid(), Name = "Bound Production", ExternalAccountId = "subscription-bound",
            CloudProvider = CloudProviderType.Azure, IsReadOnly = true
        };
        var unrelatedAccount = new CloudAccount
        {
            Id = Guid.NewGuid(), Name = "Unrelated Production", ExternalAccountId = "subscription-unrelated",
            CloudProvider = CloudProviderType.Azure, IsReadOnly = true
        };
        try
        {
            await using (var context = factory.CreateDbContext())
            {
                await context.Database.EnsureCreatedAsync();
                context.CloudAccounts.AddRange(boundAccount, unrelatedAccount);
                await context.SaveChangesAsync();
            }

            var identity = CreateIdentity("owner@example.invalid");
            var organizationService = new OrganizationManagementService();
            var organization = organizationService.CreateOrganization("Bound Org", "bound-org", identity.TenantId, identity);
            organizationService.BindCloudAccount(organization.Id, identity, CloudProvider.Azure, boundAccount.Id, "subscription-bound", boundAccount.Name);
            var session = new AuthenticatedOrganizationContext();
            session.Set(identity, organization.Id);
            var workflow = CreateWorkflow(factory, organizationService, session);

            Assert.True(workflow.CanExport);
            var bytes = await workflow.CreateBundleAsync(ComplianceFramework.CiemBaseline);
            using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            using var assessment = JsonDocument.Parse(await ReadEntryAsync(archive, "assessment.json"));
            var accountEvidence = assessment.RootElement.GetProperty("controls").EnumerateArray()
                .Single(control => control.GetProperty("controlId").GetString() == "CIEM-BASE-001")
                .GetProperty("evidence").EnumerateArray().Select(item => item.GetString()).ToArray();

            Assert.Contains(accountEvidence, item => item!.Contains("Bound Production", StringComparison.Ordinal));
            Assert.DoesNotContain(accountEvidence, item => item!.Contains("Unrelated Production", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CreateBundleAsync_ShouldDenyViewerWithoutReportExportPermission()
    {
        var identity = CreateIdentity("viewer@example.invalid");
        var organizationService = new OrganizationManagementService();
        var owner = CreateIdentity(identity.TenantId, "owner@example.invalid");
        var organization = organizationService.CreateOrganization("Read Only Org", "read-only-org", owner.TenantId, owner);
        var member = organizationService.AddMember(organization.Id, owner, identity.Email, OrganizationMemberRole.Viewer);
        Assert.NotNull(organizationService.AcceptInvitation(organization.Id, identity));
        var session = new AuthenticatedOrganizationContext();
        session.Set(identity, organization.Id);
        var factory = new TestDbContextFactory(new DbContextOptionsBuilder<CloudEntitleDbContext>()
            .UseSqlite("Data Source=:memory:").Options);
        var workflow = CreateWorkflow(factory, organizationService, session);

        Assert.False(workflow.CanExport);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => workflow.CreateBundleAsync(ComplianceFramework.CiemBaseline));
        Assert.Equal(Guid.Empty, member.CustomRoleId ?? Guid.Empty);
    }

    private static ComplianceExportWorkflow CreateWorkflow(TestDbContextFactory factory, OrganizationManagementService organizations, AuthenticatedOrganizationContext session)
        => new(factory, organizations, session, new ComplianceAssessmentService(), new IdentityGovernanceService(), new ComplianceExportService());

    private static OrganizationIdentity CreateIdentity(string email)
        => CreateIdentity(Guid.NewGuid().ToString("D"), email);

    private static OrganizationIdentity CreateIdentity(string tenantId, string email)
        => new(Guid.NewGuid(), Guid.NewGuid().ToString("N"), tenantId, email);

    private static async Task<string> ReadEntryAsync(ZipArchive archive, string name)
    {
        using var reader = new StreamReader(archive.GetEntry(name)!.Open());
        return await reader.ReadToEndAsync();
    }

    private sealed class TestDbContextFactory(DbContextOptions<CloudEntitleDbContext> options) : IDbContextFactory<CloudEntitleDbContext>
    {
        public CloudEntitleDbContext CreateDbContext() => new(options);
    }
}
