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
    protected RecommendationModel Create(RecommendationContext context, RiskFindingModel finding, string title, string description, string current, string recommended, string reason, RecommendationImpactState impact, Guid? pathId = null)
    {
        var entitlement = Entitlement(context, finding);
        var path = pathId.HasValue ? context.AccessPaths.FirstOrDefault(item => item.Id == pathId) : Path(context, finding);
        var state = finding.EvaluationState == RiskEvaluationState.Confirmed && entitlement != null && finding.EffectiveAccessId.HasValue ? RiskEvaluationState.Confirmed : finding.EvaluationState == RiskEvaluationState.Unknown ? RiskEvaluationState.Unknown : RiskEvaluationState.Potential;
        var coverage = context.ActivityCoverage?.FirstOrDefault(item => item.CloudAccountId == finding.CloudAccountId && item.Provider == finding.CloudProvider);
        var activityEvidence = coverage is null
            ? "Unavailable: no provider/account activity coverage record."
            : $"{coverage.Status}; Window={coverage.ObservedFromUtc:O}..{coverage.ObservedThroughUtc:O}; Records={coverage.RecordCount}; {coverage.Details ?? "No additional details."}";
        var evidence = new RecommendationEvidence(finding.Id, finding.EffectiveAccessId, path?.Id, entitlement?.Id, finding.CloudProvider, "RiskFinding", finding.Fingerprint, finding.Title, Name(context, finding.PrincipalId), Resource(context, finding.ResourceId), Permission(context, finding.PermissionId), entitlement?.Scope.OriginalScope ?? "unknown", entitlement?.AssignmentType.ToString() ?? "unknown", finding.Evidence.Summary, activityEvidence, $"EffectiveAccess={finding.EffectiveAccessId}; AccessPath={path?.Id}; Entitlement={entitlement?.Id}", finding.Evidence.Summary);
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
            var entitlement = Entitlement(context, finding);
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

            var proposedPolicy = BuildPolicy(finding.CloudProvider, entitlement.Scope.OriginalScope, observed, windowStart, windowEnd);
            if (proposedPolicy is null) continue;
            var description = $"Generated from {observed.Length} successful action/resource observation(s) over {context.ActivityObservationDays} days. Review business requirements and provider constraints before replacing the source policy.";
            recommendations.Add(Create(context, finding, "Generate least-privilege policy proposal", description, permission.Name, proposedPolicy,
                $"Observed operations only; activity coverage was complete from {windowStart:O} through {windowEnd:O}.", RecommendationImpactState.RequiresReview));
        }

        return Task.FromResult<IReadOnlyCollection<RecommendationModel>>(recommendations);
    }

    private static string? BuildPolicy(CloudProvider provider, string scope, IReadOnlyList<ObservedOperation> observed, DateTime windowStart, DateTime windowEnd)
    {
        var actions = observed.Select(item => item.Action).Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToArray();
        var resources = observed.Select(item => item.Resource).Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToArray();
        object? document = provider switch
        {
            CloudProvider.Azure => new
            {
                Name = "YASK-CIEM least-privilege proposal",
                IsCustom = true,
                Description = $"Derived from successful observed activity ({windowStart:O} to {windowEnd:O}); review before use.",
                Actions = actions,
                NotActions = Array.Empty<string>(),
                DataActions = Array.Empty<string>(),
                NotDataActions = Array.Empty<string>(),
                AssignableScopes = new[] { scope }
            },
            CloudProvider.Aws => new
            {
                Version = "2012-10-17",
                Statement = observed.Select(item => new { Effect = "Allow", item.Action, item.Resource }).ToArray()
            },
            CloudProvider.Gcp => new
            {
                title = "YASK-CIEM least-privilege proposal",
                description = $"Derived from successful observed activity ({windowStart:O} to {windowEnd:O}); bind only at the reviewed scope: {scope}.",
                includedPermissions = actions
            },
            _ => null
        };
        return document is null ? null : System.Text.Json.JsonSerializer.Serialize(document, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
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
