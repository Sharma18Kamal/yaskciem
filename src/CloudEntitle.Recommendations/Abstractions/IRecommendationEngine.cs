using CloudEntitle.Recommendations.Models;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;

namespace CloudEntitle.Recommendations.Abstractions;

public interface IRecommendationRule
{
    string RuleId { get; }
    RecommendationType RecommendationType { get; }
    Task<IReadOnlyCollection<RecommendationModel>> EvaluateAsync(RecommendationContext context, CancellationToken cancellationToken = default);
}

public interface IRecommendationRuleRegistry
{
    IReadOnlyList<IRecommendationRule> ActiveRules();
}

public interface IRecommendationEngine
{
    Task<RecommendationResult> EvaluateAsync(RecommendationContext context, CancellationToken cancellationToken = default);
}

public interface IRecommendationValidator
{
    IReadOnlyList<RecommendationDiagnostic> Validate(RecommendationModel recommendation, RecommendationContext context);
}
