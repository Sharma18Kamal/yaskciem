using System.Text.Json;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.EffectiveAccess.Abstractions;
using CloudEntitle.EffectiveAccess.Models;
using CloudEntitle.Normalization.Models;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;

namespace CloudEntitle.EffectiveAccess.Services;

public sealed class EffectiveAccessEngine : IEffectiveAccessEngine
{
    private readonly IReadOnlyDictionary<CloudProvider, IAccessEvaluator> _evaluators;
    private readonly IScopeResolver _scopeResolver;

    public EffectiveAccessEngine(IEnumerable<IAccessEvaluator>? evaluators = null, IScopeResolver? scopeResolver = null)
    {
        _evaluators = (evaluators ?? new IAccessEvaluator[] { new AzureAccessEvaluator(), new AwsAccessEvaluator(), new GcpAccessEvaluator() }).ToDictionary(item => item.Provider);
        _scopeResolver = scopeResolver ?? new ScopeResolutionService();
    }

    public async Task<AccessEvaluationResult> EvaluateAsync(AccessEvaluationRequest request, CancellationToken cancellationToken = default)
    {
        var results = new List<DomainEffectiveAccess>();
        var accessPaths = new Dictionary<Guid, IReadOnlyList<AccessExplanationPath>>();
        var diagnostics = new List<AccessEvaluationDiagnostic>();
        var principals = request.PrincipalId.HasValue
            ? new[] { request.PrincipalId.Value }
            : request.Normalized.Principals.Select(principal => principal.Id)
                .Concat(request.Normalized.Entitlements.Select(entitlement => entitlement.PrincipalId)).Distinct().ToArray();
        var resources = request.Resources.Where(resource => !request.ResourceId.HasValue || resource.Id == request.ResourceId.Value).ToArray();

        foreach (var principalId in principals)
        {
            foreach (var resource in resources)
            {
                foreach (var permission in request.Normalized.Permissions.Where(permission => permission.AccountId == resource.AccountId && (!request.PermissionId.HasValue || permission.Id == request.PermissionId.Value)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var explanation = EvaluateOne(request, principalId, resource, permission.Id);
                    diagnostics.AddRange(explanation.Diagnostics);
                    if (explanation.Decision == AccessDecision.NotApplicable) continue;
                    var entitlement = explanation.ApplicableEntitlements.FirstOrDefault();
                    var provider = GetProvider(request, principalId, explanation.ApplicableEntitlements, permission);
                    var effectiveAccess = new DomainEffectiveAccess
                    {
                        Id = StableId(principalId, resource.Id, permission.Id),
                        Provider = provider,
                        CloudAccountId = resource.AccountId,
                        PrincipalId = principalId,
                        ResourceId = resource.Id,
                        PermissionId = permission.Id,
                        RoleId = entitlement?.RoleId,
                        PolicyId = entitlement?.PolicyId,
                        Scope = entitlement?.Scope.OriginalScope,
                        Decision = explanation.Decision,
                        EvaluationStatus = explanation.EvaluationStatus,
                        AccessType = explanation.Decision == AccessDecision.Deny ? AccessType.Deny : AccessType.Allow,
                        AssignmentType = entitlement?.AssignmentType ?? AssignmentType.Unknown,
                        IsDirect = explanation.IsDirect,
                        IsInherited = explanation.IsInherited,
                        IsConditional = explanation.IsConditional,
                        IsPrivileged = entitlement?.Evidence.ProviderSpecificData.TryGetValue("privileged", out var privileged) == true && privileged == "true",
                        AccessSource = explanation.AccessSource,
                        Evidence = JsonSerializer.Serialize(explanation.Evidence),
                        EvaluationDetails = JsonSerializer.Serialize(new { explanation.Diagnostics, explanation.AccessPaths }),
                        CalculatedAt = request.EvaluationTime ?? DateTime.UtcNow
                    };
                    results.Add(effectiveAccess);
                    accessPaths[effectiveAccess.Id] = explanation.AccessPaths;
                }
            }
        }

        return await Task.FromResult(new AccessEvaluationResult(results, diagnostics, results.Count(item => item.Decision == AccessDecision.Allow), results.Count(item => item.Decision == AccessDecision.Deny), results.Count(item => item.Decision == AccessDecision.Conditional), results.Count(item => item.Decision == AccessDecision.Unknown), results.Count(item => item.Decision == AccessDecision.Unsupported)) { AccessPaths = accessPaths });
    }

    public Task<AccessDecisionResult> ExplainAsync(AccessEvaluationRequest request, Guid principalId, Guid resourceId, Guid permissionId, CancellationToken cancellationToken = default)
    {
        var resource = request.Resources.FirstOrDefault(item => item.Id == resourceId);
        if (resource == null) return Task.FromResult(new AccessDecisionResult(AccessDecision.Unknown, EvaluationStatus.Invalid, false, false, false, "Unknown", Array.Empty<NormalizedEntitlement>(), Array.Empty<ProviderEvidence>(), new[] { new AccessEvaluationDiagnostic("RESOURCE_NOT_FOUND", "The requested resource is not present.", null, true) }));
        return Task.FromResult(EvaluateOne(request, principalId, resource, permissionId));
    }

    private AccessDecisionResult EvaluateOne(AccessEvaluationRequest request, Guid principalId, CloudResource resource, Guid permissionId)
    {
        var permission = request.Normalized.Permissions.FirstOrDefault(item => item.Id == permissionId);
        if (permission == null) return new(AccessDecision.Unknown, EvaluationStatus.Invalid, false, false, false, "Unknown", Array.Empty<NormalizedEntitlement>(), Array.Empty<ProviderEvidence>(), new[] { new AccessEvaluationDiagnostic("PERMISSION_NOT_FOUND", "The requested permission is not present.", null, true) });
        if (permission.AccountId != resource.AccountId)
        {
            return new(AccessDecision.Unknown, EvaluationStatus.Invalid, false, false, false, "Account mismatch", Array.Empty<NormalizedEntitlement>(), Array.Empty<ProviderEvidence>(), new[] { new AccessEvaluationDiagnostic("ACCOUNT_MISMATCH", "The permission and resource belong to different cloud accounts.", permission.Id.ToString(), true) });
        }

        var provider = GetProvider(request, principalId, request.Normalized.Entitlements.Where(item => item.PrincipalId == principalId).ToArray(), permission);
        var evaluationTime = request.EvaluationTime ?? DateTime.UtcNow;
        var principalPaths = ResolvePrincipalPaths(request, principalId, resource.AccountId, provider, evaluationTime);
        var targetAction = permission.Action ?? permission.Name;
        var applicable = request.Normalized.Entitlements.Where(item =>
        {
            if (item.CloudAccountId != resource.AccountId || item.Provider != provider || !principalPaths.ContainsKey(item.PrincipalId) || !_scopeResolver.Applies(item, resource)) return false;
            var candidatePermission = request.Normalized.Permissions.FirstOrDefault(candidate => candidate.Id == item.PermissionId);
            if (candidatePermission is null || !ActionMatches(provider, candidatePermission.Action ?? candidatePermission.Name, targetAction)) return false;
            return provider != CloudProvider.Aws || ResourcePatternMatches(item.ResourcePattern ?? candidatePermission.Resource, resource.ResourceId ?? resource.ResourceUri ?? string.Empty);
        }).ToArray();

        if (applicable.Length == 0) return new(AccessDecision.NotApplicable, EvaluationStatus.FullyEvaluated, false, false, false, "No applicable entitlement", applicable, Array.Empty<ProviderEvidence>(), Array.Empty<AccessEvaluationDiagnostic>());

        var context = new AccessEvaluationContext(provider, resource.AccountId, principalId, resource.Id, permissionId, evaluationTime, request.RuntimeConditionContext ?? new Dictionary<string, string>())
        {
            Resource = resource,
            Permission = permission,
            ApplicableEntitlements = applicable,
            HasIncompleteNormalization = request.Normalized.Errors.Count > 0 || request.Normalized.Statistics.ItemsFailed > 0
        };
        var diagnostics = new List<AccessEvaluationDiagnostic>();
        if (!_evaluators.TryGetValue(provider, out var evaluator))
        {
            return new(AccessDecision.Unsupported, EvaluationStatus.Unsupported, false, false, false, "Provider evaluator unavailable", applicable, applicable.Select(item => item.Evidence).ToArray(), new[] { new AccessEvaluationDiagnostic("PROVIDER_UNSUPPORTED", $"No evaluator is registered for {provider}.", null, true) });
        }

        var result = evaluator.Evaluate(context, request);
        diagnostics.AddRange(result.Diagnostics);
        var accessPaths = BuildAccessPaths(request, context, principalPaths);
        return result with { AccessPaths = accessPaths };
    }

    private static IReadOnlyList<AccessExplanationPath> BuildAccessPaths(AccessEvaluationRequest request, AccessEvaluationContext context, IReadOnlyDictionary<Guid, IReadOnlyList<AccessPathStep>> principalPaths)
    {
        return context.ApplicableEntitlements.Select(entitlement =>
        {
            var steps = principalPaths.TryGetValue(entitlement.PrincipalId, out var path)
                ? path.ToList()
                : new List<AccessPathStep> { new("Principal", entitlement.PrincipalId.ToString(), entitlement.Evidence.SourceId, "Resolved principal") };
            if (entitlement.RoleId is Guid roleId) steps.Add(new("Role", request.Normalized.Roles.FirstOrDefault(role => role.Id == roleId)?.Name ?? roleId.ToString(), entitlement.Evidence.SourceId, entitlement.AssignmentType.ToString()));
            if (entitlement.PolicyId is Guid policyId) steps.Add(new("Policy", request.Normalized.Policies.FirstOrDefault(policy => policy.Id == policyId)?.Name ?? policyId.ToString(), entitlement.Evidence.SourceId, entitlement.Evidence.SourceType));
            var permission = request.Normalized.Permissions.FirstOrDefault(item => item.Id == entitlement.PermissionId);
            steps.Add(new("Permission", permission?.Action ?? permission?.Name ?? entitlement.PermissionId.ToString(), entitlement.Evidence.SourceId, entitlement.IsExclusion ? "Azure role exclusion" : entitlement.AccessType.ToString()));
            if (entitlement.Condition is not null) steps.Add(new("Condition", entitlement.Condition.Expression ?? entitlement.Condition.Title ?? "Condition", entitlement.Evidence.SourceId, "Requires provider runtime context"));
            steps.Add(new("Scope", entitlement.Scope.OriginalScope, entitlement.Evidence.SourceId, entitlement.IsInherited ? "Inherited" : "Assigned scope"));
            steps.Add(new("Resource", context.Resource?.Name ?? context.ResourceId.ToString(), context.Resource?.ResourceId, permission?.Action));
            var decision = entitlement.IsExclusion ? "Excluded" : entitlement.AccessType.ToString();
            return new AccessExplanationPath(entitlement.Evidence.SourceId, decision, steps);
        }).ToArray();
    }

    private static Dictionary<Guid, IReadOnlyList<AccessPathStep>> ResolvePrincipalPaths(AccessEvaluationRequest request, Guid principalId, Guid accountId, CloudProvider provider, DateTime evaluationTime)
    {
        var paths = new Dictionary<Guid, IReadOnlyList<AccessPathStep>>();
        var principal = request.Normalized.Principals.FirstOrDefault(item => item.Id == principalId);
        paths[principalId] = new[] { new AccessPathStep("Principal", principal?.Name ?? principalId.ToString(), principal?.PrincipalId, principal?.IdentityType.ToString()) };
        var pending = new Queue<Guid>();
        pending.Enqueue(principalId);
        while (pending.Count > 0)
        {
            var memberId = pending.Dequeue();
            foreach (var membership in request.GroupMemberships.Where(item => item.PrincipalId == memberId && (item.CloudAccountId == accountId || item.CloudAccountId == Guid.Empty) && (!item.ExpiresAt.HasValue || item.ExpiresAt.Value > evaluationTime) && item.MembershipType is MembershipType.Direct or MembershipType.Inherited))
            {
                if (paths.ContainsKey(membership.GroupId)) continue;
                var group = request.Normalized.Principals.FirstOrDefault(item => item.Id == membership.GroupId);
                paths[membership.GroupId] = paths[memberId].Append(new AccessPathStep("Group", group?.Name ?? membership.GroupId.ToString(), membership.Id.ToString(), membership.MembershipType.ToString())).ToArray();
                pending.Enqueue(membership.GroupId);
            }

            foreach (var trust in request.RoleTrustRelationships ?? Array.Empty<RoleTrustRelationship>())
            {
                if (trust.Provider != provider || trust.CloudAccountId != accountId || paths.ContainsKey(trust.RolePrincipalId)) continue;
                var trustedPrincipal = request.Normalized.Principals.FirstOrDefault(item => item.Id == memberId);
                var trusted = trust.TrustedPrincipalId == memberId
                    || (!string.IsNullOrWhiteSpace(trust.TrustedPrincipalExternalId) && string.Equals(trustedPrincipal?.PrincipalId, trust.TrustedPrincipalExternalId, StringComparison.Ordinal));
                if (!trusted) continue;
                var rolePrincipal = request.Normalized.Principals.FirstOrDefault(item => item.Id == trust.RolePrincipalId);
                paths[trust.RolePrincipalId] = paths[memberId].Append(new AccessPathStep("AssumeRole", rolePrincipal?.Name ?? trust.RolePrincipalId.ToString(), trust.SourceId, trust.Description)).ToArray();
                pending.Enqueue(trust.RolePrincipalId);
            }
        }
        return paths;
    }

    private static CloudProvider GetProvider(AccessEvaluationRequest request, Guid principalId, IReadOnlyList<NormalizedEntitlement> entitlements, CloudPermission permission)
    {
        var principal = request.Normalized.Principals.FirstOrDefault(item => item.Id == principalId);
        if (principal is not null) return principal.CloudProvider switch
        {
            CloudProviderType.Azure => CloudProvider.Azure,
            CloudProviderType.Aws => CloudProvider.Aws,
            CloudProviderType.Gcp => CloudProvider.Gcp,
            _ => CloudProvider.Kubernetes
        };
        if (entitlements.Count > 0) return entitlements[0].Provider;
        return Enum.TryParse<CloudProvider>(permission.Provider, true, out var provider) ? provider : CloudProvider.Kubernetes;
    }

    internal static bool ActionMatches(CloudProvider provider, string pattern, string action)
        => WildcardMatches(pattern, action, provider != CloudProvider.Gcp);

    internal static bool ResourcePatternMatches(string? pattern, string resource)
        => string.IsNullOrWhiteSpace(pattern) || pattern == "*" || WildcardMatches(pattern, resource, false);

    private static bool WildcardMatches(string pattern, string value, bool ignoreCase)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var patternIndex = 0;
        var valueIndex = 0;
        var starIndex = -1;
        var retryValueIndex = -1;
        while (valueIndex < value.Length)
        {
            if (patternIndex < pattern.Length && (pattern[patternIndex] == '?' || string.Equals(pattern[patternIndex].ToString(), value[valueIndex].ToString(), comparison)))
            {
                patternIndex++;
                valueIndex++;
            }
            else if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starIndex = patternIndex++;
                retryValueIndex = valueIndex;
            }
            else if (starIndex >= 0)
            {
                patternIndex = starIndex + 1;
                valueIndex = ++retryValueIndex;
            }
            else return false;
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*') patternIndex++;
        return patternIndex == pattern.Length;
    }

    private static Guid StableId(Guid principalId, Guid resourceId, Guid permissionId) => GuidUtility.Create(GuidUtility.Namespace, $"effective|{principalId}|{resourceId}|{permissionId}");
}

public sealed class ScopeResolutionService : IScopeResolver
{
    public bool Applies(NormalizedEntitlement entitlement, CloudResource resource)
    {
        if (entitlement.CloudAccountId != resource.AccountId) return false;
        if (entitlement.ResourceId.HasValue) return entitlement.ResourceId == resource.Id;
        var scope = entitlement.Scope.OriginalScope.TrimEnd('/');
        if (scope == "*" || scope.Equals("global", StringComparison.OrdinalIgnoreCase) || scope.Equals("account", StringComparison.OrdinalIgnoreCase)) return true;
        var targets = new[] { resource.ResourceId, resource.Scope, resource.ParentResourceId, resource.ResourceUri }.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.TrimEnd('/'));
        if (entitlement.Provider == CloudProvider.Aws && scope.EndsWith(":root", StringComparison.Ordinal)) return true;
        if (entitlement.Provider == CloudProvider.Aws) return targets.Any(target => EffectiveAccessEngine.ResourcePatternMatches(scope, target));
        return targets.Any(target => IsSameOrChild(target, scope, entitlement.Provider == CloudProvider.Azure));
    }

    private static bool IsSameOrChild(string target, string scope, bool ignoreCase)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(target, scope, comparison) || target.StartsWith(scope + "/", comparison);
    }
}

public sealed class AzureAccessEvaluator : IAccessEvaluator { public CloudProvider Provider => CloudProvider.Azure; public AccessDecisionResult Evaluate(AccessEvaluationContext context, AccessEvaluationRequest request) => ProviderAccessPolicy.Evaluate(Provider, context); }
public sealed class AwsAccessEvaluator : IAccessEvaluator { public CloudProvider Provider => CloudProvider.Aws; public AccessDecisionResult Evaluate(AccessEvaluationContext context, AccessEvaluationRequest request) => ProviderAccessPolicy.Evaluate(Provider, context); }
public sealed class GcpAccessEvaluator : IAccessEvaluator { public CloudProvider Provider => CloudProvider.Gcp; public AccessDecisionResult Evaluate(AccessEvaluationContext context, AccessEvaluationRequest request) => ProviderAccessPolicy.Evaluate(Provider, context); }

internal static class ProviderAccessPolicy
{
    public static AccessDecisionResult Evaluate(CloudProvider provider, AccessEvaluationContext context)
    {
        var applicable = context.ApplicableEntitlements;
        var diagnostics = new List<AccessEvaluationDiagnostic>();
        if (context.HasIncompleteNormalization) diagnostics.Add(new("NORMALIZATION_INCOMPLETE", "Some provider records failed normalization; allow results are partial.", null));
        var exclusions = provider == CloudProvider.Azure ? applicable.Where(item => item.IsExclusion).ToArray() : Array.Empty<NormalizedEntitlement>();
        var unrecognizedDenies = applicable.Where(item => item.AccessType == NormalizedAccessType.Deny && !item.IsExclusion && !IsExplicitDeny(provider, item)).ToArray();
        if (unrecognizedDenies.Length > 0)
        {
            diagnostics.Add(new("DENY_SEMANTICS_UNSUPPORTED", $"One or more {provider} deny records do not identify a provider-native deny policy and were not treated as a global deny.", unrecognizedDenies[0].Evidence.SourceId));
        }

        var denies = applicable.Where(item => item.AccessType == NormalizedAccessType.Deny && !item.IsExclusion && IsExplicitDeny(provider, item)).ToArray();
        var conditional = applicable.Where(item => item.IsConditional || item.AccessType == NormalizedAccessType.Conditional).ToArray();
        var resolvedConditionalAllows = new List<NormalizedEntitlement>();
        var resolvedConditionalDenies = new List<NormalizedEntitlement>();
        var unresolvedConditional = new List<NormalizedEntitlement>();
        foreach (var entitlement in conditional)
        {
            if (!TryEvaluateCondition(provider, entitlement.Condition, context.RuntimeConditionContext, out var conditionMatches))
            {
                unresolvedConditional.Add(entitlement);
                continue;
            }
            if (!conditionMatches) continue;
            var effect = entitlement.ConditionalEffect ?? AccessType.Allow;
            if (effect == AccessType.Deny) resolvedConditionalDenies.Add(entitlement);
            else resolvedConditionalAllows.Add(entitlement);
        }

        denies = denies.Concat(resolvedConditionalDenies.Where(item => IsExplicitDeny(provider, item))).ToArray();
        if (denies.Length > 0)
        {
            return Create(AccessDecision.Deny, EvaluationStatus.FullyEvaluated, denies, diagnostics, $"Explicit {provider} deny");
        }

        var conditionalDeniesWithoutProviderSemantics = resolvedConditionalDenies.Where(item => !IsExplicitDeny(provider, item)).ToArray();
        if (conditionalDeniesWithoutProviderSemantics.Length > 0)
        {
            diagnostics.Add(new("CONDITIONAL_DENY_UNSUPPORTED", $"A conditional {provider} deny was not applied because its provider-native deny semantics are not identified.", conditionalDeniesWithoutProviderSemantics[0].Evidence.SourceId, true));
        }
        if (unresolvedConditional.Count > 0)
        {
            diagnostics.Add(new("CONDITION_REQUIRED", "The provider condition is preserved in the access path but cannot be resolved from the supplied runtime context.", unresolvedConditional[0].Evidence.SourceId));
        }

        var unknown = applicable.Where(item => item.AccessType == NormalizedAccessType.Unknown || item.Status is NormalizationStatus.Unsupported or NormalizationStatus.Invalid or NormalizationStatus.Unknown).ToArray();
        unknown = unknown.Concat(unrecognizedDenies).Concat(conditionalDeniesWithoutProviderSemantics).ToArray();
        var allows = applicable.Where(item => item.AccessType == NormalizedAccessType.Allow && !item.IsExclusion)
            .Concat(resolvedConditionalAllows)
            .Where(item => !IsExcluded(provider, item, exclusions, context.Permission)).ToArray();
        if (provider == CloudProvider.Azure && exclusions.Any(item => !item.RoleId.HasValue))
        {
            diagnostics.Add(new("AZURE_EXCLUSION_ROLE_MISSING", "A NotActions exclusion without its role identity cannot be associated safely with a grant.", exclusions.First(item => !item.RoleId.HasValue).Evidence.SourceId));
            unknown = unknown.Concat(exclusions.Where(item => !item.RoleId.HasValue)).ToArray();
        }

        if (allows.Length > 0)
        {
            var partial = unresolvedConditional.Count > 0 || unknown.Length > 0 || context.HasIncompleteNormalization;
            var source = allows.Any(item => item.IsInherited) ? $"Inherited {provider} grant" : $"Direct {provider} grant";
            return Create(AccessDecision.Allow, partial ? EvaluationStatus.PartiallyEvaluated : EvaluationStatus.FullyEvaluated, allows, diagnostics, source, unresolvedConditional.Count > 0);
        }
        if (unresolvedConditional.Count > 0) return Create(AccessDecision.Conditional, EvaluationStatus.ConditionRequired, unresolvedConditional, diagnostics, $"Conditional {provider} grant", true);
        if (unknown.Length > 0) return Create(unknown.Any(item => item.Status == NormalizationStatus.Unsupported) ? AccessDecision.Unsupported : AccessDecision.Unknown, unknown.Any(item => item.Status == NormalizationStatus.Unsupported) ? EvaluationStatus.Unsupported : EvaluationStatus.PartiallyEvaluated, unknown, diagnostics, $"{provider} policy coverage incomplete");
        if (exclusions.Length > 0) return Create(AccessDecision.NotApplicable, EvaluationStatus.FullyEvaluated, exclusions, diagnostics, "Excluded from this Azure role");
        return Create(AccessDecision.NotApplicable, EvaluationStatus.FullyEvaluated, applicable, diagnostics, "No applicable grant");

        AccessDecisionResult Create(AccessDecision decision, EvaluationStatus status, IReadOnlyList<NormalizedEntitlement> evidence, IReadOnlyList<AccessEvaluationDiagnostic> currentDiagnostics, string source, bool isConditional = false)
            => new(decision, status, evidence.Any(item => item.PrincipalId == context.PrincipalId && item.IsDirect), evidence.Any(item => item.IsInherited), isConditional, source, evidence, evidence.Select(item => item.Evidence).ToArray(), currentDiagnostics);
    }

    private static bool TryEvaluateCondition(CloudProvider provider, NormalizedCondition? condition, IReadOnlyDictionary<string, string> runtimeContext, out bool result)
    {
        result = false;
        if (condition?.Expression is not string expression || string.IsNullOrWhiteSpace(expression)) return false;
        if (provider == CloudProvider.Gcp)
        {
            const string startsWithOperator = ".startsWith(";
            var startsWithIndex = expression.IndexOf(startsWithOperator, StringComparison.Ordinal);
            if (startsWithIndex > 0 && expression.EndsWith(')'))
            {
                var key = expression[..startsWithIndex].Trim();
                var prefix = expression[(startsWithIndex + startsWithOperator.Length)..^1].Trim().Trim('\'', '"');
                if (TryGetRuntimeValue(runtimeContext, key, out var value))
                {
                    result = value.StartsWith(prefix, StringComparison.Ordinal);
                    return true;
                }
            }
        }

        var operatorIndex = expression.IndexOf("==", StringComparison.Ordinal);
        var operationLength = 2;
        var isNotEqual = false;
        if (operatorIndex < 0)
        {
            operatorIndex = expression.IndexOf("!=", StringComparison.Ordinal);
            isNotEqual = operatorIndex >= 0;
        }
        if (operatorIndex <= 0) return false;
        var claim = expression[..operatorIndex].Trim().TrimStart('@', '$');
        var expected = expression[(operatorIndex + operationLength)..].Trim().TrimEnd(';').Trim().Trim('\'', '"');
        if (!TryGetRuntimeValue(runtimeContext, claim, out var actual)) return false;
        var matches = string.Equals(actual, expected, provider == CloudProvider.Gcp ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        result = isNotEqual ? !matches : matches;
        return true;
    }

    private static bool TryGetRuntimeValue(IReadOnlyDictionary<string, string> runtimeContext, string key, out string value)
    {
        if (runtimeContext.TryGetValue(key, out value!)) return true;
        var item = runtimeContext.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase));
        value = item.Value ?? string.Empty;
        return item.Key is not null;
    }

    private static bool IsExplicitDeny(CloudProvider provider, NormalizedEntitlement entitlement)
    {
        var sourceType = entitlement.Evidence.SourceType;
        return provider switch
        {
            CloudProvider.Azure => sourceType.Contains("DenyAssignment", StringComparison.OrdinalIgnoreCase) || sourceType.Contains("DenyPolicy", StringComparison.OrdinalIgnoreCase) || sourceType.Contains("SnapshotExplicitDeny", StringComparison.OrdinalIgnoreCase),
            CloudProvider.Gcp => sourceType.Contains("Deny", StringComparison.OrdinalIgnoreCase),
            CloudProvider.Aws => true,
            _ => false
        };
    }

    private static bool IsExcluded(CloudProvider provider, NormalizedEntitlement allow, IReadOnlyList<NormalizedEntitlement> exclusions, CloudPermission? targetPermission)
    {
        if (provider != CloudProvider.Azure || !allow.RoleId.HasValue || targetPermission is null) return false;
        return exclusions.Any(exclusion => exclusion.RoleId == allow.RoleId);
    }
}

internal static class GuidUtility
{
    public static readonly Guid Namespace = Guid.Parse("7c31cbf1-3b9f-4cc2-b9d2-0f3f0f02c6af");
    public static Guid Create(Guid namespaceId, string name)
    {
        var hash = System.Security.Cryptography.SHA1.HashData(namespaceId.ToByteArray().Concat(System.Text.Encoding.UTF8.GetBytes(name)).ToArray());
        var bytes = hash.Take(16).ToArray(); bytes[6] = (byte)((bytes[6] & 0x0f) | 0x50); bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80); return new Guid(bytes);
    }
}
