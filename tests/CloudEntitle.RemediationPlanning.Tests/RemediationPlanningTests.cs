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
