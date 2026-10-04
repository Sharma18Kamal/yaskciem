using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.RemediationPlanning.Abstractions;
using CloudEntitle.RemediationPlanning.Models;

namespace CloudEntitle.RemediationPlanning.Services;

public sealed class RemediationPlanner : IRemediationPlanner
{
    public RemediationPlan CreatePlan(RecommendationModel recommendation, RemediationContext context)
    {
        var evidenceValid = recommendation.RiskFindingId != Guid.Empty
            && recommendation.EffectiveAccessId.HasValue
            && context.EffectiveAccess.Any(item => item.Id == recommendation.EffectiveAccessId)
            && (!recommendation.AccessPathId.HasValue || context.AccessPaths.Any(item => item.Id == recommendation.AccessPathId));
        var status = evidenceValid && recommendation.EvaluationState == RiskEvaluationState.Confirmed
            ? RemediationPlanStatus.RequiresReview
            : RemediationPlanStatus.Draft;
        var change = new RemediationChange(
            Guid.NewGuid(),
            recommendation.RecommendationType.ToString(),
            $"{recommendation.Provider}:{recommendation.CloudAccountId}:{recommendation.PrincipalId}",
            recommendation.CurrentState,
            recommendation.SuggestedState,
            false,
            "Review-only proposal. No provider mutation or executable remediation is generated.")
        {
            RollbackState = recommendation.CurrentState
        };
        var rollback = new RemediationChange(Guid.NewGuid(), $"Rollback{recommendation.RecommendationType}", change.Target, change.ProposedState, change.BeforeState, false,
            "Non-executable rollback draft. Revalidate the original state and review before any provider action.")
        {
            RollbackState = change.ProposedState
        };
        var createdAt = DateTime.UtcNow;
        return new RemediationPlan(Guid.NewGuid(), recommendation, recommendation.Provider, status, new[] { change }, createdAt, createdAt, "This plan is non-executable and requires human review.")
        {
            RollbackChanges = new[] { rollback },
            AuditTrail = new[] { new RemediationAuditEntry(createdAt, "System", "PlanCreated", "Non-executable remediation and rollback drafts created from recommendation evidence.") }
        };
    }
}

public sealed class RemediationSimulator : IRemediationSimulator
{
    public RemediationSimulation Simulate(RemediationPlan plan, RemediationContext context)
    {
        var diagnostics = new List<string>();
        var affected = context.EffectiveAccess.Where(item => item.Id == plan.Recommendation.EffectiveAccessId).Select(item => item.Id).ToArray();
        var retained = Array.Empty<Guid>();
        var potentiallyLost = affected;
        var status = SimulationStatus.RequiresReview;
        var impact = AccessImpactStatus.Unknown;
        if (affected.Length == 0)
        {
            status = SimulationStatus.Invalid;
            diagnostics.Add("The recommendation has no matching EffectiveAccess record.");
        }
        else if (plan.Recommendation.EvaluationState == RiskEvaluationState.Unknown || plan.Recommendation.EvaluationState == RiskEvaluationState.Unsupported)
        {
            diagnostics.Add("Recommendation evidence is incomplete; access impact cannot be safely simulated.");
        }
        else
        {
            diagnostics.Add("Simulation is structural only. Provider authorization was not re-evaluated.");
            diagnostics.Add("Human validation is required before any approval.");
        }
        return new RemediationSimulation(plan.Id, status, impact, affected, retained, potentiallyLost, diagnostics, DateTime.UtcNow);
    }
}
