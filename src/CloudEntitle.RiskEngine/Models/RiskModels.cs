using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.AccessPath.Models;
using CloudEntitle.Normalization.Models;
using System.Text.Json;
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
    IReadOnlyList<ResourceSecurityEvidenceCoverage>? ResourceSecurityCoverage = null,
    IReadOnlyList<ToxicCombinationRule>? ToxicCombinationRules = null,
    IReadOnlyList<ToxicTrustRelationshipEvidence>? ToxicTrustRelationships = null,
    IReadOnlyList<PermissionConditionEvidence>? PermissionConditions = null,
    AttackGraph? AttackGraph = null);

public enum TrustRelationshipStatus { Confirmed, Potential, Unknown, Rejected }
public enum AuthorizationConditionState { Satisfied, NotSatisfied, Unknown }
public enum AuthorizationConditionLogic { And, Or }
public enum AuthorizationConditionOperator
{
    StringEquals,
    StringNotEquals,
    StringEqualsIgnoreCase,
    StringLike,
    StringNotLike,
    Bool,
    NumericEquals,
    NumericGreaterThan,
    NumericLessThan
}

public sealed record ToxicTrustRelationshipEvidence(
    Guid SourcePrincipalId,
    Guid SourceCloudAccountId,
    CloudProvider SourceProvider,
    Guid TargetPrincipalId,
    Guid TargetCloudAccountId,
    CloudProvider TargetProvider,
    string RelationshipType,
    TrustRelationshipStatus Status,
    string EvidenceSource,
    string EvidenceReference,
    DateTime ObservedAtUtc,
    bool RequiresSourcePermission = true,
    string? RequiredSourcePermissionPattern = null,
    AuthorizationConditionState ConditionState = AuthorizationConditionState.Satisfied,
    string? ConditionEvidence = null);

public sealed record AuthorizationConditionPredicate(
    string ContextKey,
    AuthorizationConditionOperator Operator,
    IReadOnlyList<string> ExpectedValues,
    string ProviderOperator);

public sealed record AuthorizationConditionClause(IReadOnlyList<AuthorizationConditionPredicate> Predicates);

public sealed record PermissionConditionEvidence(
    Guid EffectiveAccessId,
    CloudProvider Provider,
    string SourceId,
    string OriginalExpression,
    bool IsSupported,
    IReadOnlyList<AuthorizationConditionClause> AnyOfClauses,
    IReadOnlyDictionary<string, string> Context);

public sealed record AuthorizationConditionAssessment(
    AuthorizationConditionState State,
    string Summary,
    string EvidenceSource);

public static class AuthorizationConditionEvaluator
{
    public static PermissionConditionEvidence FromAwsJson(Guid effectiveAccessId, string sourceId,
        string conditionJson, IReadOnlyDictionary<string, string> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var predicates = new List<AuthorizationConditionPredicate>();
        try
        {
            using var document = JsonDocument.Parse(conditionJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Unsupported(CloudProvider.Aws, effectiveAccessId, sourceId, conditionJson, context);

            foreach (var operatorGroup in document.RootElement.EnumerateObject())
            {
                var (conditionOperator, normalizedOperator) = MapAwsOperator(operatorGroup.Name);
                if (conditionOperator is null || operatorGroup.Value.ValueKind != JsonValueKind.Object)
                    return Unsupported(CloudProvider.Aws, effectiveAccessId, sourceId, conditionJson, context);
                foreach (var keyCondition in operatorGroup.Value.EnumerateObject())
                {
                    var values = ReadValues(keyCondition.Value);
                    if (values is null || values.Count == 0)
                        return Unsupported(CloudProvider.Aws, effectiveAccessId, sourceId, conditionJson, context);
                    predicates.Add(new(keyCondition.Name, conditionOperator.Value, values, normalizedOperator));
                }
            }
        }
        catch (JsonException)
        {
            return Unsupported(CloudProvider.Aws, effectiveAccessId, sourceId, conditionJson, context);
        }

        return predicates.Count == 0
            ? Unsupported(CloudProvider.Aws, effectiveAccessId, sourceId, conditionJson, context)
            : new(effectiveAccessId, CloudProvider.Aws, sourceId, conditionJson, true,
                [new AuthorizationConditionClause(predicates)], context);
    }

    public static AuthorizationConditionAssessment Evaluate(PermissionConditionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!evidence.IsSupported || string.IsNullOrWhiteSpace(evidence.SourceId)
            || string.IsNullOrWhiteSpace(evidence.OriginalExpression) || evidence.AnyOfClauses.Count == 0)
            return Unknown("Condition syntax is unsupported or incomplete.");

        var clauseStates = evidence.AnyOfClauses.Select(clause =>
        {
            if (clause.Predicates.Count == 0) return AuthorizationConditionState.Unknown;
            var states = clause.Predicates.Select(predicate => EvaluatePredicate(predicate, evidence.Context)).ToArray();
            if (states.Contains(AuthorizationConditionState.NotSatisfied)) return AuthorizationConditionState.NotSatisfied;
            return states.All(state => state == AuthorizationConditionState.Satisfied)
                ? AuthorizationConditionState.Satisfied
                : AuthorizationConditionState.Unknown;
        }).ToArray();

        var state = clauseStates.Contains(AuthorizationConditionState.Satisfied)
            ? AuthorizationConditionState.Satisfied
            : clauseStates.All(item => item == AuthorizationConditionState.NotSatisfied)
                ? AuthorizationConditionState.NotSatisfied
                : AuthorizationConditionState.Unknown;
        return new(state,
            $"Normalized {evidence.Provider} condition evaluated as {state}; provider operator evidence retained.",
            $"{evidence.SourceId}; provider={evidence.Provider}; normalized context keys={string.Join(",", evidence.Context.Keys.Order(StringComparer.Ordinal))}");
    }

    private static AuthorizationConditionState EvaluatePredicate(AuthorizationConditionPredicate predicate,
        IReadOnlyDictionary<string, string> context)
    {
        if (string.IsNullOrWhiteSpace(predicate.ContextKey) || predicate.ExpectedValues.Count == 0
            || string.IsNullOrWhiteSpace(predicate.ProviderOperator)
            || !context.TryGetValue(predicate.ContextKey, out var actual))
            return AuthorizationConditionState.Unknown;

        var matches = predicate.Operator switch
        {
            AuthorizationConditionOperator.StringEquals => predicate.ExpectedValues.Contains(actual, StringComparer.Ordinal),
            AuthorizationConditionOperator.StringNotEquals => !predicate.ExpectedValues.Contains(actual, StringComparer.Ordinal),
            AuthorizationConditionOperator.StringEqualsIgnoreCase => predicate.ExpectedValues.Contains(actual, StringComparer.OrdinalIgnoreCase),
            AuthorizationConditionOperator.StringLike => predicate.ExpectedValues.Any(value => GlobMatches(value, actual, StringComparison.Ordinal)),
            AuthorizationConditionOperator.StringNotLike => !predicate.ExpectedValues.Any(value => GlobMatches(value, actual, StringComparison.Ordinal)),
            AuthorizationConditionOperator.Bool => MatchBoolean(actual, predicate.ExpectedValues),
            AuthorizationConditionOperator.NumericEquals => MatchNumeric(actual, predicate.ExpectedValues, comparison => comparison == 0),
            AuthorizationConditionOperator.NumericGreaterThan => MatchNumeric(actual, predicate.ExpectedValues, comparison => comparison > 0),
            AuthorizationConditionOperator.NumericLessThan => MatchNumeric(actual, predicate.ExpectedValues, comparison => comparison < 0),
            _ => null
        };
        return matches is null ? AuthorizationConditionState.Unknown
            : matches.Value ? AuthorizationConditionState.Satisfied : AuthorizationConditionState.NotSatisfied;
    }

    private static bool? MatchBoolean(string actual, IReadOnlyList<string> expectedValues)
    {
        if (!bool.TryParse(actual, out var actualValue)) return null;
        var expected = new List<bool>();
        foreach (var value in expectedValues)
            if (bool.TryParse(value, out var parsed)) expected.Add(parsed);
            else return null;
        return expected.Contains(actualValue);
    }

    private static bool? MatchNumeric(string actual, IReadOnlyList<string> expectedValues, Func<int, bool> compare)
    {
        if (!decimal.TryParse(actual, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var actualValue)) return null;
        var matched = false;
        foreach (var value in expectedValues)
        {
            if (!decimal.TryParse(value, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var expectedValue)) return null;
            matched |= compare(actualValue.CompareTo(expectedValue));
        }
        return matched;
    }

    private static bool GlobMatches(string pattern, string value, StringComparison comparison)
    {
        var patternIndex = 0;
        var valueIndex = 0;
        var starIndex = -1;
        var retryIndex = -1;
        while (valueIndex < value.Length)
        {
            if (patternIndex < pattern.Length && (pattern[patternIndex] == '?'
                || string.Equals(pattern[patternIndex].ToString(), value[valueIndex].ToString(), comparison)))
            {
                patternIndex++;
                valueIndex++;
            }
            else if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starIndex = patternIndex++;
                retryIndex = valueIndex;
            }
            else if (starIndex >= 0)
            {
                patternIndex = starIndex + 1;
                valueIndex = ++retryIndex;
            }
            else return false;
        }
        while (patternIndex < pattern.Length && pattern[patternIndex] == '*') patternIndex++;
        return patternIndex == pattern.Length;
    }

    private static AuthorizationConditionAssessment Unknown(string reason)
        => new(AuthorizationConditionState.Unknown, reason, "No supported provider-normalized condition evidence.");

    private static PermissionConditionEvidence Unsupported(CloudProvider provider, Guid accessId, string sourceId,
        string expression, IReadOnlyDictionary<string, string> context)
        => new(accessId, provider, sourceId, expression, false, Array.Empty<AuthorizationConditionClause>(), context);

    private static (AuthorizationConditionOperator? Operator, string NormalizedName) MapAwsOperator(string name)
    {
        var normalized = name.StartsWith("ForAnyValue:", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("ForAllValues:", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : name.EndsWith("IfExists", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : name;
        var mapped = normalized.ToLowerInvariant() switch
        {
            "stringequals" or "arnequals" => AuthorizationConditionOperator.StringEquals,
            "stringnotequals" or "arnnotequals" => AuthorizationConditionOperator.StringNotEquals,
            "stringequalsignorecase" => AuthorizationConditionOperator.StringEqualsIgnoreCase,
            "stringlike" or "arnlike" => AuthorizationConditionOperator.StringLike,
            "stringnotlike" or "arnnotlike" => AuthorizationConditionOperator.StringNotLike,
            "bool" => AuthorizationConditionOperator.Bool,
            "numericequals" => AuthorizationConditionOperator.NumericEquals,
            "numericgreaterthan" => AuthorizationConditionOperator.NumericGreaterThan,
            "numericlessthan" => AuthorizationConditionOperator.NumericLessThan,
            _ => (AuthorizationConditionOperator?)null
        };
        return (mapped, normalized);
    }

    private static IReadOnlyList<string>? ReadValues(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String) return [element.GetString() ?? string.Empty];
        if (element.ValueKind != JsonValueKind.Array) return null;
        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) return null;
            values.Add(item.GetString() ?? string.Empty);
        }
        return values;
    }
}

public static class ToxicTrustRelationshipMapper
{
    public static IReadOnlyList<ToxicTrustRelationshipEvidence> FromFederationEvidence(
        IReadOnlyList<CloudPrincipal> principals)
    {
        ArgumentNullException.ThrowIfNull(principals);
        var result = new List<ToxicTrustRelationshipEvidence>();
        foreach (var target in principals.Where(item => item.FederationTrusts.Count > 0))
        foreach (var trust in target.FederationTrusts)
        {
            if (ToProvider(target.CloudProvider) == CloudProvider.Aws
                && trust.Issuer.StartsWith("aws-principal:", StringComparison.Ordinal))
            {
                var trustedPrincipal = trust.Issuer["aws-principal:".Length..];
                foreach (var source in principals.Where(source => source.Id != target.Id
                    && source.CloudProvider == CloudProviderType.Aws
                    && AwsPrincipalMatches(trustedPrincipal, source.PrincipalId)))
                {
                    var hasCondition = string.Equals(trust.Subject, "condition-present", StringComparison.Ordinal);
                    var trustIsInventoried = target.FederationInventoryComplete == true;
                    result.Add(new ToxicTrustRelationshipEvidence(
                        source.Id, source.AccountId, CloudProvider.Aws,
                        target.Id, target.AccountId, CloudProvider.Aws,
                        "AWS AssumeRole", trustIsInventoried ? TrustRelationshipStatus.Confirmed : TrustRelationshipStatus.Potential,
                        trust.Source, $"{target.PrincipalId ?? target.Name}|{trustedPrincipal}",
                        new[] { source.UpdatedAt ?? source.CreatedAt, target.UpdatedAt ?? target.CreatedAt }.Max(),
                        RequiresSourcePermission: true,
                        RequiredSourcePermissionPattern: "sts:AssumeRole",
                        ConditionState: hasCondition ? AuthorizationConditionState.Unknown
                            : trustIsInventoried ? AuthorizationConditionState.Satisfied : AuthorizationConditionState.Unknown,
                        ConditionEvidence: hasCondition
                            ? "AWS trust policy has a condition; applicable request context was not evaluated."
                            : trustIsInventoried ? "AWS AssumeRole trust policy was inventoried; source permission is evaluated separately."
                            : "AWS trust policy coverage is incomplete."));
                }
                continue;
            }

            var issuer = NormalizeIssuer(trust.Issuer);
            var matchingSources = principals.Where(source => source.Id != target.Id
                && !string.IsNullOrWhiteSpace(source.FederationIssuer)
                && string.Equals(NormalizeIssuer(source.FederationIssuer), issuer, StringComparison.OrdinalIgnoreCase)
                && string.Equals(source.FederationSubject, trust.Subject, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(source.FederationAudience)
                && source.FederationAudience.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Any(audience => trust.Audiences.Contains(audience, StringComparer.Ordinal))).ToArray();
            foreach (var source in matchingSources)
            {
                var verified = (target.FederationTrustVerified == true || source.FederationTrustVerified == true)
                    && target.FederationInventoryComplete == true && source.FederationInventoryComplete == true;
                result.Add(new ToxicTrustRelationshipEvidence(
                    source.Id, source.AccountId, ToProvider(source.CloudProvider),
                    target.Id, target.AccountId, ToProvider(target.CloudProvider),
                    "Federated workload identity", verified ? TrustRelationshipStatus.Confirmed : TrustRelationshipStatus.Potential,
                    string.IsNullOrWhiteSpace(trust.Source) ? "Federation inventory" : trust.Source,
                    $"{NormalizeIssuer(trust.Issuer)}|{trust.Subject}|{string.Join(",", trust.Audiences.Order(StringComparer.Ordinal))}",
                    new[] { source.UpdatedAt ?? source.CreatedAt, target.UpdatedAt ?? target.CreatedAt }.Max(),
                    RequiresSourcePermission: false,
                    ConditionState: verified ? AuthorizationConditionState.Satisfied : AuthorizationConditionState.Unknown,
                    ConditionEvidence: verified ? "Provider trust inventory verified." : "Trust was inventoried but not fully verified."));
            }
        }
        return result;
    }

    private static string NormalizeIssuer(string? issuer)
        => issuer?.Trim().TrimEnd('/') ?? string.Empty;

    private static bool AwsPrincipalMatches(string trustedPrincipal, string? sourcePrincipal)
    {
        if (string.IsNullOrWhiteSpace(sourcePrincipal)) return false;
        if (string.Equals(trustedPrincipal, sourcePrincipal, StringComparison.Ordinal)) return true;
        const string rootSuffix = ":root";
        if (!trustedPrincipal.StartsWith("arn:aws:iam::", StringComparison.Ordinal)
            || !trustedPrincipal.EndsWith(rootSuffix, StringComparison.Ordinal))
            return false;
        var trustedAccount = trustedPrincipal["arn:aws:iam::".Length..^rootSuffix.Length];
        var sourceAccountStart = sourcePrincipal.StartsWith("arn:aws:iam::", StringComparison.Ordinal)
            ? "arn:aws:iam::".Length : -1;
        if (sourceAccountStart < 0) return false;
        var separator = sourcePrincipal.IndexOf(':', sourceAccountStart);
        return separator > sourceAccountStart
            && string.Equals(sourcePrincipal[sourceAccountStart..separator], trustedAccount, StringComparison.Ordinal);
    }

    private static CloudProvider ToProvider(CloudProviderType provider) => provider switch
    {
        CloudProviderType.Azure => CloudProvider.Azure,
        CloudProviderType.Aws => CloudProvider.Aws,
        CloudProviderType.Gcp => CloudProvider.Gcp,
        _ => CloudProvider.Kubernetes
    };
}

public sealed record RiskRuleOptions(int DormancyDays = 90, int MaxPathDepth = 5, bool RequireProductionTag = true, IReadOnlySet<string>? EnabledRules = null, int UsageObservationDays = 30, int EvidenceFreshnessWindowDays = 30, int MaxCredentialAgeDays = 90)
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
