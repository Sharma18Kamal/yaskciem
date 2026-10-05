using System.IO;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Recommendations.Models;
using CloudEntitle.RemediationPlanning.Models;
using CloudEntitle.Desktop.Services;
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
}
