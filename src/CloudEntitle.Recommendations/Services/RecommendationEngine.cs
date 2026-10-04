using CloudEntitle.Domain.Entities;
using CloudEntitle.Domain;
using CloudEntitle.Recommendations.Abstractions;
using CloudEntitle.Recommendations.Models;
using CloudEntitle.Recommendations.Rules;
using Serilog;

namespace CloudEntitle.Recommendations.Services;

public sealed class RecommendationRuleRegistry : IRecommendationRuleRegistry
{
    private readonly IReadOnlyList<IRecommendationRule> _rules = new IRecommendationRule[]
    {
        new RemoveUnusedPermissionRule(), new RemoveUnusedRoleRule(), new GenerateLeastPrivilegePolicyRule(), new NarrowWildcardPermissionRule(), new NarrowResourceScopeRule(), new ReplaceBroadRoleRule(), new RemoveRedundantAccessPathRule(), new RemoveDormantPrivilegeRule(), new ReduceServiceAccountPermissionRule(), new ReducePrivilegedGroupAccessRule(), new ReduceNestedPrivilegeRule(), new ReduceLongPrivilegeChainRule(), new ConsolidateAccessRule()
    };
    public IReadOnlyList<IRecommendationRule> ActiveRules() => _rules;
}

public sealed class RecommendationEngine : IRecommendationEngine
{
    private readonly IRecommendationRuleRegistry _registry;
    public RecommendationEngine(IRecommendationRuleRegistry? registry = null) => _registry = registry ?? new RecommendationRuleRegistry();

    public async Task<RecommendationResult> EvaluateAsync(RecommendationContext context, CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<RecommendationDiagnostic>();
        var byFingerprint = new Dictionary<string, RecommendationModel>(StringComparer.Ordinal);
        foreach (var rule in _registry.ActiveRules())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var recommendation in await rule.EvaluateAsync(context, cancellationToken))
                {
                    var validation = Validate(recommendation, context);
                    if (validation.Count > 0) { diagnostics.AddRange(validation); continue; }
                    if (!byFingerprint.ContainsKey(recommendation.Fingerprint)) byFingerprint[recommendation.Fingerprint] = recommendation;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { diagnostics.Add(new("REC-010", exception.Message, rule.RuleId)); Log.Error(exception, "Recommendation rule {RuleId} failed", rule.RuleId); }
        }
        var recommendations = byFingerprint.Values.OrderBy(item => item.Priority).ThenBy(item => item.Fingerprint, StringComparer.Ordinal).ToArray();
        return new RecommendationResult(recommendations, diagnostics, new(recommendations.Length, recommendations.Count(item => item.EvaluationState == Domain.RiskEvaluationState.Confirmed), recommendations.Count(item => item.EvaluationState == Domain.RiskEvaluationState.Potential), recommendations.Count(item => item.EvaluationState == Domain.RiskEvaluationState.Unknown), recommendations.Count(item => item.EvaluationState == Domain.RiskEvaluationState.Unsupported), diagnostics.Count(item => item.Code == "REC-011")));
    }

    private static IReadOnlyList<RecommendationDiagnostic> Validate(RecommendationModel recommendation, RecommendationContext context)
    {
        var diagnostics = new List<RecommendationDiagnostic>();
        var finding = context.Findings.FirstOrDefault(item => item.Id == recommendation.RiskFindingId);
        if (finding == null) diagnostics.Add(new("REC-001", "Risk finding evidence is missing.", null, recommendation.RiskFindingId));
        if (!recommendation.EffectiveAccessId.HasValue || !context.EffectiveAccess.Any(item => item.Id == recommendation.EffectiveAccessId)) diagnostics.Add(new("REC-002", "Effective access evidence is missing.", null, recommendation.RiskFindingId));
        if (!recommendation.EntitlementId.HasValue || !context.Entitlements.Any(item => item.Id == recommendation.EntitlementId)) diagnostics.Add(new("REC-004", "Normalized entitlement evidence is missing.", null, recommendation.RiskFindingId));
        if (recommendation.EvaluationState == Domain.RiskEvaluationState.Confirmed && recommendation.AccessPathId.HasValue && !context.AccessPaths.Any(item => item.Id == recommendation.AccessPathId)) diagnostics.Add(new("REC-003", "Confirmed recommendation access path is missing.", null, recommendation.RiskFindingId));
        if (string.IsNullOrWhiteSpace(recommendation.CurrentState) || string.IsNullOrWhiteSpace(recommendation.SuggestedState)) diagnostics.Add(new("REC-012", "Recommendation before/after state is incomplete.", null, recommendation.RiskFindingId));
        return diagnostics;
    }
}

public interface IProviderRecommendationProvider { CloudProvider Provider { get; } ProviderChangeProposal CreateProposal(RecommendationModel recommendation); }
public sealed class AzureRecommendationProvider : IProviderRecommendationProvider { public CloudProvider Provider => CloudProvider.Azure; public ProviderChangeProposal CreateProposal(RecommendationModel r) => new(Provider, r.RecommendationType == RecommendationType.GenerateLeastPrivilegePolicy ? "ReviewCustomRoleDefinition" : r.RecommendationType == RecommendationType.RemoveUnusedRole ? "ReviewRoleAssignmentRemoval" : "ReviewRoleAssignment", r.CurrentState, r.SuggestedState, false, "Review-only Azure proposal; no mutation is executed."); }
public sealed class AwsRecommendationProvider : IProviderRecommendationProvider { public CloudProvider Provider => CloudProvider.Aws; public ProviderChangeProposal CreateProposal(RecommendationModel r) => new(Provider, r.RecommendationType == RecommendationType.GenerateLeastPrivilegePolicy ? "ReviewIamPolicyDocument" : r.RecommendationType == RecommendationType.RemoveUnusedRole ? "ReviewRoleAssignmentRemoval" : "ReviewPolicyOrRole", r.CurrentState, r.SuggestedState, false, "Review-only AWS proposal; no mutation is executed."); }
public sealed class GcpRecommendationProvider : IProviderRecommendationProvider { public CloudProvider Provider => CloudProvider.Gcp; public ProviderChangeProposal CreateProposal(RecommendationModel r) => new(Provider, r.RecommendationType == RecommendationType.GenerateLeastPrivilegePolicy ? "ReviewCustomRole" : r.RecommendationType == RecommendationType.RemoveUnusedRole ? "ReviewIamBindingRemoval" : "ReviewIamBinding", r.CurrentState, r.SuggestedState, false, "Review-only GCP proposal; no mutation is executed."); }
