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
    public RemediationPlan CreatePlan(RecommendationModel recommendation, RemediationContext context, string requestedBy, Guid? requestedByUserId = null, Guid? organizationId = null)
    {
        if (string.IsNullOrWhiteSpace(requestedBy)) throw new ArgumentException("A plan creator is required.", nameof(requestedBy));
        var plan = planner.CreatePlan(recommendation, context);
        return plan with
        {
            CreatedBy = requestedBy.Trim(),
            CreatedByUserId = requestedByUserId,
            OrganizationId = organizationId,
            AuditTrail = plan.AuditTrail.Append(new RemediationAuditEntry(DateTime.UtcNow, requestedBy.Trim(), "PlanCreated", "Review-only plan and rollback draft created from recommendation evidence.") { ActorUserId = requestedByUserId }).ToArray()
        };
    }

    public RemediationPlan Approve(RemediationPlan plan, string approver, string comment, DateTime? asOf = null, Guid? approverUserId = null)
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
        if (plan.OrganizationId.HasValue && (!approverUserId.HasValue || approverUserId == Guid.Empty))
            throw new InvalidOperationException("An authenticated organization identity is required to approve this remediation plan.");
        if (plan.CreatedByUserId.HasValue && approverUserId == plan.CreatedByUserId)
            throw new InvalidOperationException("The plan creator cannot approve their own remediation plan.");
        if (string.Equals(plan.CreatedBy.Trim(), approver.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The plan creator cannot approve their own remediation plan.");
        if (isFinalExecutionApproval && (plan.DryRunApprovedByUserId.HasValue && plan.DryRunApprovedByUserId == approverUserId
            || string.Equals(plan.ApprovedBy?.Trim(), approver.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Final execution approval must be recorded by a reviewer other than the dry-run approver.");
        var now = asOf ?? DateTime.UtcNow;
        var approved = plan with
        {
            Status = RemediationPlanStatus.Approved,
            ApprovedBy = approver.Trim(),
            ApprovedByUserId = approverUserId,
            DryRunApprovedByUserId = isFinalExecutionApproval ? plan.DryRunApprovedByUserId : approverUserId,
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
                $"{comment.Trim()} (ChangeSetSHA256={fingerprint}; DryRunSHA256={dryRunFingerprint ?? "not run"})") { ActorUserId = approverUserId }).ToArray()
        };
    }

    public RemediationPlan Reject(RemediationPlan plan, string reviewer, string reason, DateTime? asOf = null, Guid? reviewerUserId = null)
    {
        if (plan.Status is not (RemediationPlanStatus.RequiresReview or RemediationPlanStatus.Draft or RemediationPlanStatus.DryRunCompleted))
            throw new InvalidOperationException("Only a draft, review-required, or dry-run-completed plan can be rejected.");
        if (string.IsNullOrWhiteSpace(reviewer)) throw new ArgumentException("A reviewer is required.", nameof(reviewer));
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A rejection reason is required.", nameof(reason));
        if (plan.OrganizationId.HasValue && (!reviewerUserId.HasValue || reviewerUserId == Guid.Empty))
            throw new InvalidOperationException("An authenticated organization identity is required to reject this remediation plan.");
        if (plan.CreatedByUserId.HasValue && reviewerUserId == plan.CreatedByUserId)
            throw new InvalidOperationException("The plan creator cannot reject their own remediation plan as an independent review.");
        if (!string.IsNullOrWhiteSpace(plan.CreatedBy) && string.Equals(plan.CreatedBy.Trim(), reviewer.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The plan creator cannot reject their own remediation plan as an independent review.");
        var now = asOf ?? DateTime.UtcNow;
        return plan with
        {
            Status = RemediationPlanStatus.Rejected,
            UpdatedAt = now,
            AuditTrail = plan.AuditTrail.Append(new RemediationAuditEntry(now, reviewer.Trim(), "Rejected", reason.Trim()) { ActorUserId = reviewerUserId }).ToArray()
        };
    }

    public async Task<RemediationPlan> RunDryRunAsync(RemediationPlan plan, RemediationContext context, string requestedBy, CancellationToken cancellationToken = default, Guid? requestedByUserId = null)
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
            AuditTrail = plan.AuditTrail.Append(new RemediationAuditEntry(now, requestedBy, "DryRun", string.Join("; ", execution.Diagnostics)) { ActorUserId = requestedByUserId }).ToArray()
        };
        return completed && !execution.MutatedCloud
            ? completedPlan with { DryRunResultFingerprint = RemediationPlanIntegrity.ComputeDryRunFingerprint(completedPlan) }
            : completedPlan;
    }

    public async Task<RemediationPlan> VerifyAsync(RemediationPlan plan, string requestedBy, CancellationToken cancellationToken = default, Guid? requestedByUserId = null)
    {
        var now = DateTime.UtcNow;
        if (plan.LastExecution?.MutatedCloud != true || plan.LastExecution.Status is not (RemediationExecutionStatus.Executed or RemediationExecutionStatus.VerificationFailed))
        {
            var notApplied = new RemediationVerification(RemediationVerificationStatus.Unavailable, "No provider mutation was confirmed; post-change verification cannot be performed in this build.", now);
            return plan with
            {
                Verification = notApplied,
                AuditTrail = plan.AuditTrail.Append(new RemediationAuditEntry(now, requestedBy, "VerificationUnavailable", notApplied.Details) { ActorUserId = requestedByUserId }).ToArray(),
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
            AuditTrail = plan.AuditTrail.Append(new RemediationAuditEntry(now, requestedBy, "Verification", verification.Details) { ActorUserId = requestedByUserId }).ToArray()
        };
    }

    public RemediationPlan PrepareRollback(RemediationPlan plan, string requestedBy, string rationale, DateTime? asOf = null, Guid? requestedByUserId = null)
    {
        if (plan.Status is not (RemediationPlanStatus.DryRunCompleted or RemediationPlanStatus.Implemented or RemediationPlanStatus.VerificationFailed))
            throw new InvalidOperationException("A rollback draft can only be prepared after a dry run or implementation attempt.");
        if (plan.RollbackChanges.Count == 0) throw new InvalidOperationException("No rollback state was captured in this plan.");
        if (string.IsNullOrWhiteSpace(requestedBy)) throw new ArgumentException("A requesting actor is required.", nameof(requestedBy));
        if (string.IsNullOrWhiteSpace(rationale)) throw new ArgumentException("A rollback rationale is required.", nameof(rationale));
        var now = asOf ?? DateTime.UtcNow;
        var rollbackChanges = plan.RollbackChanges.Select(change => change with { Id = Guid.NewGuid(), Executable = false }).ToArray();
        var audit = new[] { new RemediationAuditEntry(now, requestedBy.Trim(), "RollbackDraftPrepared", rationale.Trim()) { ActorUserId = requestedByUserId } };
        return new RemediationPlan(Guid.NewGuid(), plan.Recommendation, plan.Provider, RemediationPlanStatus.RequiresReview,
            rollbackChanges, now, now, "Rollback draft is non-executable and requires independent approval.")
        {
            CreatedBy = requestedBy.Trim(),
            CreatedByUserId = requestedByUserId,
            OrganizationId = plan.OrganizationId,
            IsRollbackDraft = true,
            ProviderExecution = plan.ProviderExecution,
            WhatIfReportJson = null,
            WhatIfReportFingerprint = null,
            WhatIfEvaluatedAtUtc = null,
            WhatIfSourceSnapshotAtUtc = null,
            RollbackPreparedAfterUtc = plan.LastExecution?.CompletedAt,
            RollbackChanges = plan.Changes.Select(change => change with { Id = Guid.NewGuid(), Executable = false }).ToArray(),
            AuditTrail = audit
        };
    }

    public RemediationPlan RecordProviderExecution(
        RemediationPlan plan,
        RemediationExecution execution,
        string requestedBy,
        string providerPlanStatus,
        string details,
        DateTime? asOf = null,
        Guid? requestedByUserId = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(execution);
        if (plan.ProviderExecution is null || plan.ProviderExecution.ProviderPlanId == Guid.Empty)
            throw new InvalidOperationException("A provider-prepared plan is required to record provider execution.");
        if (execution.PlanId != plan.Id || execution.Provider != plan.Provider || execution.Mode != RemediationExecutionMode.Live)
            throw new InvalidOperationException("Execution record does not match the selected remediation plan.");
        if (string.IsNullOrWhiteSpace(requestedBy) || string.IsNullOrWhiteSpace(providerPlanStatus))
            throw new ArgumentException("An execution actor and provider status are required.");

        var now = asOf ?? DateTime.UtcNow;
        var nextStatus = execution.Status switch
        {
            RemediationExecutionStatus.Verified => RemediationPlanStatus.Implemented,
            RemediationExecutionStatus.RolledBack => RemediationPlanStatus.RolledBack,
            RemediationExecutionStatus.OutcomeUnknown => RemediationPlanStatus.OutcomeUnknown,
            RemediationExecutionStatus.Reconciled => RemediationPlanStatus.ExecutionFailed,
            RemediationExecutionStatus.VerificationFailed => RemediationPlanStatus.VerificationFailed,
            RemediationExecutionStatus.Failed or RemediationExecutionStatus.Blocked => RemediationPlanStatus.ExecutionFailed,
            _ => throw new InvalidOperationException("Unsupported provider execution status.")
        };
        var link = plan.ProviderExecution with { Status = providerPlanStatus };
        var verification = execution.Verification ?? plan.Verification;
        return plan with
        {
            ProviderExecution = link,
            Status = nextStatus,
            LastExecution = execution,
            ExecutionHistory = plan.ExecutionHistory.Append(execution).ToArray(),
            Verification = verification,
            UpdatedAt = now,
            AuditTrail = plan.AuditTrail.Append(new RemediationAuditEntry(now, requestedBy.Trim(),
                execution.Status.ToString(), details.Trim()) { ActorUserId = requestedByUserId }).ToArray()
        };
    }

    public RemediationPlan AttachWhatIfEvidence(
        RemediationPlan plan,
        string reportJson,
        string reportFingerprint,
        DateTime evaluatedAt,
        string requestedBy,
        Guid? requestedByUserId = null,
        DateTime? sourceSnapshotAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Status is not (RemediationPlanStatus.Draft or RemediationPlanStatus.RequiresReview))
            throw new InvalidOperationException("What-if evidence can only be attached before approval.");
        if (string.IsNullOrWhiteSpace(reportJson) || string.IsNullOrWhiteSpace(reportFingerprint)
            || !sourceSnapshotAtUtc.HasValue
            || string.IsNullOrWhiteSpace(requestedBy))
            throw new ArgumentException("A simulation report, source snapshot timestamp, fingerprint and requesting actor are required.");
        var now = DateTime.UtcNow;
        return plan with
        {
            WhatIfReportJson = reportJson,
            WhatIfReportFingerprint = reportFingerprint,
            WhatIfEvaluatedAtUtc = evaluatedAt,
            WhatIfSourceSnapshotAtUtc = sourceSnapshotAtUtc,
            Simulation = null,
            Status = plan.Recommendation.EvaluationState == RiskEvaluationState.Confirmed
                ? RemediationPlanStatus.RequiresReview
                : RemediationPlanStatus.Draft,
            UpdatedAt = now,
            AuditTrail = plan.AuditTrail.Append(new RemediationAuditEntry(now, requestedBy.Trim(),
                "WhatIfEvidenceAttached", $"Attached What-if report SHA-256={reportFingerprint}.")
            { ActorUserId = requestedByUserId }).ToArray()
        };
    }

    public RemediationPlan AttachProviderPreview(
        RemediationPlan plan,
        RemediationProviderExecutionLink providerExecution,
        string requestedBy,
        Guid? requestedByUserId = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(providerExecution);
        if (plan.Status is not (RemediationPlanStatus.Draft or RemediationPlanStatus.RequiresReview or RemediationPlanStatus.ExecutionFailed))
            throw new InvalidOperationException("Provider previews can only be attached before approval.");
        if (plan.Recommendation.EvaluationState != RiskEvaluationState.Confirmed
            || string.IsNullOrWhiteSpace(plan.WhatIfReportFingerprint)
            || !string.Equals(plan.Provider.ToString(), plan.Recommendation.Provider.ToString(), StringComparison.Ordinal)
            || providerExecution.ProviderPlanId == Guid.Empty
            || string.IsNullOrWhiteSpace(providerExecution.AccountId)
            || string.IsNullOrWhiteSpace(providerExecution.RequestFingerprint)
            || string.IsNullOrWhiteSpace(providerExecution.BeforeJson)
            || string.IsNullOrWhiteSpace(providerExecution.AfterJson)
            || string.IsNullOrWhiteSpace(requestedBy))
            throw new InvalidOperationException("A confirmed recommendation, What-if report and complete provider preview are required.");

        var change = new RemediationChange(Guid.NewGuid(), providerExecution.Operation,
            $"{plan.Provider}:{providerExecution.AccountId}", providerExecution.BeforeJson,
            providerExecution.AfterJson, true,
            "Provider-generated before/after state from the guarded administration preview. Requires independent approvals and a fresh pre-write check.")
        {
            RollbackState = providerExecution.BeforeJson
        };
        var rollback = new RemediationChange(Guid.NewGuid(), $"Rollback{providerExecution.Operation}",
            change.Target, providerExecution.AfterJson, providerExecution.BeforeJson, false,
            "Compensating restoration from the captured provider state. Revalidate and independently approve before rollback.")
        {
            RollbackState = providerExecution.AfterJson
        };
        var now = DateTime.UtcNow;
        return plan with
        {
            ProviderExecution = providerExecution,
            Changes = new[] { change },
            RollbackChanges = new[] { rollback },
            Status = RemediationPlanStatus.RequiresReview,
            ApprovedBy = null,
            ApprovedByUserId = null,
            ApprovedAt = null,
            ApprovalComment = null,
            ApprovedChangeSetFingerprint = null,
            DryRunApprovedByUserId = null,
            DryRunResultFingerprint = null,
            ApprovedDryRunFingerprint = null,
            Simulation = null,
            LastExecution = null,
            Verification = null,
            UpdatedAt = now,
            AuditTrail = plan.AuditTrail.Append(new RemediationAuditEntry(now, requestedBy.Trim(),
                "ProviderPreviewAttached", $"Provider before/after preview attached; request SHA-256={providerExecution.RequestFingerprint}.")
            { ActorUserId = requestedByUserId }).ToArray()
        };
    }

}
