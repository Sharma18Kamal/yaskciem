using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CloudEntitle.RemediationPlanning.Models;

public enum RemediationPlanStatus { Draft, RequiresReview, Approved, Rejected, Simulated, DryRunCompleted, Implemented, VerificationPending, VerificationFailed, RollbackPrepared }
public enum SimulationStatus { NotRun, Simulated, RequiresReview, Unsupported, Invalid }
public enum AccessImpactStatus { Unknown, Preserved, PotentialAccessLoss, NoKnownImpact }
public enum RemediationVerificationStatus { NotRun, Verified, Failed, Unavailable }

public sealed record RemediationAuditEntry(DateTime Timestamp, string Actor, string Action, string Details)
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid? ActorUserId { get; init; }
}
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
    public string? CreatedBy { get; init; }
    public Guid? CreatedByUserId { get; init; }
    public Guid? OrganizationId { get; init; }
    public string? ApprovedBy { get; init; }
    public Guid? ApprovedByUserId { get; init; }
    public Guid? DryRunApprovedByUserId { get; init; }
    public DateTime? ApprovedAt { get; init; }
    public string? ApprovalComment { get; init; }
    public string? ApprovedChangeSetFingerprint { get; init; }
    public string? DryRunResultFingerprint { get; init; }
    public string? ApprovedDryRunFingerprint { get; init; }
    public RemediationSimulation? Simulation { get; init; }
    public IReadOnlyList<RemediationChange> RollbackChanges { get; init; } = Array.Empty<RemediationChange>();
    public bool IsRollbackDraft { get; init; }
    public RemediationVerification? Verification { get; init; }
    public RemediationExecution? LastExecution { get; init; }
    public IReadOnlyList<RemediationAuditEntry> AuditTrail { get; init; } = Array.Empty<RemediationAuditEntry>();
}

public static class RemediationPlanIntegrity
{
    public static string ComputeChangeSetFingerprint(RemediationPlan plan)
    {
        var content = JsonSerializer.Serialize(new
        {
            plan.Id,
            plan.Provider,
            plan.IsRollbackDraft,
            plan.CreatedByUserId,
            plan.OrganizationId,
            Recommendation = new
            {
                plan.Recommendation.Id,
                plan.Recommendation.RiskFindingId,
                plan.Recommendation.Fingerprint,
                plan.Recommendation.RecommendationType,
                plan.Recommendation.CurrentState,
                plan.Recommendation.SuggestedState
            },
            Changes = plan.Changes.OrderBy(change => change.Id).Select(change => new
            {
                change.Id,
                change.ChangeType,
                change.Target,
                change.BeforeState,
                change.ProposedState,
                change.Executable,
                change.Rationale,
                change.RollbackState
            }),
            RollbackChanges = plan.RollbackChanges.OrderBy(change => change.Id).Select(change => new
            {
                change.Id,
                change.ChangeType,
                change.Target,
                change.BeforeState,
                change.ProposedState,
                change.Executable,
                change.Rationale,
                change.RollbackState
            })
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    public static bool HasAuditedApproval(RemediationPlan plan)
        => plan.Status is RemediationPlanStatus.Approved or RemediationPlanStatus.DryRunCompleted
            && !string.IsNullOrWhiteSpace(plan.CreatedBy)
            && !string.IsNullOrWhiteSpace(plan.ApprovedBy)
            && !string.Equals(plan.CreatedBy.Trim(), plan.ApprovedBy.Trim(), StringComparison.OrdinalIgnoreCase)
            && (!plan.CreatedByUserId.HasValue || plan.ApprovedByUserId.HasValue && plan.CreatedByUserId != plan.ApprovedByUserId)
            && (!plan.OrganizationId.HasValue || plan.CreatedByUserId.HasValue && plan.ApprovedByUserId.HasValue)
            && plan.ApprovedAt.HasValue
            && !string.IsNullOrWhiteSpace(plan.ApprovalComment)
            && string.Equals(plan.ApprovedChangeSetFingerprint, ComputeChangeSetFingerprint(plan), StringComparison.Ordinal);

    public static bool HasCompletedDryRun(RemediationPlan plan)
        => plan.Status is RemediationPlanStatus.DryRunCompleted or RemediationPlanStatus.Approved
            && HasAuditedApproval(plan)
            && plan.LastExecution is { Mode: RemediationExecutionMode.DryRun, Status: RemediationExecutionStatus.DryRunCompleted, MutatedCloud: false }
            && plan.Simulation is not null
            && string.Equals(plan.DryRunResultFingerprint, ComputeDryRunFingerprint(plan), StringComparison.Ordinal);

    public static string ComputeDryRunFingerprint(RemediationPlan plan)
    {
        var content = JsonSerializer.Serialize(new
        {
            ChangeSet = ComputeChangeSetFingerprint(plan),
            plan.Simulation,
            Execution = plan.LastExecution is null ? null : new
            {
                plan.LastExecution.Id,
                plan.LastExecution.Mode,
                plan.LastExecution.Status,
                plan.LastExecution.MutatedCloud,
                plan.LastExecution.Diagnostics,
                plan.LastExecution.StartedAt,
                plan.LastExecution.CompletedAt
            }
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    public static bool HasAuditedLiveApproval(RemediationPlan plan)
        => plan.Status == RemediationPlanStatus.Approved
            && HasCompletedDryRun(plan)
            && string.Equals(plan.ApprovedDryRunFingerprint, plan.DryRunResultFingerprint, StringComparison.Ordinal);
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
