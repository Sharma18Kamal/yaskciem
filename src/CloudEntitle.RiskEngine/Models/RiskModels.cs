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
    RiskRuleOptions? Options = null,
    IReadOnlyList<ActivityEvidenceCoverage>? ActivityCoverage = null,
    IReadOnlyList<ResourceSecuritySignal>? ResourceSecuritySignals = null,
    IReadOnlyList<ResourceSecurityEvidenceCoverage>? ResourceSecurityCoverage = null);

public sealed record RiskRuleOptions(int DormancyDays = 90, int MaxPathDepth = 5, bool RequireProductionTag = true, IReadOnlySet<string>? EnabledRules = null, int UsageObservationDays = 30, int EvidenceFreshnessWindowDays = 30)
{
    public bool IsEnabled(string ruleId) => EnabledRules == null || EnabledRules.Contains(ruleId);
}

public enum ActivityCoverageStatus { Complete, Partial, Unavailable, Unsupported, Unknown }

public sealed record ActivityEvidenceCoverage(
    Guid CloudAccountId,
    CloudProvider Provider,
    DateTime ObservedFromUtc,
    DateTime ObservedThroughUtc,
    ActivityCoverageStatus Status,
    bool IncludesActions,
    bool IncludesResourceIds,
    bool IncludesRoleIds,
    int RecordCount,
    string? Details = null);

public static class ActivityUsageEvaluator
{
    public static bool Covers(ActivityEvidenceCoverage? coverage, DateTime windowStartUtc, DateTime windowEndUtc, bool requiresActions = false, bool requiresResourceIds = false, bool requiresRoleIds = false)
        => coverage is not null
            && coverage.Status == ActivityCoverageStatus.Complete
            && coverage.ObservedFromUtc.ToUniversalTime() <= windowStartUtc.ToUniversalTime()
            && coverage.ObservedThroughUtc.ToUniversalTime() >= windowEndUtc.ToUniversalTime()
            && (!requiresActions || coverage.IncludesActions)
            && (!requiresResourceIds || coverage.IncludesResourceIds)
            && (!requiresRoleIds || coverage.IncludesRoleIds);

    public static bool ActionMatches(CloudProvider provider, string pattern, string action)
    {
        var comparison = provider == CloudProvider.Gcp ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var patternIndex = 0;
        var actionIndex = 0;
        var starIndex = -1;
        var retryActionIndex = -1;
        while (actionIndex < action.Length)
        {
            if (patternIndex < pattern.Length && (pattern[patternIndex] == '?' || string.Equals(pattern[patternIndex].ToString(), action[actionIndex].ToString(), comparison)))
            {
                patternIndex++;
                actionIndex++;
            }
            else if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starIndex = patternIndex++;
                retryActionIndex = actionIndex;
            }
            else if (starIndex >= 0)
            {
                patternIndex = starIndex + 1;
                actionIndex = ++retryActionIndex;
            }
            else return false;
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*') patternIndex++;
        return patternIndex == pattern.Length;
    }
}

public sealed record RiskRuleContext(RiskEvaluationRequest Request, DateTime EvaluatedAt, RiskRuleOptions Options);
public sealed record RiskDiagnostic(string Code, string Message, string? RuleId = null, string? SourceId = null, bool IsBlocking = false);
public sealed record RiskStatistics(int RulesEvaluated, int RulesFailed, int FindingsGenerated, int Confirmed, int Potential, int Unknown, int Unsupported);
public sealed record RiskEvaluationResult(IReadOnlyList<RiskFindingModel> Findings, IReadOnlyList<RiskDiagnostic> Diagnostics, RiskStatistics Statistics);
