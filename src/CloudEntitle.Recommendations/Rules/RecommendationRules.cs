using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Normalization.Models;
using DomainAccessPath = CloudEntitle.Domain.Entities.AccessPath;
using CloudEntitle.Recommendations.Abstractions;
using CloudEntitle.Recommendations.Models;
using CloudEntitle.RiskEngine.Models;

namespace CloudEntitle.Recommendations.Rules;

public abstract class RecommendationRuleBase : IRecommendationRule
{
    public abstract string RuleId { get; }
    public abstract RecommendationType RecommendationType { get; }
    public abstract Task<IReadOnlyCollection<RecommendationModel>> EvaluateAsync(RecommendationContext context, CancellationToken cancellationToken = default);

    protected static IEnumerable<RiskFindingModel> Findings(RecommendationContext context, string ruleId) => context.Findings.Where(finding => finding.RuleId == Stable(ruleId));
    protected static NormalizedEntitlement? Entitlement(RecommendationContext context, RiskFindingModel finding) => context.Entitlements.FirstOrDefault(item => item.PrincipalId == finding.PrincipalId && item.PermissionId == finding.PermissionId);
    protected static string Name(RecommendationContext context, Guid? id) => context.Principals.FirstOrDefault(item => item.Id == id)?.Name ?? "unknown";
    protected static string Resource(RecommendationContext context, Guid? id) => context.Resources.FirstOrDefault(item => item.Id == id)?.Name ?? "unknown";
    protected static string Permission(RecommendationContext context, Guid? id) => context.Permissions.FirstOrDefault(item => item.Id == id)?.Name ?? "unknown";
    protected static DomainAccessPath? Path(RecommendationContext context, RiskFindingModel finding) => context.AccessPaths.FirstOrDefault(path => path.Id == finding.AccessPathId || path.EffectiveAccessId == finding.EffectiveAccessId);
    protected RecommendationModel Create(RecommendationContext context, RiskFindingModel finding, string title, string description, string current, string recommended, string reason, RecommendationImpactState impact, Guid? pathId = null, string? originalProviderObject = null, NormalizedEntitlement? sourceEntitlement = null)
    {
        var entitlement = sourceEntitlement ?? Entitlement(context, finding);
        var path = pathId.HasValue ? context.AccessPaths.FirstOrDefault(item => item.Id == pathId) : Path(context, finding);
        var state = finding.EvaluationState == RiskEvaluationState.Confirmed && entitlement != null && finding.EffectiveAccessId.HasValue ? RiskEvaluationState.Confirmed : finding.EvaluationState == RiskEvaluationState.Unknown ? RiskEvaluationState.Unknown : RiskEvaluationState.Potential;
        var coverage = context.ActivityCoverage?.FirstOrDefault(item => item.CloudAccountId == finding.CloudAccountId && item.Provider == finding.CloudProvider);
        var activityEvidence = coverage is null
            ? "Unavailable: no provider/account activity coverage record."
            : $"{coverage.Status}; Window={coverage.ObservedFromUtc:O}..{coverage.ObservedThroughUtc:O}; Records={coverage.RecordCount}; {coverage.Details ?? "No additional details."}";
        var evidence = new RecommendationEvidence(finding.Id, finding.EffectiveAccessId, path?.Id, entitlement?.Id, finding.CloudProvider, "RiskFinding", finding.Fingerprint, finding.Title, Name(context, finding.PrincipalId), Resource(context, finding.ResourceId), Permission(context, finding.PermissionId), entitlement?.Scope.OriginalScope ?? "unknown", entitlement?.AssignmentType.ToString() ?? "unknown", finding.Evidence.Summary, activityEvidence, $"EffectiveAccess={finding.EffectiveAccessId}; AccessPath={path?.Id}; Entitlement={entitlement?.Id}", originalProviderObject ?? finding.Evidence.Summary);
        var fingerprint = string.Join('|', finding.CloudProvider, finding.CloudAccountId, finding.PrincipalId, finding.ResourceId, finding.EffectiveAccessId, path?.Id, finding.Id, RecommendationType);
        return new RecommendationModel
        {
            Id = Stable(fingerprint), RiskFindingId = finding.Id, RecommendationType = RecommendationType, Provider = finding.CloudProvider, CloudAccountId = finding.CloudAccountId, PrincipalId = finding.PrincipalId, ResourceId = finding.ResourceId, EffectiveAccessId = finding.EffectiveAccessId, AccessPathId = path?.Id, EntitlementId = entitlement?.Id, Title = title, Description = description, CurrentState = current, SuggestedState = recommended, Reason = reason, Changes = $"Current: {current}; Recommended: {recommended}", Impact = $"Impact={impact}", Evidence = System.Text.Json.JsonSerializer.Serialize(evidence), EvaluationState = state, ImpactState = impact, Priority = state == RiskEvaluationState.Confirmed ? 1 : state == RiskEvaluationState.Potential ? 2 : 3, Status = RecommendationStatus.New, Fingerprint = fingerprint, CreatedAt = context.EvaluatedAt, UpdatedAt = context.EvaluatedAt
        };
    }

    protected static Guid Stable(string value) => GuidUtility.Create(GuidUtility.Namespace, value);
}

public sealed class GenerateLeastPrivilegePolicyRule : RecommendationRuleBase
{
    private sealed record ObservedOperation(string Action, string Resource);
    private sealed record ProviderCondition(string? Title, string Expression, string EvaluationStatus, string? Version);

    public override string RuleId => "CIEM-003";
    public override RecommendationType RecommendationType => RecommendationType.GenerateLeastPrivilegePolicy;

    public override Task<IReadOnlyCollection<RecommendationModel>> EvaluateAsync(RecommendationContext context, CancellationToken cancellationToken = default)
    {
        var recommendations = new List<RecommendationModel>();
        var windowEnd = context.EvaluatedAt.ToUniversalTime();
        var windowStart = windowEnd.AddDays(-Math.Max(1, context.ActivityObservationDays));
        foreach (var finding in Findings(context, RuleId).Where(item => item.EvaluationState == RiskEvaluationState.Confirmed))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var coverage = context.ActivityCoverage?.FirstOrDefault(item => item.CloudAccountId == finding.CloudAccountId && item.Provider == finding.CloudProvider);
            var entitlement = FindPolicyEntitlement(context, finding);
            var permission = context.Permissions.FirstOrDefault(item => item.Id == finding.PermissionId);
            if (entitlement is null || string.IsNullOrWhiteSpace(permission?.Action)) continue;
            if (!ActivityUsageEvaluator.Covers(coverage, windowStart, windowEnd, requiresActions: true, requiresResourceIds: true, requiresRoleIds: entitlement.RoleId.HasValue)) continue;

            var observed = context.ActivityRecords
                .Where(activity => activity.CloudAccountId == finding.CloudAccountId
                    && activity.PrincipalId == finding.PrincipalId
                    && activity.Success
                    && activity.Timestamp.ToUniversalTime() >= windowStart
                    && activity.Timestamp.ToUniversalTime() <= windowEnd
                    && activity.ResourceId.HasValue
                    && (!finding.ResourceId.HasValue || activity.ResourceId == finding.ResourceId)
                    && (!entitlement.RoleId.HasValue || activity.RoleId == entitlement.RoleId)
                    && ActivityUsageEvaluator.ActionMatches(finding.CloudProvider, permission.Action, activity.Action)
                    && !activity.Action.Contains('*')
                    && !activity.Action.Contains('?'))
                .Select(activity => new ObservedOperation(
                    activity.Action,
                    context.Resources.FirstOrDefault(resource => resource.Id == activity.ResourceId)?.ResourceId ?? string.Empty))
                .Where(item => !string.IsNullOrWhiteSpace(item.Resource))
                .DistinctBy(item => (item.Action, item.Resource))
                .OrderBy(item => item.Action, StringComparer.Ordinal)
                .ThenBy(item => item.Resource, StringComparer.Ordinal)
                .ToArray();
            if (observed.Length == 0) continue;

            var normalizedPermission = context.NormalizedPermissions.FirstOrDefault(item => item.Id == finding.PermissionId);
            var condition = GetProviderCondition(finding.CloudProvider, entitlement);
            var proposedPolicy = BuildPolicy(finding.CloudProvider, entitlement, normalizedPermission, condition, observed, windowStart, windowEnd);
            if (proposedPolicy is null) continue;
            var description = $"Generated from {observed.Length} successful action/resource observation(s) over {context.ActivityObservationDays} days. Source conditions and provider semantics are retained where supported. Possible access loss is unknown because the proposal was not validated by the provider.";
            var originalProviderObject = System.Text.Json.JsonSerializer.Serialize(new
            {
                entitlement.Evidence.OriginalDocument,
                entitlement.Evidence.OriginalStatement,
                entitlement.Evidence.ProviderSpecificData,
                condition
            });
            recommendations.Add(Create(context, finding, "Generate least-privilege policy proposal", description, permission.Name, proposedPolicy,
                $"Observed operations only; activity coverage was complete from {windowStart:O} through {windowEnd:O}. Provider-side impact validation was not available; potential access loss is unknown.",
                RecommendationImpactState.UnknownImpact, originalProviderObject: originalProviderObject, sourceEntitlement: entitlement));
        }

        return Task.FromResult<IReadOnlyCollection<RecommendationModel>>(recommendations);
    }

    private static string? BuildPolicy(CloudProvider provider, NormalizedEntitlement entitlement, NormalizedPermission? normalizedPermission, ProviderCondition? condition,
        IReadOnlyList<ObservedOperation> observed, DateTime windowStart, DateTime windowEnd)
    {
        var actions = observed.Select(item => item.Action).Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToArray();
        var scope = entitlement.Scope.OriginalScope;
        var conditionExpression = condition?.Expression;
        if (condition is not null && string.IsNullOrWhiteSpace(conditionExpression)) return null;
        var azureIsDataAction = provider == CloudProvider.Azure ? ClassifyAzureDataAction(normalizedPermission, actions) : null;
        if (provider == CloudProvider.Azure && !azureIsDataAction.HasValue) return null;
        object? providerDocument = provider switch
        {
            CloudProvider.Azure => new
            {
                RoleDefinition = new
                {
                    Name = "YASK-CIEM least-privilege proposal",
                    IsCustom = true,
                    Description = $"Derived from successful observed activity ({windowStart:O} to {windowEnd:O}); review before use.",
                    Actions = azureIsDataAction == true ? Array.Empty<string>() : actions,
                    NotActions = Array.Empty<string>(),
                    DataActions = azureIsDataAction == true ? actions : Array.Empty<string>(),
                    NotDataActions = Array.Empty<string>(),
                    AssignableScopes = new[] { scope }
                },
                RoleAssignment = new
                {
                    Scope = scope,
                    Condition = condition is null ? null : new { condition.Title, Expression = conditionExpression, condition.EvaluationStatus, condition.Version }
                }
            },
            CloudProvider.Aws => BuildAwsPolicy(observed, conditionExpression),
            CloudProvider.Gcp => new
            {
                CustomRole = new
                {
                    title = "YASK-CIEM least-privilege proposal",
                    description = $"Derived from successful observed activity ({windowStart:O} to {windowEnd:O}); review before use.",
                    stage = "GA",
                    includedPermissions = actions
                },
                Binding = new
                {
                    Scope = scope,
                    Condition = condition is null ? null : new { condition.Title, Expression = conditionExpression, condition.EvaluationStatus }
                }
            },
            _ => null
        };
        if (providerDocument is null) return null;
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = "1.0",
            provider,
            reviewOnly = true,
            observedResources = observed.Select(item => item.Resource).Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToArray(),
            scopeSemantics = provider switch
            {
                CloudProvider.Azure => "The role assignment remains at the source scope; observed resources are evidence, not validated assignment scope reductions.",
                CloudProvider.Aws => "Each statement retains its observed resource ARN and source IAM condition.",
                CloudProvider.Gcp => "A custom role restricts permissions, not target resources; the binding retains the source scope and condition.",
                _ => "Provider scope semantics require review."
            },
            impactAssessment = new { possibleAccessLoss = "Unknown", providerValidation = "Not performed" },
            providerProposal = providerDocument
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private static bool? ClassifyAzureDataAction(NormalizedPermission? permission, IReadOnlyList<string> actions)
    {
        if (permission?.Category is PermissionCategory.DataRead or PermissionCategory.DataWrite) return true;
        if (permission?.Category is PermissionCategory.Read or PermissionCategory.Write or PermissionCategory.Delete or PermissionCategory.Administrative) return false;
        var classifications = actions.Select(ClassifyAzureAction).ToArray();
        if (classifications.Length == 0 || classifications.Any(value => !value.HasValue)
            || classifications.Distinct().Count() != 1) return null;
        return classifications[0];
    }

    private static bool? ClassifyAzureAction(string action)
    {
        if (new[] { "/blobServices/", "/fileServices/", "/queueServices/", "/tableServices/", "/containers/blobs/", "/vaults/secrets/", "/vaults/keys/", "/vaults/certificates/" }
            .Any(segment => action.Contains(segment, StringComparison.OrdinalIgnoreCase))) return true;
        if (action.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
            && new[] { "/read", "/write", "/delete", "/action" }.Any(suffix => action.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))) return false;
        return null;
    }

    private static object? BuildAwsPolicy(IReadOnlyList<ObservedOperation> observed, string? conditionExpression)
    {
        System.Text.Json.JsonElement? condition = null;
        if (conditionExpression is not null)
        {
            try
            {
                using var parsed = System.Text.Json.JsonDocument.Parse(conditionExpression);
                if (parsed.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
                condition = parsed.RootElement.Clone();
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        var statements = observed.Select(item =>
        {
            var statement = new Dictionary<string, object?>
            {
                ["Effect"] = "Allow",
                ["Action"] = item.Action,
                ["Resource"] = item.Resource
            };
            if (condition.HasValue) statement["Condition"] = condition.Value;
            return statement;
        }).ToArray();
        return new { Version = "2012-10-17", Statement = statements };
    }

    private static NormalizedEntitlement? FindPolicyEntitlement(RecommendationContext context, RiskFindingModel finding)
    {
        var candidates = context.Entitlements.Where(item => item.CloudAccountId == finding.CloudAccountId
            && item.PrincipalId == finding.PrincipalId && item.PermissionId == finding.PermissionId).ToArray();
        if (candidates.Length <= 1) return candidates.FirstOrDefault();

        var effective = finding.EffectiveAccessId.HasValue
            ? context.EffectiveAccess.FirstOrDefault(item => item.Id == finding.EffectiveAccessId.Value)
            : null;
        var resource = finding.ResourceId.HasValue ? context.Resources.FirstOrDefault(item => item.Id == finding.ResourceId.Value) : null;
        var scored = candidates.Select(candidate =>
        {
            var score = 0;
            if (effective is not null)
            {
                if (candidate.Scope.OriginalScope.Equals(effective.Scope, StringComparison.OrdinalIgnoreCase)) score += 8;
                if (candidate.RoleId == effective.RoleId) score += 4;
                if (candidate.PolicyId == effective.PolicyId) score += 4;
            }
            if (resource is not null)
            {
                if (candidate.ResourceId == resource.Id) score += 6;
                if (!string.IsNullOrWhiteSpace(candidate.ResourcePattern)
                    && candidate.ResourcePattern.Equals(resource.ResourceId ?? resource.ResourceUri, StringComparison.OrdinalIgnoreCase)) score += 3;
            }
            return (Candidate: candidate, Score: score);
        }).OrderByDescending(item => item.Score).ToArray();
        var best = scored.Where(item => item.Score == scored[0].Score).Select(item => item.Candidate).ToArray();
        var signatures = best.Select(item => string.Join('|', item.Scope.OriginalScope, item.ResourceId, item.ResourcePattern, item.RoleId, item.PolicyId,
            item.Condition?.OriginalExpression ?? item.Condition?.Expression, item.Condition?.Title)).Distinct(StringComparer.Ordinal).ToArray();
        return signatures.Length == 1 ? best.OrderBy(item => item.Id).First() : null;
    }

    private static ProviderCondition? GetProviderCondition(CloudProvider provider, NormalizedEntitlement entitlement)
    {
        if (entitlement.Condition is not null)
        {
            var expression = entitlement.Condition.OriginalExpression ?? entitlement.Condition.Expression;
            var version = entitlement.Evidence.ProviderSpecificData.GetValueOrDefault("conditionVersion");
            return new ProviderCondition(entitlement.Condition.Title, expression ?? string.Empty, entitlement.Condition.EvaluationStatus, version);
        }

        if (provider == CloudProvider.Aws)
        {
            var raw = entitlement.Evidence.OriginalStatement ?? entitlement.Evidence.OriginalDocument;
            var expression = ExtractAwsCondition(raw);
            if (!string.IsNullOrWhiteSpace(expression)) return new ProviderCondition(null, expression, "PreservedFromSource", null);
            if (raw?.Contains("\"Condition\"", StringComparison.Ordinal) == true)
                return new ProviderCondition(null, string.Empty, "SourceConditionCouldNotBeParsed", null);
        }

        var providerData = entitlement.Evidence.ProviderSpecificData;
        var conditionPair = providerData.FirstOrDefault(item => item.Key.Equals("conditionExpression", StringComparison.OrdinalIgnoreCase)
            || item.Key.Equals("condition", StringComparison.OrdinalIgnoreCase));
        return conditionPair.Key is null
            ? null
            : new ProviderCondition(providerData.GetValueOrDefault("conditionTitle"), conditionPair.Value ?? string.Empty,
                "PreservedFromSource", providerData.GetValueOrDefault("conditionVersion"));
    }

    private static string? ExtractAwsCondition(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty("Condition", out var condition)) return condition.GetRawText();
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty("Statement", out var statements) && statements.ValueKind == System.Text.Json.JsonValueKind.Array)
                return statements.EnumerateArray().Select(statement => statement.TryGetProperty("Condition", out var found) ? found.GetRawText() : null).FirstOrDefault(value => value is not null);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        return null;
    }
}

public sealed class RemoveUnusedPermissionRule : RecommendationRuleBase
{
    public override string RuleId => "CIEM-008";
    public override RecommendationType RecommendationType => RecommendationType.RemoveUnusedPermission;

    public override Task<IReadOnlyCollection<RecommendationModel>> EvaluateAsync(RecommendationContext context, CancellationToken cancellationToken = default)
    {
        var windowEnd = context.EvaluatedAt.ToUniversalTime();
        var windowStart = windowEnd.AddDays(-Math.Max(1, context.ActivityObservationDays));
        var recommendations = Findings(context, RuleId)
            .Where(finding => finding.EvaluationState == RiskEvaluationState.Confirmed)
            .Where(finding =>
            {
                var entitlement = Entitlement(context, finding);
                var coverage = context.ActivityCoverage?.FirstOrDefault(item => item.CloudAccountId == finding.CloudAccountId && item.Provider == finding.CloudProvider);
                return entitlement is not null && ActivityUsageEvaluator.Covers(coverage, windowStart, windowEnd,
                    requiresActions: true, requiresResourceIds: finding.ResourceId.HasValue, requiresRoleIds: entitlement.RoleId.HasValue);
            })
            .Select(finding => Create(context, finding, "Remove unused permission", "Remove the entitlement after confirming the observed usage window.", Permission(context, finding.PermissionId),
                "Remove the unused permission from the source policy or assignment.", "Complete activity coverage confirms no matching successful use.", RecommendationImpactState.RequiresReview))
            .ToArray();
        return Task.FromResult<IReadOnlyCollection<RecommendationModel>>(recommendations);
    }
}
public sealed class RemoveUnusedRoleRule : RecommendationRuleBase
{
    public override string RuleId => "CIEM-016";
    public override RecommendationType RecommendationType => RecommendationType.RemoveUnusedRole;

    public override Task<IReadOnlyCollection<RecommendationModel>> EvaluateAsync(RecommendationContext context, CancellationToken cancellationToken = default)
    {
        var windowEnd = context.EvaluatedAt.ToUniversalTime();
        var windowStart = windowEnd.AddDays(-Math.Max(1, context.ActivityObservationDays));
        var recommendations = Findings(context, RuleId).Where(finding => finding.EvaluationState == RiskEvaluationState.Confirmed)
            .Where(finding => ActivityUsageEvaluator.Covers(
                context.ActivityCoverage?.FirstOrDefault(item => item.CloudAccountId == finding.CloudAccountId && item.Provider == finding.CloudProvider),
                windowStart, windowEnd, requiresRoleIds: true))
            .Select(finding => Create(context, finding, "Remove unused role assignment", "Review removing the role assignment after confirming ownership and business requirements.",
                Entitlement(context, finding)?.RoleId?.ToString() ?? "Unknown role", "Remove the unused role assignment from its source policy.",
                "Complete role-attributed activity coverage contains no successful use in the observation window.", RecommendationImpactState.RequiresReview))
            .ToArray();
        return Task.FromResult<IReadOnlyCollection<RecommendationModel>>(recommendations);
    }
}
public sealed class NarrowWildcardPermissionRule : RecommendationRuleBase { public override string RuleId => "CIEM-003"; public override RecommendationType RecommendationType => RecommendationType.NarrowWildcardPermission; public override Task<IReadOnlyCollection<RecommendationModel>> EvaluateAsync(RecommendationContext c, CancellationToken t = default) => Task.FromResult<IReadOnlyCollection<RecommendationModel>>(Findings(c, RuleId).Select(f => Create(c, f, "Narrow wildcard permission", "Replace the wildcard action with only observed required operations after review.", Permission(c, f.PermissionId), "Specific observed permissions at the narrowest supported scope.", "The risk evidence identifies a wildcard action; required operations are not fully inferred.", RecommendationImpactState.RequiresReview)).ToArray()); }
public sealed class NarrowResourceScopeRule : RecommendationRuleBase { public override string RuleId => "CIEM-004"; public override RecommendationType RecommendationType => RecommendationType.NarrowResourceScope; public override Task<IReadOnlyCollection<RecommendationModel>> EvaluateAsync(RecommendationContext c, CancellationToken t = default) => Task.FromResult<IReadOnlyCollection<RecommendationModel>>(Findings(c, RuleId).Select(f => Create(c, f, "Narrow resource scope", "Review reducing the assignment to only the resources evidenced by effective access.", Entitlement(c, f)?.Scope.OriginalScope ?? "unknown", Resource(c, f.ResourceId), "Broad scope is evidenced, but required business coverage must be validated.", RecommendationImpactState.RequiresReview)).ToArray()); }
public sealed class ReplaceBroadRoleRule : RecommendationRuleBase { public override string RuleId => "CIEM-001"; public override RecommendationType RecommendationType => RecommendationType.ReplaceBroadRole; public override Task<IReadOnlyCollection<RecommendationModel>> EvaluateAsync(RecommendationContext c, CancellationToken t = default) => Task.FromResult<IReadOnlyCollection<RecommendationModel>>(Findings(c, RuleId).Select(f => Create(c, f, "Review broad privileged role", "Evaluate replacing the current role with a narrower role that preserves required access.", "Privileged role or permission", "Provider-supported least-privilege role", "Required permissions and business ownership require review before approval.", RecommendationImpactState.HighPotentialImpact)).ToArray()); }
public sealed class RemoveRedundantAccessPathRule : RecommendationRuleBase { public override string RuleId => "CIEM-011"; public override RecommendationType RecommendationType => RecommendationType.RemoveRedundantAccessPath; public override Task<IReadOnlyCollection<RecommendationModel>> EvaluateAsync(RecommendationContext c, CancellationToken t = default) => Task.FromResult<IReadOnlyCollection<RecommendationModel>>(Findings(c, RuleId).Select(f => Create(c, f, "Review redundant access path", "Review whether one equivalent path can be removed while preserving effective access.", "Multiple access paths", "One validated access path", "The risk engine identified redundant paths; removal requires impact validation.", RecommendationImpactState.RequiresReview)).ToArray()); }
public sealed class RemoveDormantPrivilegeRule : RecommendationRuleBase { public override string RuleId => "CIEM-005"; public override RecommendationType RecommendationType => RecommendationType.RemoveDormantPrivilege; public override Task<IReadOnlyCollection<RecommendationModel>> EvaluateAsync(RecommendationContext c, CancellationToken t = default) => Task.FromResult<IReadOnlyCollection<RecommendationModel>>(Findings(c, RuleId).Where(f => f.EvaluationState == RiskEvaluationState.Confirmed).Select(f => Create(c, f, "Review dormant privilege", "Review removing or reducing the privileged assignment; do not disable the identity automatically.", "Dormant privileged access", "Reduced privilege after owner validation", "Configured dormancy evidence exceeds the review threshold.", RecommendationImpactState.RequiresReview)).ToArray()); }
public sealed class ReduceServiceAccountPermissionRule : RecommendationRuleBase { public override string RuleId => "CIEM-007"; public override RecommendationType RecommendationType => RecommendationType.ReduceServiceAccountPermission; public override Task<IReadOnlyCollection<RecommendationModel>> EvaluateAsync(RecommendationContext c, CancellationToken t = default) => Task.FromResult<IReadOnlyCollection<RecommendationModel>>(Findings(c, RuleId).Select(f => Create(c, f, "Reduce service account permission", "Review restricting the workload identity to observed required operations.", Permission(c, f.PermissionId), "Observed required permissions only", "The identity is non-human and has privileged effective access; activity coverage determines certainty.", RecommendationImpactState.RequiresReview)).ToArray()); }
public sealed class ReducePrivilegedGroupAccessRule : RecommendationRuleBase { public override string RuleId => "CIEM-012"; public override RecommendationType RecommendationType => RecommendationType.ReducePrivilegedGroupAccess; public override Task<IReadOnlyCollection<RecommendationModel>> EvaluateAsync(RecommendationContext c, CancellationToken t = default) => Task.FromResult<IReadOnlyCollection<RecommendationModel>>(Findings(c, RuleId).Select(f => Create(c, f, "Review privileged group access", "Review the group membership or assignment while preserving required access.", "Privileged group-derived access", "Validated group membership and narrower role", "Effective access is obtained through a privileged group path.", RecommendationImpactState.RequiresReview)).ToArray()); }
public sealed class ReduceNestedPrivilegeRule : RecommendationRuleBase { public override string RuleId => "CIEM-013"; public override RecommendationType RecommendationType => RecommendationType.ReduceNestedPrivilege; public override Task<IReadOnlyCollection<RecommendationModel>> EvaluateAsync(RecommendationContext c, CancellationToken t = default) => Task.FromResult<IReadOnlyCollection<RecommendationModel>>(Findings(c, RuleId).Select(f => Create(c, f, "Review nested privilege", "Review simplifying nested group privilege without changing required access.", "Nested privileged path", "Simplified validated membership path", "Nested access relationships increase review complexity.", RecommendationImpactState.RequiresReview)).ToArray()); }
public sealed class ReduceLongPrivilegeChainRule : RecommendationRuleBase { public override string RuleId => "CIEM-015"; public override RecommendationType RecommendationType => RecommendationType.ReduceLongPrivilegeChain; public override Task<IReadOnlyCollection<RecommendationModel>> EvaluateAsync(RecommendationContext c, CancellationToken t = default) => Task.FromResult<IReadOnlyCollection<RecommendationModel>>(Findings(c, RuleId).Select(f => Create(c, f, "Review long privilege chain", "Review whether the access chain can be simplified while retaining required access.", "Complex access path", "Shorter validated access path", "The path exceeds the configured complexity threshold.", RecommendationImpactState.RequiresReview)).ToArray()); }
public sealed class ConsolidateAccessRule : RecommendationRuleBase { public override string RuleId => "CIEM-011"; public override RecommendationType RecommendationType => RecommendationType.ConsolidateAccess; public override Task<IReadOnlyCollection<RecommendationModel>> EvaluateAsync(RecommendationContext c, CancellationToken t = default) => Task.FromResult<IReadOnlyCollection<RecommendationModel>>(Array.Empty<RecommendationModel>()); }

internal static class GuidUtility { public static readonly Guid Namespace = Guid.Parse("7c31cbf1-3b9f-4cc2-b9d2-0f3f0f02c6af"); public static Guid Create(Guid ns, string name) { var h = System.Security.Cryptography.SHA1.HashData(ns.ToByteArray().Concat(System.Text.Encoding.UTF8.GetBytes(name)).ToArray()); var b = h.Take(16).ToArray(); b[6] = (byte)((b[6] & 15) | 80); b[8] = (byte)((b[8] & 63) | 128); return new Guid(b); } }
