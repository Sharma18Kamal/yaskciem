using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;

namespace CloudEntitle.Integration.Services;

public sealed class HistoricalEntitlementAttributionService
{
    public HistoricalAttributionReport Correlate(CloudActivitySnapshot activity, IReadOnlyList<EntitlementEvidenceSnapshot> history)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(history);
        if (history.Any(snapshot => snapshot.Provider != activity.Provider || !Same(activity.Provider, snapshot.AccountId, activity.AccountId)))
            throw new ArgumentException("Historical evidence must belong to the exact provider/account.");
        var rows = activity.Events.DistinctBy(item => (item.EventId, item.Principal, item.PermissionAction, item.Resource))
            .Select(item =>
            {
                var prior = history.Where(snapshot => snapshot.CollectedAtUtc <= item.Timestamp).OrderByDescending(snapshot => snapshot.CollectedAtUtc).ToArray();
                var observation = prior.FirstOrDefault();
                var tied = observation is not null && prior.Count(snapshot => snapshot.CollectedAtUtc == observation.CollectedAtUtc) > 1;
                var permissionKnown = item.Success && item.OutcomeKnown && item.PermissionEvidenceSource ==
                    (activity.Provider == CloudProvider.Azure ? "Azure authorization.action" : activity.Provider == CloudProvider.Gcp ? "GCP authorizationInfo.permission" : null)
                    && activity.Provider != CloudProvider.Aws && !string.IsNullOrWhiteSpace(item.PermissionAction);
                var candidates = observation is null || !permissionKnown || tied ? [] : observation.Grants.Where(grant =>
                    grant.Effect == "Allow" && !grant.HasCondition && !grant.IsExclusion
                    && PrincipalMatches(activity.Provider, grant.Principal, item.Principal)
                    && Same(activity.Provider, grant.Action, item.PermissionAction)
                    && ResourceMatches(activity.Provider, grant.ResourcePattern, item.Resource)
                    && !observation.Grants.Any(block => block.SourceId == grant.SourceId
                        && block.Principal == grant.Principal && (block.IsExclusion || block.Effect == "Deny" || block.HasCondition)))
                    .ToArray();
                var status = !permissionKnown ? "NoSuccessfulPermissionEvidence"
                    : observation is null ? "NoPriorDocumentObservation"
                    : tied ? "AmbiguousObservationTime"
                    : candidates.Length == 0 ? "NoExactPriorObservedGrant"
                    : candidates.Length > 1 ? "AmbiguousPriorObservedGrants" : "CompatiblePriorObservedGrant";
                return new HistoricalGrantCorrelation(item.EventId, item.Principal, item.PermissionAction, item.Resource,
                    item.Timestamp, observation?.SnapshotId, observation?.CollectedAtUtc, status, candidates);
            }).ToArray();
        return new(rows, "Partial observed document history, not an authoritative validity interval or proof of historical grant use. Grants may have changed after observation; cross-source gaps, conditions, exclusions, deny controls and AWS event-name mappings are not inferred. No missing observation or compatible grant authorizes automatic removal.");
    }

    private static bool PrincipalMatches(CloudProvider provider, string grant, string principal)
        => Same(provider, grant, principal) || provider == CloudProvider.Gcp && grant.StartsWith("serviceAccount:", StringComparison.Ordinal)
            && Same(provider, grant["serviceAccount:".Length..], principal);
    private static bool ResourceMatches(CloudProvider provider, string scope, string? resource)
        => Same(provider, scope, resource) || provider == CloudProvider.Azure && resource is not null
            && resource.StartsWith(scope.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
    private static bool Same(CloudProvider provider, string? left, string? right)
        => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
            && left.Equals(right, provider == CloudProvider.Azure ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
