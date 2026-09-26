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
}
