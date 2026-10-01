using CloudEntitle.Domain;

namespace CloudEntitle.RiskEngine.Models;

public sealed record CustomRiskRuleDefinition(
    string RuleId,
    string Name,
    string Description,
    RiskSeverity Severity,
    RiskCategory Category,
    bool Enabled = true,
    CloudProvider? Provider = null,
    string? PermissionName = null,
    string? ActionContains = null,
    string? ResourceContains = null,
    string? ScopeContains = null,
    bool? RequiresPrivilegedAccess = null,
    bool? RequiresExternalIdentity = null,
    bool? RequiresServiceIdentity = null,
    RiskEvaluationState EvaluationState = RiskEvaluationState.Potential,
    string? Recommendation = null);
