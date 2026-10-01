using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.RiskEngine.Abstractions;
using CloudEntitle.RiskEngine.Models;
using CloudEntitle.RiskEngine.Rules;
using Serilog;

namespace CloudEntitle.RiskEngine.Services;

public sealed class RiskRuleRegistry : IRiskRuleRegistry
{
    private readonly IReadOnlyDictionary<string, IRiskRule> _rules;

    public RiskRuleRegistry(IEnumerable<IRiskRule>? rules = null, IEnumerable<CustomRiskRuleDefinition>? customRules = null)
    {
        var builtInRules = (rules ?? new IRiskRule[]
        {
            new PrivilegedIdentityRule(), new ExcessivePermissionRule(), new WildcardPermissionRule(), new BroadScopeRule(), new DormantPrivilegedIdentityRule(), new ExternalPrivilegedIdentityRule(), new PrivilegedServiceAccountRule(), new UnusedEntitlementRule(), new ProductionAdministrativeAccessRule(), new ExcessiveServicePermissionRule(), new RedundantAccessPathRule(), new PrivilegedGroupMembershipRule(), new NestedPrivilegedAccessRule(), new BroadResourceWildcardRule(), new LongPrivilegeChainRule()
        });
        var loadedCustomRules = (customRules ?? Array.Empty<CustomRiskRuleDefinition>()).Where(rule => rule.Enabled).Select(rule => (IRiskRule)new CustomRiskRule(rule));
        _rules = builtInRules.Concat(loadedCustomRules).GroupBy(rule => rule.RuleId, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<IRiskRule> ActiveRules(RiskRuleOptions options) => _rules.Values.Where(rule => options.IsEnabled(rule.RuleId)).OrderBy(rule => rule.RuleId).ToArray();
    public IRiskRule? Get(string ruleId) => _rules.TryGetValue(ruleId, out var rule) ? rule : null;
}

public sealed class RiskEngine : IRiskEngine
{
    private readonly IRiskRuleRegistry _registry;

    public RiskEngine(IRiskRuleRegistry? registry = null) => _registry = registry ?? new RiskRuleRegistry();

    public async Task<RiskEvaluationResult> EvaluateAsync(RiskEvaluationRequest request, CancellationToken cancellationToken = default)
    {
        var evaluatedAt = request.EvaluatedAt ?? DateTime.UtcNow;
        var options = request.Options ?? new RiskRuleOptions();
        var context = new RiskRuleContext(request, evaluatedAt, options);
        var findings = new Dictionary<string, RiskFindingModel>(StringComparer.Ordinal);
        var diagnostics = new List<RiskDiagnostic>();
        var evaluated = 0;
        var failed = 0;
        foreach (var rule in _registry.ActiveRules(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            evaluated++;
            try
            {
                foreach (var finding in await rule.EvaluateAsync(context, cancellationToken))
                {
                    if (!findings.ContainsKey(finding.Fingerprint)) findings[finding.Fingerprint] = finding;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                failed++;
                diagnostics.Add(new("RISK-007", exception.Message, rule.RuleId));
                Log.Error(exception, "Risk rule {RuleId} failed", rule.RuleId);
            }
        }

        var result = findings.Values.OrderBy(finding => finding.Fingerprint, StringComparer.Ordinal).ToArray();
        return new RiskEvaluationResult(result, diagnostics, new(evaluated, failed, result.Length, result.Count(finding => finding.EvaluationState == RiskEvaluationState.Confirmed), result.Count(finding => finding.EvaluationState == RiskEvaluationState.Potential), result.Count(finding => finding.EvaluationState == RiskEvaluationState.Unknown), result.Count(finding => finding.EvaluationState == RiskEvaluationState.Unsupported)));
    }
}
