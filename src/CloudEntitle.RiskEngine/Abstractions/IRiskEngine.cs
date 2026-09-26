using CloudEntitle.RiskEngine.Models;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;

namespace CloudEntitle.RiskEngine.Abstractions;

public interface IRiskRule
{
    string RuleId { get; }
    string Name { get; }
    RiskSeverity DefaultSeverity { get; }
    RiskCategory Category { get; }
    Task<IReadOnlyCollection<RiskFindingModel>> EvaluateAsync(RiskRuleContext context, CancellationToken cancellationToken = default);
}

public interface IRiskRuleRegistry
{
    IReadOnlyList<IRiskRule> ActiveRules(RiskRuleOptions options);
    IRiskRule? Get(string ruleId);
}

public interface IRiskEngine
{
    Task<RiskEvaluationResult> EvaluateAsync(RiskEvaluationRequest request, CancellationToken cancellationToken = default);
}
