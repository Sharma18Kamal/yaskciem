using CloudEntitle.Domain;

namespace CloudEntitle.RemediationPlanning.Models;

public enum RemediationExecutionMode { DryRun, Live }
public enum RemediationExecutionStatus { NotStarted, Blocked, DryRunCompleted, Approved, Executed, Verified, Failed }

public sealed record RemediationExecutionRequest(
    RemediationPlan Plan,
    RemediationExecutionMode Mode,
    string RequestedBy,
    bool ExplicitApproval,
    bool RequireVerification = true);

public sealed record RemediationExecution(
    Guid Id,
    Guid PlanId,
    CloudProvider Provider,
    RemediationExecutionMode Mode,
    RemediationExecutionStatus Status,
    bool MutatedCloud,
    IReadOnlyList<string> Diagnostics,
    DateTime StartedAt,
    DateTime CompletedAt);

public sealed record ProviderChangeValidation(bool IsValid, IReadOnlyList<string> Diagnostics);
public sealed record ProviderChangeResult(bool Succeeded, bool MutatedCloud, IReadOnlyList<string> Diagnostics);
