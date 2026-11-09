using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CloudEntitle.Infrastructure.Tests;

public sealed class RemediationMigrationTests
{
    [Fact]
    public async Task Migration_IsAdditiveRepeatableAndCreatesRelationships()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CloudEntitleDbContext>().UseSqlite(connection).Options;
        await using var db = new CloudEntitleDbContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261109041336_AddTrustSourcePermissionEvidence");
        var account = new CloudAccount { Id = Guid.NewGuid(), Name = "Existing", CloudProvider = CloudProviderType.Azure };
        db.CloudAccounts.Add(account);
        await db.SaveChangesAsync();
        await migrator.MigrateAsync();
        await migrator.MigrateAsync();
        Assert.Equal("Existing", (await db.CloudAccounts.SingleAsync()).Name);
        var id = Guid.NewGuid();
        db.RemediationPlans.Add(new RemediationPlanRecord { Id = id, Payload = "{}", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        db.RemediationActions.Add(new RemediationActionRecord { PlanId = id, Id = Guid.NewGuid(), Payload = "{}" });
        db.RemediationApprovals.Add(new RemediationApprovalRecord { PlanId = id, Payload = "{}" });
        db.RemediationExecutions.Add(new RemediationExecutionRecord { PlanId = id, Id = Guid.NewGuid(), Payload = "{}" });
        db.RemediationRollbacks.Add(new RemediationRollbackRecord { PlanId = id, Id = Guid.NewGuid(), Payload = "{}" });
        db.RemediationAuditRecords.Add(new RemediationAuditRecord { PlanId = id, Id = Guid.NewGuid(), Payload = "{}" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await db.RemediationPlans.Where(item => item.Id == id).ExecuteDeleteAsync();
        Assert.Empty(await db.RemediationActions.ToListAsync());
        Assert.Empty(await db.RemediationApprovals.ToListAsync());
        Assert.Empty(await db.RemediationExecutions.ToListAsync());
        Assert.Empty(await db.RemediationRollbacks.ToListAsync());
        Assert.Empty(await db.RemediationAuditRecords.ToListAsync());
        Assert.Single(await db.CloudAccounts.ToListAsync());
    }
}
