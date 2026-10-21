using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;

namespace CloudEntitle.IdentityGovernance.Models;

public enum GovernanceLifecycleState { Active, Inactive, Dormant, Disabled, External, ServiceIdentity, Unknown }
public enum CampaignStatus { Draft, Open, Escalated, Completed, Cancelled }
public enum ReviewDecision { Pending, Certified, NeedsInvestigation, Rejected, ExceptionGranted }

public sealed record GovernanceOptions(int DormancyDays = 90, bool IncludeExternalIdentities = true, bool IncludeServiceIdentities = true);

public sealed record AccessReviewTemplate(
    string Id,
    string Name,
    string Description,
    int DurationDays,
    bool PrivilegedOnly = false,
    bool WorkloadIdentitiesOnly = false,
    bool ExternalOnly = false);

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
    IReadOnlyList<AccessReviewItem> Items)
{
    public string? CreatedBy { get; init; }
    public string? TemplateId { get; init; }
    public int ReminderLeadDays { get; init; } = 3;
    public int ReminderIntervalDays { get; init; } = 1;
    public IReadOnlyList<AccessReviewAuditEntry> AuditTrail { get; init; } = Array.Empty<AccessReviewAuditEntry>();
}

public sealed record AccessReviewCandidate(
    Guid PrincipalId,
    Guid EffectiveAccessId,
    Guid? AccessPathId,
    CloudProvider Provider,
    string AccessSummary,
    string EvidenceSnapshot,
    string? SuggestedReviewer = null,
    IdentityType IdentityType = IdentityType.Unknown,
    bool IsPrivileged = false,
    bool IsExternal = false)
{
    public string? ExternalAccountId { get; init; }
    public string? Team { get; init; }
}

public sealed record AccessReviewAuditEntry(DateTime Timestamp, string Actor, string Action, string Details)
{
    public Guid EventId { get; init; }
    public Guid CorrelationId { get; init; }
    public Guid? ItemId { get; init; }
    public Guid ActorUserId { get; init; }
    public ReviewDecision? Decision { get; init; }
    public string? EvidenceSha256 { get; init; }
    public string CorrelationIdText => CorrelationId == Guid.Empty ? "local" : CorrelationId.ToString("N");
}

public sealed record AccessReviewAttestation(
    string Reviewer,
    string Actor,
    ReviewDecision Decision,
    string Comment,
    DateTime AttestedAtUtc,
    string EvidenceSha256,
    string EvidenceSnapshot)
{
    public Guid CorrelationId { get; init; }
    public Guid ActorUserId { get; init; }
}

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
    DateTime? DecidedAt)
{
    public string? AssignedReviewer { get; init; }
    public string? OwnerSnapshot { get; init; }
    public string? AssignedOwner { get; init; }
    public string? ExternalAccountId { get; init; }
    public string? Team { get; init; }
    public long AssignmentVersion { get; init; }
    public DateTime? DueAt { get; init; }
    public DateTime? WorkflowDeadline => Decision == ReviewDecision.ExceptionGranted && ExceptionExpiresAt.HasValue ? ExceptionExpiresAt : DueAt;
    public string EvidenceSnapshot { get; init; } = string.Empty;
    public int ReminderCount { get; init; }
    public DateTime? LastReminderAt { get; init; }
    public IReadOnlyList<AccessReviewAttestation> Attestations { get; init; } = Array.Empty<AccessReviewAttestation>();
    public DateTime? ExceptionExpiresAt { get; init; }
    public DateTime? ExceptionExpiredAt { get; init; }
    public int ExceptionReminderCount { get; init; }
    public DateTime? LastExceptionReminderAt { get; init; }
    public DateTime? EscalatedAt { get; init; }
    public string? EscalationReason { get; init; }
    public IReadOnlyList<AccessReviewAuditEntry> AuditTrail { get; init; } = Array.Empty<AccessReviewAuditEntry>();
}
