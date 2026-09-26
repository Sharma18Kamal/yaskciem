using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;

namespace CloudEntitle.RemediationPlanning.Models;

public enum RemediationPlanStatus { Draft, RequiresReview, Approved, Rejected, Simulated, Implemented }
public enum SimulationStatus { NotRun, Simulated, RequiresReview, Unsupported, Invalid }
public enum AccessImpactStatus { Unknown, Preserved, PotentialAccessLoss, NoKnownImpact }

public sealed record RemediationPlan(
    Guid Id,
    RecommendationModel Recommendation,
    CloudProvider Provider,
    RemediationPlanStatus Status,
    IReadOnlyList<RemediationChange> Changes,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string SafetyNotice);

public sealed record RemediationChange(
    Guid Id,
    string ChangeType,
    string Target,
    string BeforeState,
    string ProposedState,
    bool Executable,
    string Rationale);

public sealed record RemediationSimulation(
    Guid PlanId,
    SimulationStatus Status,
    AccessImpactStatus AccessImpact,
    IReadOnlyList<Guid> AffectedEffectiveAccess,
    IReadOnlyList<Guid> RetainedEffectiveAccess,
    IReadOnlyList<Guid> PotentiallyLostEffectiveAccess,
    IReadOnlyList<string> Diagnostics,
    DateTime SimulatedAt);
