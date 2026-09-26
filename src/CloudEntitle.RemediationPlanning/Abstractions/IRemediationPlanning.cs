using CloudEntitle.Domain.Entities;
using CloudEntitle.Recommendations.Models;
using CloudEntitle.RemediationPlanning.Models;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;
using DomainAccessPath = CloudEntitle.Domain.Entities.AccessPath;

namespace CloudEntitle.RemediationPlanning.Abstractions;

public sealed record RemediationContext(
    IReadOnlyList<RecommendationModel> Recommendations,
    IReadOnlyList<DomainEffectiveAccess> EffectiveAccess,
    IReadOnlyList<DomainAccessPath> AccessPaths);

public interface IRemediationPlanner
{
    RemediationPlan CreatePlan(RecommendationModel recommendation, RemediationContext context);
}

public interface IRemediationSimulator
{
    RemediationSimulation Simulate(RemediationPlan plan, RemediationContext context);
}
