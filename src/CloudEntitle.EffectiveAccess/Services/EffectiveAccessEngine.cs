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
        var diagnostics = new List<AccessEvaluationDiagnostic>();
        var candidates = request.Normalized.Entitlements
            .Where(entitlement => !request.PrincipalId.HasValue || entitlement.PrincipalId == request.PrincipalId.Value)
            .Where(entitlement => !request.PermissionId.HasValue || entitlement.PermissionId == request.PermissionId.Value)
            .ToArray();
        var principals = candidates.Select(entitlement => entitlement.PrincipalId).Distinct().ToArray();
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
                    results.Add(new DomainEffectiveAccess
                    {
                        Id = StableId(principalId, resource.Id, permission.Id),
                        Provider = request.Normalized.Principals.FirstOrDefault(item => item.Id == principalId)?.CloudProvider switch
                        {
                            CloudProviderType.Azure => CloudProvider.Azure,
                            CloudProviderType.Aws => CloudProvider.Aws,
                            CloudProviderType.Gcp => CloudProvider.Gcp,
                            _ => CloudProvider.Kubernetes
                        },
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
                        EvaluationDetails = JsonSerializer.Serialize(explanation.Diagnostics),
                        CalculatedAt = request.EvaluationTime ?? DateTime.UtcNow
                    });
                }
            }
        }

        return await Task.FromResult(new AccessEvaluationResult(results, diagnostics, results.Count(item => item.Decision == AccessDecision.Allow), results.Count(item => item.Decision == AccessDecision.Deny), results.Count(item => item.Decision == AccessDecision.Conditional), results.Count(item => item.Decision == AccessDecision.Unknown), results.Count(item => item.Decision == AccessDecision.Unsupported)));
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
        var principalIds = ResolvePrincipalClosure(principalId, request.GroupMemberships);
        var applicable = request.Normalized.Entitlements.Where(item => item.PermissionId == permissionId && principalIds.Contains(item.PrincipalId) && _scopeResolver.Applies(item, resource)).ToArray();
        if (applicable.Length == 0) return new(AccessDecision.NotApplicable, EvaluationStatus.FullyEvaluated, false, false, false, "None", applicable, Array.Empty<ProviderEvidence>(), Array.Empty<AccessEvaluationDiagnostic>());

        var diagnostics = new List<AccessEvaluationDiagnostic>();
        var conditional = applicable.Where(item => item.IsConditional || item.AccessType == NormalizedAccessType.Conditional).ToArray();
        var denies = applicable.Where(item => item.AccessType == NormalizedAccessType.Deny).ToArray();
        var allows = applicable.Where(item => item.AccessType == NormalizedAccessType.Allow).ToArray();
        if (conditional.Length > 0) diagnostics.Add(new("CONDITION_REQUIRED", "A runtime condition could not be evaluated safely.", conditional[0].Evidence.SourceId));
        if (denies.Length > 0) return Result(AccessDecision.Deny, EvaluationStatus.FullyEvaluated, denies, principalId, diagnostics, "Explicit deny");
        if (conditional.Length > 0) return Result(AccessDecision.Conditional, EvaluationStatus.ConditionRequired, conditional, principalId, diagnostics, "Conditional entitlement");
        if (allows.Length > 0) return Result(AccessDecision.Allow, EvaluationStatus.FullyEvaluated, allows, principalId, diagnostics, allows.Any(item => item.IsInherited) ? "Inherited entitlement" : "Direct entitlement");
        return Result(AccessDecision.Unknown, EvaluationStatus.PartiallyEvaluated, applicable, principalId, diagnostics, "Unknown entitlement");
    }

    private static AccessDecisionResult Result(AccessDecision decision, EvaluationStatus status, IReadOnlyList<NormalizedEntitlement> entitlements, Guid principalId, IReadOnlyList<AccessEvaluationDiagnostic> diagnostics, string source)
    {
        return new(decision, status, entitlements.Any(item => item.PrincipalId == principalId && item.IsDirect), entitlements.Any(item => item.IsInherited), entitlements.Any(item => item.IsConditional), source, entitlements, entitlements.Select(item => item.Evidence).ToArray(), diagnostics);
    }

    private static HashSet<Guid> ResolvePrincipalClosure(Guid principalId, IReadOnlyList<GroupMembership> memberships)
    {
        var resolved = new HashSet<Guid> { principalId };
        var pending = new Stack<Guid>();
        pending.Push(principalId);
        while (pending.Count > 0)
        {
            var member = pending.Pop();
            foreach (var membership in memberships.Where(item => item.PrincipalId == member))
            {
                if (resolved.Add(membership.GroupId)) pending.Push(membership.GroupId);
            }
        }
        return resolved;
    }

    private static Guid StableId(Guid principalId, Guid resourceId, Guid permissionId) => GuidUtility.Create(GuidUtility.Namespace, $"effective|{principalId}|{resourceId}|{permissionId}");
}

public sealed class ScopeResolutionService : IScopeResolver
{
    public bool Applies(NormalizedEntitlement entitlement, CloudResource resource)
    {
        if (entitlement.ResourceId.HasValue) return entitlement.ResourceId == resource.Id;
        var scope = entitlement.Scope.OriginalScope.TrimEnd('/');
        var target = (resource.ResourceId ?? resource.Id.ToString()).TrimEnd('/');
        if (scope == "*" || scope.Equals("global", StringComparison.OrdinalIgnoreCase)) return true;
        if (scope.Equals(target, StringComparison.OrdinalIgnoreCase)) return true;
        return target.StartsWith(scope + "/", StringComparison.OrdinalIgnoreCase) || (entitlement.IsInherited && target.StartsWith(scope, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class AzureAccessEvaluator : IAccessEvaluator { public CloudProvider Provider => CloudProvider.Azure; public AccessDecisionResult Evaluate(AccessEvaluationContext context, AccessEvaluationRequest request) => throw new NotSupportedException("Azure evaluation is coordinated by EffectiveAccessEngine."); }
public sealed class AwsAccessEvaluator : IAccessEvaluator { public CloudProvider Provider => CloudProvider.Aws; public AccessDecisionResult Evaluate(AccessEvaluationContext context, AccessEvaluationRequest request) => throw new NotSupportedException("AWS evaluation is coordinated by EffectiveAccessEngine."); }
public sealed class GcpAccessEvaluator : IAccessEvaluator { public CloudProvider Provider => CloudProvider.Gcp; public AccessDecisionResult Evaluate(AccessEvaluationContext context, AccessEvaluationRequest request) => throw new NotSupportedException("GCP evaluation is coordinated by EffectiveAccessEngine."); }

internal static class GuidUtility
{
    public static readonly Guid Namespace = Guid.Parse("7c31cbf1-3b9f-4cc2-b9d2-0f3f0f02c6af");
    public static Guid Create(Guid namespaceId, string name)
    {
        var hash = System.Security.Cryptography.SHA1.HashData(namespaceId.ToByteArray().Concat(System.Text.Encoding.UTF8.GetBytes(name)).ToArray());
        var bytes = hash.Take(16).ToArray(); bytes[6] = (byte)((bytes[6] & 0x0f) | 0x50); bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80); return new Guid(bytes);
    }
}
