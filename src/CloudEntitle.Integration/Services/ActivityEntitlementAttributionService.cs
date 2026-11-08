using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Integration.Contracts;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;

namespace CloudEntitle.Integration.Services;

public sealed class ActivityEntitlementAttributionService
{
    public ActivityAttributionReport Correlate(CloudActivitySnapshot snapshot, CloudAccount account,
        IReadOnlyList<CloudPrincipal> principals, IReadOnlyList<CloudResource> resources,
        IReadOnlyList<CloudPermission> permissions, IReadOnlyList<DomainEffectiveAccess> effectiveAccess)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(principals);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(effectiveAccess);
        if (account.Id == Guid.Empty || account.Provider != snapshot.Provider
            || !Equal(snapshot.Provider, account.ExternalId, snapshot.AccountId))
            throw new ArgumentException("Attribution requires the exact provider/account inventory, not a display-name match.");
        var permissionById = permissions.Where(item => item.AccountId == account.Id).ToDictionary(item => item.Id);
        var observations = snapshot.Events.DistinctBy(item => (item.EventId, item.Principal, item.Action, item.PermissionAction, item.Resource))
            .Select(item =>
            {
                var matchedPrincipals = principals.Where(principal => principal.AccountId == account.Id
                    && (int)principal.CloudProvider == (int)snapshot.Provider
                    && (Equal(snapshot.Provider, principal.ExternalId, item.Principal)
                        || snapshot.Provider == CloudProvider.Gcp && Equal(snapshot.Provider, principal.Email, item.Principal))).ToArray();
                var matchedResources = resources.Where(resource => resource.AccountId == account.Id
                    && Equal(snapshot.Provider, resource.ExternalId, item.Resource)).ToArray();
                var principalId = matchedPrincipals.Length == 1 ? matchedPrincipals[0].Id : (Guid?)null;
                var resourceId = matchedResources.Length == 1 ? matchedResources[0].Id : (Guid?)null;
                var knownPermission = item.Success && item.OutcomeKnown && !string.IsNullOrWhiteSpace(item.PermissionAction)
                    && item.PermissionEvidenceSource is "GCP authorizationInfo.permission" or "Azure authorization.action";
                var candidates = principalId is null || resourceId is null || !knownPermission ? []
                    : effectiveAccess.Where(access => access.CloudAccountId == account.Id && access.Provider == snapshot.Provider
                        && access.PrincipalId == principalId && access.ResourceId == resourceId
                        && access.Decision == AccessDecision.Allow && access.EvaluationStatus == EvaluationStatus.FullyEvaluated
                        && !access.IsConditional
                        && permissionById.TryGetValue(access.PermissionId, out var permission)
                        && !permission.IsExclusion && permission.AccessType == AccessType.Allow
                        && Equal(snapshot.Provider, permission.Action, item.PermissionAction))
                    .Select(access => access.Id).Distinct().Order().ToArray();
                var status = !knownPermission ? "NoSuccessfulPermissionEvidence"
                    : principalId is null ? "PrincipalUnresolved"
                    : resourceId is null ? "ResourceUnresolved"
                    : candidates.Length == 0 ? "NoExactCurrentGrant"
                    : candidates.Length > 1 ? "AmbiguousCurrentGrants" : "CompatibleCurrentGrant";
                return new ActivityEntitlementCorrelation(item.EventId, item.Principal, item.Action, item.PermissionAction,
                    item.Resource, item.Timestamp, status, principalId, resourceId, candidates,
                    "Exact provider/account identity, resource and action correlation only. Wildcards, roles, inheritance, conditions and historical grant selection are not inferred.");
            }).ToArray();
        return new(observations, "Partial client-supplied evidence. Compatible current grants are not proven used grants; missing observations never prove unused. Historical entitlement evidence and complete coverage are still required before remediation.");
    }

    private static bool Equal(CloudProvider provider, string? left, string? right)
        => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
            && left.Equals(right, provider == CloudProvider.Azure ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
