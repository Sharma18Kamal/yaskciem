using System.IO;
using System.Text.Json;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Infrastructure.Persistence;
using CloudEntitle.RemediationPlanning.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CloudEntitle.Desktop.Tests;

public sealed class RemediationPersistenceTests
{
    [Fact]
    public async Task DatabaseRoundTrip_PersistsChildrenAndDeduplicatesAudit()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new Factory(new DbContextOptionsBuilder<CloudEntitleDbContext>().UseSqlite(connection).Options);
        await using (var setup = factory.CreateDbContext()) await setup.Database.EnsureCreatedAsync();
        var plan = CreatePlan();
        var firstExecution = plan.LastExecution!;
        var secondExecution = firstExecution with
        {
            Id = Guid.NewGuid(),
            Status = RemediationExecutionStatus.Failed,
            StartedAt = firstExecution.CompletedAt.AddMinutes(1),
            CompletedAt = firstExecution.CompletedAt.AddMinutes(2)
        };
        plan = plan with { LastExecution = secondExecution, ExecutionHistory = [firstExecution, secondExecution] };
        var store = new LocalRemediationPlanStore(Path.Combine("artifacts", Guid.NewGuid() + ".json"), factory);
        await store.SaveAsync(plan);
        await store.SaveAsync(plan with { AuditTrail = [plan.AuditTrail[0], plan.AuditTrail[0]] });
        var loaded = Assert.Single(await new LocalRemediationPlanStore(Path.Combine("artifacts", Guid.NewGuid() + ".json"), factory).GetAsync());
        Assert.Equal(plan.Id, loaded.Id);
        Assert.Equal(plan.ApprovalComment, loaded.ApprovalComment);
        Assert.Equal(plan.Changes[0], Assert.Single(loaded.Changes));
        Assert.Equal(plan.RollbackChanges[0], Assert.Single(loaded.RollbackChanges));
        Assert.Equal(secondExecution.Id, loaded.LastExecution!.Id);
        Assert.Equal(new[] { firstExecution.Id, secondExecution.Id }, loaded.ExecutionHistory.Select(item => item.Id));
        Assert.Single(loaded.AuditTrail);
        await using var db = factory.CreateDbContext();
        Assert.Equal(1, await db.RemediationPlans.CountAsync());
        Assert.Equal(1, await db.RemediationActions.CountAsync());
        Assert.Equal(1, await db.RemediationApprovals.CountAsync());
        Assert.Equal(2, await db.RemediationExecutions.CountAsync());
        Assert.Equal(1, await db.RemediationRollbacks.CountAsync());
        Assert.Equal(1, await db.RemediationAuditRecords.CountAsync());
        Assert.Equal(1, await db.AuditEvents.CountAsync());
        var storedAction = await db.RemediationActions.SingleAsync();
        storedAction.Payload = JsonSerializer.Serialize(plan.Changes[0] with { Rationale = "stored child" });
        await db.SaveChangesAsync();
        Assert.Equal("stored child", Assert.Single(Assert.Single(await store.GetAsync()).Changes).Rationale);
    }

    [Fact]
    public async Task CorruptLegacyJson_IsReportedAndNotOverwritten()
    {
        var path = Path.Combine("artifacts", $"remediation-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory("artifacts");
        const string invalidJson = "{ invalid remediation data";
        await File.WriteAllTextAsync(path, invalidJson);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => new LocalRemediationPlanStore(path).GetAsync());
            Assert.Equal(invalidJson, await File.ReadAllTextAsync(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task LegacyJson_IsImportedAndDatabaseRemainsAuthoritative()
    {
        var path = Path.Combine("artifacts", $"remediation-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory("artifacts");
        try
        {
            var plan = CreatePlan();
            await new LocalRemediationPlanStore(path).SaveAsync(plan);
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var factory = new Factory(new DbContextOptionsBuilder<CloudEntitleDbContext>().UseSqlite(connection).Options);
            await using (var setup = factory.CreateDbContext()) await setup.Database.EnsureCreatedAsync();
            var store = new LocalRemediationPlanStore(path, factory);
            Assert.Equal(plan.Id, Assert.Single(await store.GetAsync()).Id);
            await store.SaveAsync(plan with { ApprovalComment = "Updated" });
            Assert.Equal("Updated", Assert.Single(await store.GetAsync()).ApprovalComment);
            await using var db = factory.CreateDbContext();
            Assert.Single(await db.RemediationPlans.ToListAsync());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData("client_secret=supersecret")]
    [InlineData("{\"access_token\":\"supersecret\"}")]
    [InlineData("Bearer abcdefghijkl")]
    [InlineData("AWS_SECRET_ACCESS_KEY=supersecret")]
    [InlineData("https://example.invalid/path?sig=supersecret")]
    public async Task SensitivePayload_IsRejectedBeforeWriting(string sensitive)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new Factory(new DbContextOptionsBuilder<CloudEntitleDbContext>().UseSqlite(connection).Options);
        await using (var setup = factory.CreateDbContext()) await setup.Database.EnsureCreatedAsync();
        var plan = CreatePlan() with { ApprovalComment = sensitive };
        var path = Path.Combine("artifacts", $"remediation-{Guid.NewGuid():N}.json");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new LocalRemediationPlanStore(path, factory).SaveAsync(plan));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new LocalRemediationPlanStore(path).SaveAsync(plan));
        Assert.False(File.Exists(path));
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        Assert.Empty(await db.RemediationPlans.ToListAsync());
    }

    private static RemediationPlan CreatePlan()
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var change = new RemediationChange(Guid.NewGuid(), "Reduce", "target", "write", "read", false, "review");
        return new RemediationPlan(id, new RecommendationModel { Id = Guid.NewGuid() }, CloudProvider.Azure,
            RemediationPlanStatus.Approved, [change], now, now, "review")
        {
            OrganizationId = Guid.NewGuid(), ApprovedBy = "reviewer", ApprovedAt = now, ApprovalComment = "approved",
            RollbackChanges = [change with { Id = Guid.NewGuid() }],
            LastExecution = new RemediationExecution(Guid.NewGuid(), id, CloudProvider.Azure, RemediationExecutionMode.DryRun,
                RemediationExecutionStatus.DryRunCompleted, false, ["ok"], now, now),
            AuditTrail = [new RemediationAuditEntry(now, "reviewer", "Approved", "approved")]
        };
    }

    private sealed class Factory(DbContextOptions<CloudEntitleDbContext> options) : IDbContextFactory<CloudEntitleDbContext>
    {
        public CloudEntitleDbContext CreateDbContext() => new(options);
    }
}
