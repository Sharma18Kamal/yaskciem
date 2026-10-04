using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;

namespace CloudEntitle.RemediationPlanning.Models;

public enum RemediationPlanStatus { Draft, RequiresReview, Approved, Rejected, Simulated, DryRunCompleted, Implemented, VerificationPending, VerificationFailed, RollbackPrepared }
public enum SimulationStatus { NotRun, Simulated, RequiresReview, Unsupported, Invalid }
public enum AccessImpactStatus { Unknown, Preserved, PotentialAccessLoss, NoKnownImpact }
public enum RemediationVerificationStatus { NotRun, Verified, Failed, Unavailable }

public sealed record RemediationAuditEntry(DateTime Timestamp, string Actor, string Action, string Details);
public sealed record RemediationVerification(RemediationVerificationStatus Status, string Details, DateTime CheckedAt);

public sealed record RemediationPlan(
    Guid Id,
    RecommendationModel Recommendation,
    CloudProvider Provider,
    RemediationPlanStatus Status,
    IReadOnlyList<RemediationChange> Changes,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string SafetyNotice)
{
    public string? ApprovedBy { get; init; }
    public DateTime? ApprovedAt { get; init; }
    public string? ApprovalComment { get; init; }
    public RemediationSimulation? Simulation { get; init; }
    public IReadOnlyList<RemediationChange> RollbackChanges { get; init; } = Array.Empty<RemediationChange>();
    public bool IsRollbackDraft { get; init; }
    public RemediationVerification? Verification { get; init; }
    public RemediationExecution? LastExecution { get; init; }
    public IReadOnlyList<RemediationAuditEntry> AuditTrail { get; init; } = Array.Empty<RemediationAuditEntry>();
}

public sealed record RemediationChange(
    Guid Id,
    string ChangeType,
    string Target,
    string BeforeState,
    string ProposedState,
    bool Executable,
    string Rationale)
{
    public string? RollbackState { get; init; }
}

public sealed record RemediationSimulation(
    Guid PlanId,
    SimulationStatus Status,
    AccessImpactStatus AccessImpact,
    IReadOnlyList<Guid> AffectedEffectiveAccess,
    IReadOnlyList<Guid> RetainedEffectiveAccess,
    IReadOnlyList<Guid> PotentiallyLostEffectiveAccess,
    IReadOnlyList<string> Diagnostics,
    DateTime SimulatedAt);
