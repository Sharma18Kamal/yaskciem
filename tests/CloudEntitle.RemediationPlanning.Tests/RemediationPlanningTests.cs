using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Recommendations.Models;
using CloudEntitle.RemediationPlanning.Abstractions;
using CloudEntitle.RemediationPlanning.Models;
using CloudEntitle.RemediationPlanning.Services;
using Xunit;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;
using DomainAccessPath = CloudEntitle.Domain.Entities.AccessPath;

namespace CloudEntitle.RemediationPlanning.Tests;

public sealed class RemediationPlanningTests
{
    [Fact]
    public void ConfirmedRecommendation_ShouldCreateReviewOnlyPlan()
    {
        var fixture = FixtureData.Create(RiskEvaluationState.Confirmed);
        var plan = new RemediationPlanner().CreatePlan(fixture.Recommendation, fixture.Context);

        Assert.Equal(RemediationPlanStatus.RequiresReview, plan.Status);
        Assert.False(plan.Changes.Single().Executable);
        Assert.Contains("non-executable", plan.SafetyNotice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Simulation_ShouldNeverClaimAccessPreservedWithoutProviderEvaluation()
    {
        var fixture = FixtureData.Create(RiskEvaluationState.Confirmed);
        var plan = new RemediationPlanner().CreatePlan(fixture.Recommendation, fixture.Context);
        var simulation = new RemediationSimulator().Simulate(plan, fixture.Context);

        Assert.Equal(SimulationStatus.RequiresReview, simulation.Status);
        Assert.Equal(AccessImpactStatus.Unknown, simulation.AccessImpact);
        Assert.Contains(simulation.Diagnostics, diagnostic => diagnostic.Contains("not re-evaluated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnknownRecommendation_ShouldRemainDraftAndInvalidSimulationIsVisible()
    {
        var fixture = FixtureData.Create(RiskEvaluationState.Unknown);
        var plan = new RemediationPlanner().CreatePlan(fixture.Recommendation, fixture.Context);
        var simulation = new RemediationSimulator().Simulate(plan, fixture.Context);

        Assert.Equal(RemediationPlanStatus.Draft, plan.Status);
        Assert.Equal(SimulationStatus.RequiresReview, simulation.Status);
        Assert.Contains(simulation.Diagnostics, diagnostic => diagnostic.Contains("incomplete", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Workflow_ShouldRequireApprovalAndKeepDryRunNonMutating()
    {
        var fixture = FixtureData.Create(RiskEvaluationState.Confirmed);
        var workflow = new RemediationWorkflowService(new RemediationPlanner(), new RemediationSimulator(), new RemediationEngine());
        var plan = workflow.CreatePlan(fixture.Recommendation, fixture.Context, "requester");

        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.RunDryRunAsync(plan, fixture.Context, "requester"));

        var approved = workflow.Approve(plan, "reviewer", "Reviewed evidence and approved the proposed change.");
        var dryRun = await workflow.RunDryRunAsync(approved, fixture.Context, "requester");

        Assert.Equal(RemediationPlanStatus.DryRunCompleted, dryRun.Status);
        Assert.Equal("reviewer", dryRun.ApprovedBy);
        Assert.Equal(RemediationExecutionStatus.DryRunCompleted, dryRun.LastExecution?.Status);
        Assert.False(dryRun.LastExecution?.MutatedCloud);
        Assert.Contains(dryRun.AuditTrail, entry => entry.Action == "Approved" && entry.Actor == "reviewer");
    }

    [Fact]
    public async Task LiveAttempt_ShouldRequireFinalApprovalAndRemainBlockedForReviewOnlyChanges()
    {
        var fixture = FixtureData.Create(RiskEvaluationState.Confirmed);
        var workflow = new RemediationWorkflowService(new RemediationPlanner(), new RemediationSimulator(), new RemediationEngine());
        var plan = workflow.CreatePlan(fixture.Recommendation, fixture.Context, "requester");
        var approvedForDryRun = workflow.Approve(plan, "reviewer-one", "Approve non-mutating preview.");
        var dryRun = await workflow.RunDryRunAsync(approvedForDryRun, fixture.Context, "requester");
        var finalApproval = workflow.Approve(dryRun, "reviewer-two", "Approve the reviewed dry-run result.");

        Assert.True(RemediationPlanIntegrity.HasAuditedLiveApproval(finalApproval));
        var execution = await new RemediationEngine().ExecuteAsync(new RemediationExecutionRequest(finalApproval, RemediationExecutionMode.Live, "operator", true));

        Assert.Equal(RemediationExecutionStatus.Blocked, execution.Status);
        Assert.False(execution.MutatedCloud);
        Assert.Contains(execution.Diagnostics, item => item.Contains("no provider-approved executable changes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PlanCreator_ShouldNotApproveOwnPlan()
    {
        var fixture = FixtureData.Create(RiskEvaluationState.Confirmed);
        var workflow = new RemediationWorkflowService(new RemediationPlanner(), new RemediationSimulator(), new RemediationEngine());
        var plan = workflow.CreatePlan(fixture.Recommendation, fixture.Context, "requester");

        Assert.Throws<InvalidOperationException>(() => workflow.Approve(plan, "REQUESTER", "Self approval is not allowed."));
    }

    [Fact]
    public void RollbackPreviewChange_ShouldInvalidatePriorApproval()
    {
        var fixture = FixtureData.Create(RiskEvaluationState.Confirmed);
        var workflow = new RemediationWorkflowService(new RemediationPlanner(), new RemediationSimulator(), new RemediationEngine());
        var plan = workflow.CreatePlan(fixture.Recommendation, fixture.Context, "requester");
        var approved = workflow.Approve(plan, "reviewer", "Reviewed the change and rollback preview.");
        var changed = approved with { RollbackChanges = new[] { approved.RollbackChanges[0] with { ProposedState = "modified rollback" } } };

        Assert.True(RemediationPlanIntegrity.HasAuditedApproval(approved));
        Assert.False(RemediationPlanIntegrity.HasAuditedApproval(changed));
    }

    [Fact]
    public void IncompleteEvidenceDraft_ShouldNotBeApproved()
    {
        var fixture = FixtureData.Create(RiskEvaluationState.Unknown);
        var workflow = new RemediationWorkflowService(new RemediationPlanner(), new RemediationSimulator(), new RemediationEngine());
        var plan = workflow.CreatePlan(fixture.Recommendation, fixture.Context, "requester");

        Assert.Equal(RemediationPlanStatus.Draft, plan.Status);
        Assert.Throws<InvalidOperationException>(() => workflow.Approve(plan, "reviewer", "Evidence is not complete."));
    }

    [Fact]
    public async Task VerificationAfterNonMutatingDryRun_ShouldBeUnavailable()
    {
        var fixture = FixtureData.Create(RiskEvaluationState.Confirmed);
        var workflow = new RemediationWorkflowService(new RemediationPlanner(), new RemediationSimulator(), new RemediationEngine());
        var plan = workflow.CreatePlan(fixture.Recommendation, fixture.Context, "requester");
        var approved = workflow.Approve(plan, "reviewer", "Approved for dry-run only.");
        var dryRun = await workflow.RunDryRunAsync(approved, fixture.Context, "requester");

        var verification = await workflow.VerifyAsync(dryRun, "verifier");

        Assert.Equal(RemediationVerificationStatus.Unavailable, verification.Verification?.Status);
        Assert.Equal(RemediationPlanStatus.DryRunCompleted, verification.Status);
        Assert.Contains(verification.AuditTrail, entry => entry.Action == "VerificationUnavailable");
    }

    [Fact]
    public void Rejection_ShouldRequireAndRecordReviewerRationale()
    {
        var fixture = FixtureData.Create(RiskEvaluationState.Confirmed);
        var workflow = new RemediationWorkflowService(new RemediationPlanner(), new RemediationSimulator(), new RemediationEngine());
        var plan = workflow.CreatePlan(fixture.Recommendation, fixture.Context, "requester");

        var rejected = workflow.Reject(plan, "reviewer", "Owner evidence does not support this change.");

        Assert.Equal(RemediationPlanStatus.Rejected, rejected.Status);
        var audit = Assert.Single(rejected.AuditTrail, entry => entry.Action == "Rejected");
        Assert.Equal("reviewer", audit.Actor);
        Assert.Equal("Owner evidence does not support this change.", audit.Details);
    }

    [Fact]
    public void RollbackPreparation_ShouldCreateSeparateNonExecutableApprovalDraft()
    {
        var fixture = FixtureData.Create(RiskEvaluationState.Confirmed);
        var planner = new RemediationPlanner();
        var workflow = new RemediationWorkflowService(planner, new RemediationSimulator(), new RemediationEngine());
        var plan = planner.CreatePlan(fixture.Recommendation, fixture.Context) with
        {
            Status = RemediationPlanStatus.DryRunCompleted,
            RollbackChanges = new[] { new RemediationChange(Guid.NewGuid(), "RestoreState", "test-target", "reduced", "original", false, "Captured original state.") }
        };

        var rollback = workflow.PrepareRollback(plan, "operator", "Prepare an independently reviewed rollback.");

        Assert.NotEqual(plan.Id, rollback.Id);
        Assert.True(rollback.IsRollbackDraft);
        Assert.Equal(RemediationPlanStatus.RequiresReview, rollback.Status);
        Assert.All(rollback.Changes, change => Assert.False(change.Executable));
        Assert.Null(rollback.ApprovedBy);
        Assert.Contains(rollback.AuditTrail, entry => entry.Action == "RollbackDraftPrepared");

        var separatelyApproved = workflow.Approve(rollback, "rollback-reviewer", "Rollback reviewed independently.");
        Assert.Equal("rollback-reviewer", separatelyApproved.ApprovedBy);
    }

    private sealed record FixtureData(RecommendationModel Recommendation, RemediationContext Context)
    {
        public static FixtureData Create(RiskEvaluationState state)
        {
            var accountId = Guid.NewGuid();
            var principalId = Guid.NewGuid();
            var accessId = Guid.NewGuid();
            var findingId = Guid.NewGuid();
            var recommendation = new RecommendationModel { Id = Guid.NewGuid(), RiskFindingId = findingId, RecommendationType = RecommendationType.NarrowWildcardPermission, Provider = CloudProvider.Aws, CloudAccountId = accountId, PrincipalId = principalId, EffectiveAccessId = accessId, EvaluationState = state, CurrentState = "Action=*", SuggestedState = "Specific actions", Status = RecommendationStatus.New };
            var access = new DomainEffectiveAccess { Id = accessId, CloudAccountId = accountId, PrincipalId = principalId, Provider = CloudProvider.Aws, Decision = AccessDecision.Allow, EvaluationStatus = EvaluationStatus.FullyEvaluated };
            return new(recommendation, new RemediationContext(new[] { recommendation }, new[] { access }, Array.Empty<DomainAccessPath>()));
        }
    }
}
