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
        if (string.IsNullOrWhiteSpace(requestedBy)) throw new ArgumentException("A plan creator is required.", nameof(requestedBy));
        var plan = planner.CreatePlan(recommendation, context);
        return plan with
        {
            CreatedBy = requestedBy.Trim(),
            AuditTrail = plan.AuditTrail.Append(new RemediationAuditEntry(DateTime.UtcNow, requestedBy, "PlanCreated", "Review-only plan and rollback draft created from recommendation evidence.")).ToArray()
        };
    }

    public RemediationPlan Approve(RemediationPlan plan, string approver, string comment, DateTime? asOf = null)
    {
        var isFinalExecutionApproval = plan.Status == RemediationPlanStatus.DryRunCompleted;
        if (plan.Status != RemediationPlanStatus.RequiresReview && !isFinalExecutionApproval)
            throw new InvalidOperationException("Only a review-required plan or a completed dry run can be approved.");
        if (!isFinalExecutionApproval && plan.Recommendation.EvaluationState != RiskEvaluationState.Confirmed)
            throw new InvalidOperationException("Only a recommendation with confirmed evidence can be approved.");
        if (isFinalExecutionApproval && !RemediationPlanIntegrity.HasCompletedDryRun(plan))
            throw new InvalidOperationException("Final execution approval requires an unchanged, completed non-mutating dry run.");
        if (string.IsNullOrWhiteSpace(approver)) throw new ArgumentException("An approver is required.", nameof(approver));
        if (string.IsNullOrWhiteSpace(comment)) throw new ArgumentException("An approval rationale is required.", nameof(comment));
        if (string.IsNullOrWhiteSpace(plan.CreatedBy)) throw new InvalidOperationException("The plan creator is not recorded; independent approval cannot be verified.");
        if (string.Equals(plan.CreatedBy.Trim(), approver.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The plan creator cannot approve their own remediation plan.");
        if (isFinalExecutionApproval && string.Equals(plan.ApprovedBy?.Trim(), approver.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Final execution approval must be recorded by a reviewer other than the dry-run approver.");
        var now = asOf ?? DateTime.UtcNow;
        var approved = plan with
        {
            Status = RemediationPlanStatus.Approved,
            ApprovedBy = approver.Trim(),
            ApprovedAt = now,
            ApprovalComment = comment.Trim(),
            UpdatedAt = now,
            AuditTrail = plan.AuditTrail
        };
        var fingerprint = RemediationPlanIntegrity.ComputeChangeSetFingerprint(approved);
        var dryRunFingerprint = isFinalExecutionApproval ? plan.DryRunResultFingerprint : null;
        return approved with
        {
            ApprovedChangeSetFingerprint = fingerprint,
            ApprovedDryRunFingerprint = dryRunFingerprint,
            AuditTrail = plan.AuditTrail.Append(new RemediationAuditEntry(now, approver.Trim(), isFinalExecutionApproval ? "ApprovedForExecution" : "Approved",
                $"{comment.Trim()} (ChangeSetSHA256={fingerprint}; DryRunSHA256={dryRunFingerprint ?? "not run"})")).ToArray()
        };
    }

    public RemediationPlan Reject(RemediationPlan plan, string reviewer, string reason, DateTime? asOf = null)
    {
        if (plan.Status is not (RemediationPlanStatus.RequiresReview or RemediationPlanStatus.Draft or RemediationPlanStatus.DryRunCompleted))
            throw new InvalidOperationException("Only a draft, review-required, or dry-run-completed plan can be rejected.");
        if (string.IsNullOrWhiteSpace(reviewer)) throw new ArgumentException("A reviewer is required.", nameof(reviewer));
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A rejection reason is required.", nameof(reason));
        if (!string.IsNullOrWhiteSpace(plan.CreatedBy) && string.Equals(plan.CreatedBy.Trim(), reviewer.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The plan creator cannot reject their own remediation plan as an independent review.");
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
        if (!RemediationPlanIntegrity.HasAuditedApproval(plan)) throw new InvalidOperationException("Approve the unchanged plan with an independent audited rationale before running its dry run.");
        var request = new RemediationExecutionRequest(plan, RemediationExecutionMode.DryRun, requestedBy, ExplicitApproval: true);
        var execution = await engine.ExecuteAsync(request, cancellationToken);
        var simulation = simulator.Simulate(plan, context);
        var now = DateTime.UtcNow;
        var completed = execution.Status == RemediationExecutionStatus.DryRunCompleted;
        var completedPlan = plan with
        {
            Status = completed ? RemediationPlanStatus.DryRunCompleted : plan.Status,
            Simulation = simulation,
            LastExecution = execution,
            UpdatedAt = now,
            AuditTrail = plan.AuditTrail.Append(new RemediationAuditEntry(now, requestedBy, "DryRun", string.Join("; ", execution.Diagnostics))).ToArray()
        };
        return completed && !execution.MutatedCloud
            ? completedPlan with { DryRunResultFingerprint = RemediationPlanIntegrity.ComputeDryRunFingerprint(completedPlan) }
            : completedPlan;
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
            CreatedBy = requestedBy.Trim(),
            IsRollbackDraft = true,
            RollbackChanges = plan.Changes.Select(change => change with { Id = Guid.NewGuid(), Executable = false }).ToArray(),
            AuditTrail = audit
        };
    }

}
