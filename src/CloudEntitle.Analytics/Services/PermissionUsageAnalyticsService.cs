using CloudEntitle.Analytics.Models;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.RiskEngine.Models;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;
using AnalyticsPermissionUsageRecord = CloudEntitle.Analytics.Models.PermissionUsageRecord;

namespace CloudEntitle.Analytics.Services;

public sealed class PermissionUsageAnalyticsService
{
    private static readonly int[] ObservationWindows = [30, 60, 90];

    public PermissionUsageReport Calculate(
        IReadOnlyList<DomainEffectiveAccess> effectiveAccess,
        IReadOnlyList<CloudPermission> permissions,
        IReadOnlyList<ActivityRecord> activityRecords,
        IReadOnlyList<ActivityEvidenceCoverage> activityCoverage,
        DateTime? evaluatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(effectiveAccess);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(activityRecords);
        ArgumentNullException.ThrowIfNull(activityCoverage);

        var evaluatedAtUtc = (evaluatedAt ?? DateTime.UtcNow).ToUniversalTime();
        var permissionById = permissions.ToDictionary(permission => permission.Id);
        if (effectiveAccess.Any(access => access.Decision is AccessDecision.Allow or AccessDecision.Conditional
            && !permissionById.ContainsKey(access.PermissionId)))
            throw new ArgumentException("Every allowed or conditional entitlement needs its permission evidence.", nameof(permissions));
        var records = effectiveAccess
            .Where(access => access.Decision is AccessDecision.Allow or AccessDecision.Conditional)
            .Where(access => permissionById.ContainsKey(access.PermissionId))
            .GroupBy(access => (access.Provider, access.CloudAccountId, access.PrincipalId, access.PermissionId, access.ResourceId))
            .OrderBy(group => group.Key.Provider)
            .ThenBy(group => group.Key.CloudAccountId)
            .ThenBy(group => group.Key.PrincipalId)
            .ThenBy(group => group.Key.PermissionId)
            .ThenBy(group => group.Key.ResourceId)
            .Select(group => Analyze(group.ToArray(), permissionById[group.Key.PermissionId], activityRecords, activityCoverage, evaluatedAtUtc))
            .ToArray();

        var summaries = ObservationWindows.Select(days =>
        {
            var windowResults = records.SelectMany(record => record.Windows).Where(window => window.Days == days).ToArray();
            return new PermissionUsageWindowSummary(
                days,
                windowResults.Count(window => window.State == PermissionUsageState.Used),
                windowResults.Count(window => window.State == PermissionUsageState.Unused),
                windowResults.Count(window => window.State == PermissionUsageState.Unknown))
            {
                NotObserved = windowResults.Count(window => window.EvidenceState == PermissionUsageEvidenceState.NotObserved),
                InsufficientObservation = windowResults.Count(window => window.EvidenceState == PermissionUsageEvidenceState.InsufficientObservation),
                UnknownMapping = windowResults.Count(window => window.EvidenceState == PermissionUsageEvidenceState.UnknownMapping),
                CollectionIncomplete = windowResults.Count(window => window.EvidenceState == PermissionUsageEvidenceState.CollectionIncomplete)
            };
        }).ToArray();

        return new PermissionUsageReport(evaluatedAtUtc, records, summaries);
    }

    private static AnalyticsPermissionUsageRecord Analyze(
        IReadOnlyList<DomainEffectiveAccess> accessSet,
        CloudPermission permission,
        IReadOnlyList<ActivityRecord> activityRecords,
        IReadOnlyList<ActivityEvidenceCoverage> activityCoverage,
        DateTime evaluatedAtUtc)
    {
        var access = accessSet[0];
        var actionPattern = permission.Action ?? string.Empty;
        var attributedActions = string.IsNullOrWhiteSpace(actionPattern)
            ? Array.Empty<ActivityRecord>()
            : activityRecords
            .Where(activity => accessSet.Any(access => activity.CloudAccountId == access.CloudAccountId
                && activity.PrincipalId == access.PrincipalId
                && activity.ActionMappingStatusForUsage() == PermissionActionMappingStatus.ExactProviderEvidence
                && activity.Success
                && !string.IsNullOrWhiteSpace(activity.PermissionAction ?? activity.Action)
                && activity.Timestamp.ToUniversalTime() <= evaluatedAtUtc
                && (!activity.RoleId.HasValue || access.RoleId == activity.RoleId)
                && (!access.ResourceId.HasValue || activity.ResourceId == access.ResourceId)
                && ActivityUsageEvaluator.ActionMatches(access.Provider, actionPattern, activity.PermissionAction ?? activity.Action)))
            .OrderByDescending(activity => activity.Timestamp.ToUniversalTime())
            .ToArray();
        var candidateActions = string.IsNullOrWhiteSpace(actionPattern) ? Array.Empty<ActivityRecord>()
            : activityRecords.Where(activity => accessSet.Any(access => activity.CloudAccountId == access.CloudAccountId
                && activity.PrincipalId == access.PrincipalId && activity.Success
                && activity.PermissionMappingStatus == PermissionActionMappingStatus.CuratedCandidate
                && !string.IsNullOrWhiteSpace(activity.PermissionAction)
                && activity.Timestamp.ToUniversalTime() <= evaluatedAtUtc
                && (!access.ResourceId.HasValue || activity.ResourceId == access.ResourceId)
                && ActivityUsageEvaluator.ActionMatches(access.Provider, actionPattern, activity.PermissionAction)))
                .ToArray();
        var unknownMappingActions = string.IsNullOrWhiteSpace(actionPattern) ? Array.Empty<ActivityRecord>()
            : activityRecords.Where(activity => accessSet.Any(access => activity.CloudAccountId == access.CloudAccountId
                && activity.PrincipalId == access.PrincipalId && activity.Success
                && activity.PermissionMappingStatus == PermissionActionMappingStatus.Unknown
                && activity.Timestamp.ToUniversalTime() >= evaluatedAtUtc.AddDays(-90)
                && activity.Timestamp.ToUniversalTime() <= evaluatedAtUtc))
                .ToArray();

        var relevantCoverage = activityCoverage.Where(item => accessSet.Any(access =>
            item.CloudAccountId == access.CloudAccountId && item.Provider == access.Provider)).ToArray();
        var windows = ObservationWindows.Select(days =>
        {
            var windowStartUtc = evaluatedAtUtc.AddDays(-days);
            var matchingActions = attributedActions
                .Where(activity => activity.Timestamp.ToUniversalTime() >= windowStartUtc)
                .ToArray();
            var coverage = relevantCoverage.OrderByDescending(item => item.ObservedThroughUtc).FirstOrDefault();
            var complete = !string.IsNullOrWhiteSpace(actionPattern) && relevantCoverage.Any(item => ActivityUsageEvaluator.Covers(
                item, windowStartUtc, evaluatedAtUtc, requiresActions: true, requiresResourceIds: access.ResourceId.HasValue));
            var candidates = candidateActions.Count(activity => activity.Timestamp.ToUniversalTime() >= windowStartUtc
                && activity.Timestamp.ToUniversalTime() <= evaluatedAtUtc);
            var unknown = unknownMappingActions.Any(activity => activity.Timestamp.ToUniversalTime() >= windowStartUtc
                && activity.Timestamp.ToUniversalTime() <= evaluatedAtUtc);
            var evidenceState = matchingActions.Length > 0 ? PermissionUsageEvidenceState.Used
                : string.IsNullOrWhiteSpace(actionPattern) || candidates > 0 || unknown
                    ? PermissionUsageEvidenceState.UnknownMapping
                : coverage is null || coverage.ObservedFromUtc.ToUniversalTime() > windowStartUtc
                    ? PermissionUsageEvidenceState.InsufficientObservation
                : coverage.Status != ActivityCoverageStatus.Complete
                    ? PermissionUsageEvidenceState.CollectionIncomplete
                : PermissionUsageEvidenceState.NotObserved;
            var state = evidenceState switch
            {
                PermissionUsageEvidenceState.Used => PermissionUsageState.Used,
                PermissionUsageEvidenceState.NotObserved => PermissionUsageState.Unused,
                _ => PermissionUsageState.Unknown
            };

            return new PermissionUsageWindow(
                days,
                state,
                matchingActions.FirstOrDefault()?.Timestamp.ToUniversalTime(),
                matchingActions.Length)
            {
                EvidenceState = evidenceState,
                CandidateActionCount = candidates
            };
        }).ToArray();

        var ninetyDayStartUtc = evaluatedAtUtc.AddDays(-90);
        var frequencies = attributedActions
            .Where(activity => activity.Timestamp.ToUniversalTime() >= ninetyDayStartUtc)
            .GroupBy(activity => activity.Action, StringComparer.Ordinal)
            .Select(group => new PermissionActionFrequency(group.Key, group.Count()))
            .OrderByDescending(item => item.SuccessfulActionCount)
            .ThenBy(item => item.Action, StringComparer.Ordinal)
            .ToArray();

        return new AnalyticsPermissionUsageRecord(
            access.Id,
            access.CloudAccountId,
            access.Provider,
            access.PrincipalId,
            permission.Id,
            permission.Name,
            attributedActions.FirstOrDefault()?.Timestamp.ToUniversalTime(),
            windows,
            frequencies)
        {
            FirstUsedAtUtc = attributedActions.LastOrDefault()?.Timestamp.ToUniversalTime(),
            ResourceId = access.ResourceId,
            PermissionAction = permission.Action,
            EffectiveAccessIds = accessSet.Select(item => item.Id).ToArray(),
            CorrelationStatus = accessSet.Any(item => item.RoleId.HasValue) && attributedActions.Any(item => !item.RoleId.HasValue)
                ? "Permission use is correlated to identity/action/resource; provider event does not identify which role or policy granted it."
                : "Provider-aware exact permission mapping correlated to the identity and available resource evidence.",
            CandidateActionCount90Days = candidateActions.Count(item => item.Timestamp.ToUniversalTime() >= ninetyDayStartUtc)
        };
    }
}

internal static class ActivityRecordMappingExtensions
{
    public static PermissionActionMappingStatus ActionMappingStatusForUsage(this ActivityRecord activity)
        => activity.PermissionMappingStatus;
}
