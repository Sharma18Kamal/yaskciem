using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Recommendations.Models;
using CloudEntitle.RemediationPlanning.Models;
using CloudEntitle.RemediationPlanning.Services;
using Xunit;

namespace CloudEntitle.RemediationPlanning.Tests;

public sealed class RemediationEngineTests
{
    [Fact]
    public async Task DryRun_ShouldCompleteWithoutMutation()
    {
        var plan = CreatePlan(RemediationPlanStatus.RequiresReview);
        var execution = await new RemediationEngine().ExecuteAsync(new RemediationExecutionRequest(plan, RemediationExecutionMode.DryRun, "tester", false));

        Assert.Equal(RemediationExecutionStatus.DryRunCompleted, execution.Status);
        Assert.False(execution.MutatedCloud);
    }

    [Fact]
    public async Task LiveExecutionWithoutApproval_ShouldBeBlocked()
    {
        var plan = CreatePlan(RemediationPlanStatus.RequiresReview);
        var execution = await new RemediationEngine().ExecuteAsync(new RemediationExecutionRequest(plan, RemediationExecutionMode.Live, "tester", false));

        Assert.Equal(RemediationExecutionStatus.Blocked, execution.Status);
        Assert.False(execution.MutatedCloud);
        Assert.Contains(execution.Diagnostics, item => item.Contains("approved", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ApprovedLiveExecution_ShouldRemainBlockedUntilMutationAdapterExists()
    {
        var plan = CreatePlan(RemediationPlanStatus.Approved);
        var execution = await new RemediationEngine().ExecuteAsync(new RemediationExecutionRequest(plan, RemediationExecutionMode.Live, "tester", true));

        Assert.Equal(RemediationExecutionStatus.Blocked, execution.Status);
        Assert.False(execution.MutatedCloud);
        Assert.Contains(execution.Diagnostics, item => item.Contains("disabled", StringComparison.OrdinalIgnoreCase));
    }

    private static RemediationPlan CreatePlan(RemediationPlanStatus status)
    {
        var recommendation = new RecommendationModel { Id = Guid.NewGuid(), Provider = CloudProvider.Azure, CloudAccountId = Guid.NewGuid(), RecommendationType = RecommendationType.NarrowWildcardPermission, CurrentState = "*", SuggestedState = "read", Status = RecommendationStatus.New, EvaluationState = RiskEvaluationState.Confirmed };
        var change = new RemediationChange(Guid.NewGuid(), "NarrowWildcardPermission", "azure:demo", "*", "read", false, "Review only.");
        return new RemediationPlan(Guid.NewGuid(), recommendation, CloudProvider.Azure, status, new[] { change }, DateTime.UtcNow, DateTime.UtcNow, "Non-executable.");
    }
}
