using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Normalization.Models;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;
using DomainAccessPath = CloudEntitle.Domain.Entities.AccessPath;

namespace CloudEntitle.RiskEngine.Models;

public sealed record RiskEvaluationRequest(
    IReadOnlyList<CloudAccount> Accounts,
    IReadOnlyList<CloudPrincipal> Principals,
    IReadOnlyList<CloudResource> Resources,
    IReadOnlyList<CloudRole> Roles,
    IReadOnlyList<CloudPolicy> Policies,
    IReadOnlyList<CloudPermission> Permissions,
    IReadOnlyList<NormalizedEntitlement> Entitlements,
    IReadOnlyList<DomainEffectiveAccess> EffectiveAccess,
    IReadOnlyList<DomainAccessPath> AccessPaths,
    IReadOnlyList<ActivityRecord> ActivityRecords,
    Guid? ScanId = null,
    DateTime? EvaluatedAt = null,
    RiskRuleOptions? Options = null);

public sealed record RiskRuleOptions(int DormancyDays = 90, int MaxPathDepth = 5, bool RequireProductionTag = true, IReadOnlySet<string>? EnabledRules = null)
{
    public bool IsEnabled(string ruleId) => EnabledRules == null || EnabledRules.Contains(ruleId);
}

public sealed record RiskRuleContext(RiskEvaluationRequest Request, DateTime EvaluatedAt, RiskRuleOptions Options);
public sealed record RiskDiagnostic(string Code, string Message, string? RuleId = null, string? SourceId = null, bool IsBlocking = false);
public sealed record RiskStatistics(int RulesEvaluated, int RulesFailed, int FindingsGenerated, int Confirmed, int Potential, int Unknown, int Unsupported);
public sealed record RiskEvaluationResult(IReadOnlyList<RiskFindingModel> Findings, IReadOnlyList<RiskDiagnostic> Diagnostics, RiskStatistics Statistics);
