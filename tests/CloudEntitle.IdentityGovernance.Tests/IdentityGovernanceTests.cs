using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.IdentityGovernance.Models;
using CloudEntitle.IdentityGovernance.Services;
using Xunit;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;
using DomainAccessPath = CloudEntitle.Domain.Entities.AccessPath;

namespace CloudEntitle.IdentityGovernance.Tests;

public sealed class IdentityGovernanceTests
{
    [Fact]
    public void PrivilegedInactiveIdentity_ShouldBeClassifiedDormantWithEvidence()
    {
        var principal = new CloudPrincipal { Id = Guid.NewGuid(), AccountId = Guid.NewGuid(), Name = "Dormant Admin", IdentityType = IdentityType.User, IsActive = true, IsPrivileged = true, LastActivityAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        var records = new[] { new ActivityRecord { Id = Guid.NewGuid(), PrincipalId = principal.Id, Timestamp = principal.LastActivityAt.Value, Action = "Login" } };

        var result = new IdentityGovernanceService().Assess(new[] { principal }, Array.Empty<DomainEffectiveAccess>(), records, new GovernanceOptions(90), new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc));

        var identity = Assert.Single(result);
        Assert.Equal(GovernanceLifecycleState.Dormant, identity.LifecycleState);
        Assert.Contains(identity.Evidence, item => item.Contains("LastActivity", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingActivity_ShouldBeUnknownNotDormant()
    {
        var principal = new CloudPrincipal { Id = Guid.NewGuid(), AccountId = Guid.NewGuid(), Name = "Unknown Activity", IdentityType = IdentityType.User, IsActive = true };

        var identity = Assert.Single(new IdentityGovernanceService().Assess(new[] { principal }, Array.Empty<DomainEffectiveAccess>(), Array.Empty<ActivityRecord>()));

        Assert.Equal(GovernanceLifecycleState.Unknown, identity.LifecycleState);
        Assert.Contains(identity.Diagnostics, item => item.Contains("GOV-001", StringComparison.Ordinal));
    }

    [Fact]
    public void AccessReview_ShouldTrackDecisionsWithoutMutatingAccess()
    {
        var principalId = Guid.NewGuid();
        var access = new DomainEffectiveAccess { Id = Guid.NewGuid(), PrincipalId = principalId, CloudAccountId = Guid.NewGuid(), Provider = CloudProvider.Azure, Decision = AccessDecision.Allow, PermissionId = Guid.NewGuid() };
        var identity = new IdentityGovernanceRecord(principalId, access.CloudAccountId, "Review User", IdentityType.User, GovernanceLifecycleState.Active, false, false, DateTime.UtcNow, 0, 1, 0, Array.Empty<string>(), Array.Empty<string>());
        var service = new IdentityGovernanceService();
        var campaign = service.CreateCampaign("Quarterly review", new[] { identity }, new[] { access }, Array.Empty<DomainAccessPath>(), TimeSpan.FromDays(30));

        var updated = service.RecordDecision(campaign, campaign.Items[0].Id, ReviewDecision.Certified, "reviewer", "Verified with owner.");

        Assert.Equal(CampaignStatus.Completed, updated.Status);
        Assert.Equal(ReviewDecision.Certified, updated.Items[0].Decision);
        Assert.Equal(access.Id, updated.Items[0].EffectiveAccessId);
    }

    [Fact]
    public void AccessReview_ShouldAssignReviewerCaptureEvidenceAndEscalateOverdueItem()
    {
        var now = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var service = new IdentityGovernanceService();
        var candidate = new AccessReviewCandidate(Guid.NewGuid(), Guid.NewGuid(), null, CloudProvider.Azure,
            "Owner at subscription scope", "Decision=Allow; Source=Azure RBAC; LastChanged=2026-09-01T00:00:00Z");
        var campaign = service.CreateCampaign("Quarterly review", new[] { candidate }, TimeSpan.FromDays(30), assignedReviewer: "initial-reviewer", asOf: now);
        var item = Assert.Single(campaign.Items);
        Assert.Equal("initial-reviewer", item.AssignedReviewer);
        Assert.Contains("Azure RBAC", item.EvidenceSnapshot);

        var assigned = service.AssignReviewer(campaign, item.Id, "security-reviewer", now.AddDays(2), "campaign-admin", now);
        var escalated = service.ReevaluateCampaign(assigned, now.AddDays(3));
        var escalatedItem = Assert.Single(escalated.Items);

        Assert.Equal(CampaignStatus.Escalated, escalated.Status);
        Assert.Equal("security-reviewer", escalatedItem.AssignedReviewer);
        Assert.NotNull(escalatedItem.EscalatedAt);
        Assert.Contains(escalatedItem.AuditTrail, entry => entry.Action == "ReviewerAssigned");
        Assert.Contains(escalatedItem.AuditTrail, entry => entry.Action == "Escalated");
    }

    [Fact]
    public void AccessReview_ExceptionMustExpireAndEscalatesAfterExpiry()
    {
        var now = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var service = new IdentityGovernanceService();
        var candidate = new AccessReviewCandidate(Guid.NewGuid(), Guid.NewGuid(), null, CloudProvider.Aws,
            "ReadOnly at account scope", "Provider=AWS; Source=IAM role assignment");
        var campaign = service.CreateCampaign("Temporary exception review", new[] { candidate }, TimeSpan.FromDays(30), asOf: now);
        var item = Assert.Single(campaign.Items);

        Assert.Throws<ArgumentOutOfRangeException>(() => service.RecordDecision(campaign, item.Id, ReviewDecision.ExceptionGranted, "reviewer", "Temporary exception" , asOf: now));

        var expiry = now.AddDays(2);
        var approved = service.RecordDecision(campaign, item.Id, ReviewDecision.ExceptionGranted, "reviewer", "Approved temporary exception", expiry, now);
        Assert.Equal(CampaignStatus.Completed, approved.Status);
        Assert.Equal(expiry, Assert.Single(approved.Items).DueAt);

        var expired = service.ReevaluateCampaign(approved, expiry.AddMinutes(1));
        var expiredItem = Assert.Single(expired.Items);
        Assert.Equal(CampaignStatus.Escalated, expired.Status);
        Assert.NotNull(expiredItem.ExceptionExpiredAt);
        Assert.NotNull(expiredItem.EscalatedAt);
        Assert.Contains(expiredItem.AuditTrail, entry => entry.Action == "ExceptionExpired");
        Assert.Contains(expiredItem.AuditTrail, entry => entry.Action == "Escalated");
    }
}
