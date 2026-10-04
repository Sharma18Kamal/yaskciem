using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.RemediationPlanning.Abstractions;
using CloudEntitle.RemediationPlanning.Models;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;
using DomainAccessPath = CloudEntitle.Domain.Entities.AccessPath;

namespace CloudEntitle.RemediationPlanning.Services;

public sealed class RemediationWorkflowService(
    IRemediationPlanner planner,
    IRemediationSimulator simulator,
    IRemediationEngine engine)
{
    public RemediationPlan CreatePlan(RecommendationModel recommendation, RemediationContext context, string requestedBy)
    {
        var plan = planner.CreatePlan(recommendation, context);
        return plan with
        {
            AuditTrail = plan.AuditTrail.Append(new RemediationAuditEntry(DateTime.UtcNow, requestedBy, "PlanCreated", "Review-only plan and rollback draft created from recommendation evidence.")).ToArray()
        };
    }

    public RemediationPlan Approve(RemediationPlan plan, string approver, string comment, DateTime? asOf = null)
    {
        if (plan.Status is not (RemediationPlanStatus.RequiresReview or RemediationPlanStatus.Draft))
            throw new InvalidOperationException("Only a draft or review-required remediation plan can be approved.");
        if (string.IsNullOrWhiteSpace(approver)) throw new ArgumentException("An approver is required.", nameof(approver));
        if (string.IsNullOrWhiteSpace(comment)) throw new ArgumentException("An approval rationale is required.", nameof(comment));
        var now = asOf ?? DateTime.UtcNow;
        return plan with
        {
            Status = RemediationPlanStatus.Approved,
            ApprovedBy = approver.Trim(),
            ApprovedAt = now,
            ApprovalComment = comment.Trim(),
            UpdatedAt = now,
            AuditTrail = plan.AuditTrail.Append(new RemediationAuditEntry(now, approver.Trim(), "Approved", comment.Trim())).ToArray()
        };
    }

    public RemediationPlan Reject(RemediationPlan plan, string reviewer, string reason, DateTime? asOf = null)
    {
        if (string.IsNullOrWhiteSpace(reviewer)) throw new ArgumentException("A reviewer is required.", nameof(reviewer));
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A rejection reason is required.", nameof(reason));
        var now = asOf ?? DateTime.UtcNow;
        return plan with
        {
            Status = RemediationPlanStatus.Rejected,
            UpdatedAt = now,
            AuditTrail = plan.AuditTrail.Append(new RemediationAuditEntry(now, reviewer.Trim(), "Rejected", reason.Trim())).ToArray()
        };
    }

    public async Task<RemediationPlan> RunDryRunAsync(RemediationPlan plan, RemediationContext context, string requestedBy, CancellationToken cancellationToken = default)
    {
        if (!HasAuditedApproval(plan)) throw new InvalidOperationException("Approve the plan with an audited rationale before running its dry run.");
        var request = new RemediationExecutionRequest(plan, RemediationExecutionMode.DryRun, requestedBy, ExplicitApproval: true);
        var execution = await engine.ExecuteAsync(request, cancellationToken);
        var simulation = simulator.Simulate(plan, context);
        var now = DateTime.UtcNow;
        var completed = execution.Status == RemediationExecutionStatus.DryRunCompleted;
        return plan with
        {
            Status = completed ? RemediationPlanStatus.DryRunCompleted : plan.Status,
            Simulation = simulation,
            LastExecution = execution,
            UpdatedAt = now,
            AuditTrail = plan.AuditTrail.Append(new RemediationAuditEntry(now, requestedBy, "DryRun", string.Join("; ", execution.Diagnostics))).ToArray()
        };
    }

    public async Task<RemediationPlan> VerifyAsync(RemediationPlan plan, string requestedBy, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        if (plan.LastExecution?.MutatedCloud != true || plan.LastExecution.Status is not (RemediationExecutionStatus.Executed or RemediationExecutionStatus.VerificationFailed))
        {
            var notApplied = new RemediationVerification(RemediationVerificationStatus.Unavailable, "No provider mutation was confirmed; post-change verification cannot be performed in this build.", now);
            return plan with
            {
                Verification = notApplied,
                AuditTrail = plan.AuditTrail.Append(new RemediationAuditEntry(now, requestedBy, "VerificationUnavailable", notApplied.Details)).ToArray(),
                UpdatedAt = now
            };
        }

        var result = await engine.VerifyAsync(plan, cancellationToken);
        var verification = new RemediationVerification(result.IsValid ? RemediationVerificationStatus.Verified : RemediationVerificationStatus.Failed, string.Join("; ", result.Diagnostics), now);
        return plan with
        {
            Verification = verification,
            Status = result.IsValid ? RemediationPlanStatus.Implemented : RemediationPlanStatus.VerificationFailed,
            UpdatedAt = now,
            AuditTrail = plan.AuditTrail.Append(new RemediationAuditEntry(now, requestedBy, "Verification", verification.Details)).ToArray()
        };
    }

    public RemediationPlan PrepareRollback(RemediationPlan plan, string requestedBy, string rationale, DateTime? asOf = null)
    {
        if (plan.Status is not (RemediationPlanStatus.DryRunCompleted or RemediationPlanStatus.Implemented or RemediationPlanStatus.VerificationFailed))
            throw new InvalidOperationException("A rollback draft can only be prepared after a dry run or implementation attempt.");
        if (plan.RollbackChanges.Count == 0) throw new InvalidOperationException("No rollback state was captured in this plan.");
        if (string.IsNullOrWhiteSpace(requestedBy)) throw new ArgumentException("A requesting actor is required.", nameof(requestedBy));
        if (string.IsNullOrWhiteSpace(rationale)) throw new ArgumentException("A rollback rationale is required.", nameof(rationale));
        var now = asOf ?? DateTime.UtcNow;
        var rollbackChanges = plan.RollbackChanges.Select(change => change with { Id = Guid.NewGuid(), Executable = false }).ToArray();
        var audit = new[] { new RemediationAuditEntry(now, requestedBy.Trim(), "RollbackDraftPrepared", rationale.Trim()) };
        return new RemediationPlan(Guid.NewGuid(), plan.Recommendation, plan.Provider, RemediationPlanStatus.RequiresReview,
            rollbackChanges, now, now, "Rollback draft is non-executable and requires independent approval.")
        {
            IsRollbackDraft = true,
            RollbackChanges = plan.Changes.Select(change => change with { Id = Guid.NewGuid(), Executable = false }).ToArray(),
            AuditTrail = audit
        };
    }

    private static bool HasAuditedApproval(RemediationPlan plan)
        => plan.Status == RemediationPlanStatus.Approved
            && !string.IsNullOrWhiteSpace(plan.ApprovedBy)
            && plan.ApprovedAt.HasValue
            && !string.IsNullOrWhiteSpace(plan.ApprovalComment);

}
