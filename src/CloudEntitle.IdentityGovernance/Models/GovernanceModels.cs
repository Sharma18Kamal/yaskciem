using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;

namespace CloudEntitle.IdentityGovernance.Models;

public enum GovernanceLifecycleState { Active, Inactive, Dormant, Disabled, External, ServiceIdentity, Unknown }
public enum CampaignStatus { Draft, Open, Completed, Cancelled }
public enum ReviewDecision { Pending, Certified, NeedsInvestigation, Rejected }

public sealed record GovernanceOptions(int DormancyDays = 90, bool IncludeExternalIdentities = true, bool IncludeServiceIdentities = true);

public sealed record IdentityGovernanceRecord(
    Guid PrincipalId,
    Guid CloudAccountId,
    string DisplayName,
    IdentityType IdentityType,
    GovernanceLifecycleState LifecycleState,
    bool IsPrivileged,
    bool IsExternal,
    DateTime? LastActivityAt,
    int? DaysSinceActivity,
    int EffectiveAccessCount,
    int PrivilegedAccessCount,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Diagnostics);

public sealed record AccessReviewCampaign(
    Guid Id,
    string Name,
    CampaignStatus Status,
    DateTime CreatedAt,
    DateTime DueAt,
    IReadOnlyList<Guid> PrincipalIds,
    IReadOnlyList<AccessReviewItem> Items);

public sealed record AccessReviewItem(
    Guid Id,
    Guid CampaignId,
    Guid PrincipalId,
    Guid EffectiveAccessId,
    Guid? AccessPathId,
    CloudProvider Provider,
    string AccessSummary,
    ReviewDecision Decision,
    string? Reviewer,
    string? ReviewComment,
    DateTime? DecidedAt);
