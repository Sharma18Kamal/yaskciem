using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CloudEntitle.Infrastructure.Tests;

public sealed class PersistenceIntegrationTests
{
    [Fact]
    public async Task DatabaseInitializer_ShouldApplyMigrationInIsolatedAppDataDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cloudentitle-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = new DatabaseConfiguration(directory);
            var initializer = new DbInitializer(configuration);

            await initializer.InitializeAsync();

            Assert.True(File.Exists(configuration.DatabasePath));
            await using (var context = new CloudEntitleDbContext(initializer.Options))
            {
                Assert.NotEmpty(await context.Database.GetAppliedMigrationsAsync());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public async Task NormalizedCiEMFlow_ShouldSurviveNewDbContextLifecycle()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CloudEntitleDbContext>().UseSqlite(connection).Options;
        var accountId = Guid.NewGuid();
        var principalId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var permissionId = Guid.NewGuid();

        await using (var context = new CloudEntitleDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();
            context.CloudAccounts.Add(new CloudAccount { Id = accountId, Name = "Production", CloudProvider = CloudProviderType.Azure, ExternalAccountId = "sub-prod" });
            context.CloudPrincipals.Add(new CloudPrincipal { Id = principalId, AccountId = accountId, Name = "analyst@example.com", PrincipalId = "principal-1", CloudProvider = CloudProviderType.Azure, IdentityType = IdentityType.User, IsActive = true });
            context.CloudResources.Add(new CloudResource { Id = resourceId, AccountId = accountId, Name = "Production Resource", ResourceId = "resource-1", Type = "Resource" });
            context.CloudRoles.Add(new CloudRole { Id = roleId, AccountId = accountId, Name = "Reader", RoleId = "role-reader" });
            context.CloudPermissions.Add(new CloudPermission { Id = permissionId, AccountId = accountId, Name = "Read", Action = "read", Resource = "*" });
            context.Entitlements.Add(new Entitlement { Id = Guid.NewGuid(), PrincipalId = principalId, RoleId = roleId, PermissionId = permissionId, CloudAccountId = accountId, ResourceId = resourceId, AssignmentType = AssignmentType.Direct });
            context.EffectiveAccesses.Add(new EffectiveAccess { Id = Guid.NewGuid(), PrincipalId = principalId, CloudAccountId = accountId, ResourceId = resourceId, PermissionId = permissionId, RoleId = roleId, AccessType = AccessType.Allow, AssignmentType = AssignmentType.Direct, IsDirect = true, CalculatedAt = DateTime.UtcNow });
            await context.SaveChangesAsync();
        }

        await using (var context = new CloudEntitleDbContext(options))
        {
            var account = await context.CloudAccounts.SingleAsync(entity => entity.Id == accountId);
            var entitlement = await context.Entitlements.SingleAsync(entity => entity.PrincipalId == principalId);
            var access = await context.EffectiveAccesses.SingleAsync(entity => entity.ResourceId == resourceId);

            Assert.Equal("Production", account.Name);
            Assert.Equal(roleId, entitlement.RoleId);
            Assert.Equal(permissionId, access.PermissionId);
            Assert.True(access.IsDirect);
        }
    }
}
