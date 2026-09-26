using CloudEntitle.EffectiveAccess.Models;
using CloudEntitle.Domain;
using CloudEntitle.Normalization.Models;

namespace CloudEntitle.EffectiveAccess.Abstractions;

public interface IEffectiveAccessEngine
{
    Task<AccessEvaluationResult> EvaluateAsync(AccessEvaluationRequest request, CancellationToken cancellationToken = default);
    Task<AccessDecisionResult> ExplainAsync(AccessEvaluationRequest request, Guid principalId, Guid resourceId, Guid permissionId, CancellationToken cancellationToken = default);
}

public interface IAccessEvaluator
{
    CloudProvider Provider { get; }
    AccessDecisionResult Evaluate(AccessEvaluationContext context, AccessEvaluationRequest request);
}

public interface IScopeResolver
{
    bool Applies(NormalizedEntitlement entitlement, CloudEntitle.Domain.CloudResource resource);
}

public interface IConditionEvaluator
{
    bool CanEvaluate(NormalizedCondition condition, IReadOnlyDictionary<string, string> runtimeContext);
}
