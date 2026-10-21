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
    public void InvestigationAndRenewedExceptions_ShouldUseCurrentDeadlineAndPreserveHistory()
    {
        var now = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var service = new IdentityGovernanceService();
        var candidate = new AccessReviewCandidate(Guid.NewGuid(), Guid.NewGuid(), null, CloudProvider.Aws,
            "Admin access", "Immutable evidence", "owner") { ExternalAccountId = "123", Team = "Platform" };
        var campaign = service.CreateCampaign("Review", new[] { candidate }, TimeSpan.FromDays(2), asOf: now);
        var id = campaign.Items[0].Id;
        campaign = service.RecordDecision(campaign, id, ReviewDecision.NeedsInvestigation, "reviewer", "Investigate", asOf: now);
        Assert.Equal(CampaignStatus.Open, campaign.Status);
        campaign = service.ReevaluateCampaign(campaign, now.AddDays(2));
        Assert.Equal(CampaignStatus.Escalated, campaign.Status);
        campaign = service.RecordDecision(campaign, id, ReviewDecision.ExceptionGranted, "reviewer", "Approved",
            now.AddDays(10), now.AddDays(3));
        var active = Assert.Single(campaign.Items);
        Assert.Equal(now.AddDays(2), active.DueAt);
        Assert.Null(active.EscalatedAt);
        Assert.Equal("123", active.ExternalAccountId);
        Assert.Equal("Platform", active.Team);
        campaign = service.ReevaluateCampaign(campaign, now.AddDays(7));
        Assert.Equal(1, campaign.Items[0].ExceptionReminderCount);
        Assert.Null(campaign.Items[0].EscalatedAt);
        Assert.Equal(1, service.ReevaluateCampaign(campaign, now.AddDays(7).AddHours(1)).Items[0].ExceptionReminderCount);
        campaign = service.ReevaluateCampaign(campaign, now.AddDays(10));
        Assert.Equal(CampaignStatus.Escalated, campaign.Status);
        Assert.NotNull(campaign.Items[0].ExceptionExpiredAt);
        campaign = service.RecordDecision(campaign, id, ReviewDecision.ExceptionGranted, "reviewer", "Renewed",
            now.AddDays(30), now.AddDays(11));
        var renewed = Assert.Single(campaign.Items);
        Assert.Equal(CampaignStatus.Completed, campaign.Status);
        Assert.Null(renewed.ExceptionExpiredAt);
        Assert.Null(renewed.EscalatedAt);
        Assert.Equal(0, renewed.ExceptionReminderCount);
        Assert.Contains(renewed.AuditTrail, entry => entry.Action == "ExceptionExpired");
        Assert.Equal(3, renewed.Attestations.Count);
        var cancelled = campaign with { Status = CampaignStatus.Cancelled };
        Assert.Same(cancelled, service.ReevaluateCampaign(cancelled, now.AddDays(40)));
    }

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
    public void AccessReview_ShouldIssueRecurringRemindersThenEscalate()
    {
        var now = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var candidate = new AccessReviewCandidate(Guid.NewGuid(), Guid.NewGuid(), null, CloudProvider.Azure,
            "Privileged workload access", "Evidence snapshot", SuggestedReviewer: "owner@example.invalid");
        var service = new IdentityGovernanceService();
        var campaign = service.CreateCampaign("Workload review", new[] { candidate }, TimeSpan.FromDays(10),
            asOf: now, templateId: "privileged-workloads", reminderLeadDays: 3, reminderIntervalDays: 1);
        var item = Assert.Single(campaign.Items);

        var beforeWindow = service.ReevaluateCampaign(campaign, now.AddDays(6));
        Assert.Equal(0, Assert.Single(beforeWindow.Items).ReminderCount);
        var firstReminder = service.ReevaluateCampaign(campaign, now.AddDays(7));
        var firstReminderItem = Assert.Single(firstReminder.Items);
        Assert.Equal("privileged-workloads", firstReminder.TemplateId);
        Assert.Equal("owner@example.invalid", firstReminderItem.OwnerSnapshot);
        Assert.Equal(1, firstReminderItem.ReminderCount);

        var sameInterval = service.ReevaluateCampaign(firstReminder, now.AddDays(7).AddHours(12));
        Assert.Equal(1, Assert.Single(sameInterval.Items).ReminderCount);
        var nextReminder = service.ReevaluateCampaign(sameInterval, now.AddDays(8));
        Assert.Equal(2, Assert.Single(nextReminder.Items).ReminderCount);

        var overdue = service.ReevaluateCampaign(nextReminder, item.DueAt!.Value.AddMinutes(1));
        var overdueItem = Assert.Single(overdue.Items);
        Assert.NotNull(overdueItem.EscalatedAt);
        Assert.Equal(2, overdueItem.ReminderCount);
        Assert.Contains(overdueItem.AuditTrail, entry => entry.Action == "ReviewerReminderDue");
        Assert.Contains(overdueItem.AuditTrail, entry => entry.Action == "Escalated");
    }

    [Fact]
    public void AccessReview_ShouldBindAttestationToEvidenceAndRecordReviewerAndActor()
    {
        var now = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var evidence = "Provider=AWS; Grant=ReadOnly; Owner=platform@example.invalid";
        var candidate = new AccessReviewCandidate(Guid.NewGuid(), Guid.NewGuid(), null, CloudProvider.Aws,
            "Read-only account access", evidence, SuggestedReviewer: "platform@example.invalid");
        var service = new IdentityGovernanceService();
        var campaign = service.CreateCampaign("Owner attestation", new[] { candidate }, TimeSpan.FromDays(14),
            asOf: now, templateId: "all-access");
        var item = Assert.Single(campaign.Items);

        var attested = service.RecordDecision(campaign, item.Id, ReviewDecision.Certified,
            "platform@example.invalid", "Access confirmed", asOf: now.AddHours(1), actor: "security-admin@example.invalid");
        var updatedItem = Assert.Single(attested.Items);
        var attestation = Assert.Single(updatedItem.Attestations);

        Assert.Equal("platform@example.invalid", attestation.Reviewer);
        Assert.Equal("security-admin@example.invalid", attestation.Actor);
        Assert.Equal(evidence, attestation.EvidenceSnapshot);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(evidence))).ToLowerInvariant(), attestation.EvidenceSha256);
        Assert.Contains(updatedItem.AuditTrail, entry => entry.Action == "AttestationRecorded");
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
        Assert.Equal(item.DueAt, Assert.Single(approved.Items).DueAt);
        Assert.Equal(expiry, IdentityGovernanceService.EffectiveDeadline(approved.Items[0], approved));

        var expired = service.ReevaluateCampaign(approved, expiry.AddMinutes(1));
        var expiredItem = Assert.Single(expired.Items);
        Assert.Equal(CampaignStatus.Escalated, expired.Status);
        Assert.NotNull(expiredItem.ExceptionExpiredAt);
        Assert.NotNull(expiredItem.EscalatedAt);
        Assert.Contains(expiredItem.AuditTrail, entry => entry.Action == "ExceptionExpired");
        Assert.Contains(expiredItem.AuditTrail, entry => entry.Action == "Escalated");
    }
}
