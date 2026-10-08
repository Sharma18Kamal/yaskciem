using System.IO;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Recommendations.Models;
using CloudEntitle.RemediationPlanning.Models;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CloudEntitle.Desktop.Tests;

public sealed class RemediationPlanStoreTests
{
    [Fact]
    public async Task SaveAsync_ShouldRoundTripApprovalAndAuditMetadata()
    {
        var directory = Path.Combine(Path.GetTempPath(), "YaskCiemTests", Guid.NewGuid().ToString("N"));
        var filePath = Path.Combine(directory, "plans.json");
        var planId = Guid.NewGuid();
        var recommendation = new RecommendationModel
        {
            Id = Guid.NewGuid(),
            Provider = CloudProvider.Azure,
            CloudAccountId = Guid.NewGuid(),
            RecommendationType = RecommendationType.NarrowWildcardPermission,
            CurrentState = "*",
            SuggestedState = "read",
            Status = RecommendationStatus.New,
            EvaluationState = RiskEvaluationState.Confirmed
        };
        var plan = new RemediationPlan(planId, recommendation, CloudProvider.Azure, RemediationPlanStatus.Approved,
            new[] { new RemediationChange(Guid.NewGuid(), "NarrowWildcardPermission", "test-target", "*", "read", false, "Review only.") },
            DateTime.UtcNow, DateTime.UtcNow, "Non-executable.")
        {
            CreatedBy = "requester",
            ApprovedBy = "reviewer",
            ApprovedAt = DateTime.UtcNow,
            ApprovalComment = "Approved after review.",
            AuditTrail = new[] { new RemediationAuditEntry(DateTime.UtcNow, "reviewer", "Approved", "Approved after review.") }
        };
        var approvedPlan = plan with { ApprovedChangeSetFingerprint = RemediationPlanIntegrity.ComputeChangeSetFingerprint(plan) };

        try
        {
            var store = new LocalRemediationPlanStore(filePath);
            await store.SaveAsync(approvedPlan);

            var loaded = Assert.Single(await store.GetAsync());
            Assert.Equal(planId, loaded.Id);
            Assert.Equal("reviewer", loaded.ApprovedBy);
            Assert.Equal("requester", loaded.CreatedBy);
            Assert.Equal("Approved after review.", loaded.ApprovalComment);
            Assert.Equal(approvedPlan.ApprovedChangeSetFingerprint, loaded.ApprovedChangeSetFingerprint);
            Assert.Equal("Approved", Assert.Single(loaded.AuditTrail).Action);
            Assert.False(Assert.Single(loaded.Changes).Executable);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_ShouldAppendEachRemediationAuditEntryOnceToSqlite()
    {
        var directory = Path.Combine(Path.GetTempPath(), "YaskCiemAuditTests", Guid.NewGuid().ToString("N"));
        var planPath = Path.Combine(directory, "plans.json");
        var databasePath = Path.Combine(directory, "audit.db");
        var options = new DbContextOptionsBuilder<CloudEntitleDbContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False").Options;
        var contextFactory = new TestDbContextFactory(options);
        var organizationId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var plan = CreatePlan(organizationId, actorId);

        try
        {
            Directory.CreateDirectory(directory);
            var store = new LocalRemediationPlanStore(planPath, contextFactory);
            await store.SaveAsync(plan);
            var updated = plan with
            {
                AuditTrail = plan.AuditTrail.Append(new RemediationAuditEntry(DateTime.UtcNow, "reviewer@example.invalid", "Approved", "Approved preview.")
                {
                    ActorUserId = Guid.NewGuid()
                }).ToArray()
            };
            await store.SaveAsync(updated);
            await store.SaveAsync(updated);

            await using (var context = contextFactory.CreateDbContext())
            {
                var events = await context.AuditEvents.OrderBy(entry => entry.Timestamp).ToArrayAsync();
                Assert.Equal(2, events.Length);
                Assert.All(events, entry => Assert.Equal(organizationId, entry.OrganizationId));
                Assert.Contains(events, entry => entry.UserId == actorId && entry.EventType == "Remediation.PlanCreated");
                Assert.Contains(events, entry => entry.Metadata["planId"] == plan.Id.ToString("D"));
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static RemediationPlan CreatePlan(Guid organizationId, Guid actorId)
    {
        var recommendation = new RecommendationModel
        {
            Id = Guid.NewGuid(),
            Provider = CloudProvider.Azure,
            CloudAccountId = Guid.NewGuid(),
            RecommendationType = RecommendationType.NarrowWildcardPermission,
            CurrentState = "*",
            SuggestedState = "read",
            Status = RecommendationStatus.New,
            EvaluationState = RiskEvaluationState.Confirmed
        };
        return new RemediationPlan(Guid.NewGuid(), recommendation, CloudProvider.Azure, RemediationPlanStatus.RequiresReview,
            new[] { new RemediationChange(Guid.NewGuid(), "NarrowWildcardPermission", "test-target", "*", "read", false, "Review only.") },
            DateTime.UtcNow, DateTime.UtcNow, "Non-executable.")
        {
            OrganizationId = organizationId,
            CreatedBy = "requester@example.invalid",
            CreatedByUserId = actorId,
            AuditTrail = new[]
            {
                new RemediationAuditEntry(DateTime.UtcNow, "requester@example.invalid", "PlanCreated", "Created preview.") { ActorUserId = actorId }
            }
        };
    }

    private sealed class TestDbContextFactory(DbContextOptions<CloudEntitleDbContext> options) : IDbContextFactory<CloudEntitleDbContext>
    {
        public CloudEntitleDbContext CreateDbContext() => new(options);
    }
}
