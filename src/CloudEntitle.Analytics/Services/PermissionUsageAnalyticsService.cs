using CloudEntitle.Analytics.Models;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.RiskEngine.Models;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;

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
        var records = effectiveAccess
            .Where(access => access.Decision is AccessDecision.Allow or AccessDecision.Conditional)
            .Where(access => permissionById.ContainsKey(access.PermissionId))
            .OrderBy(access => access.Provider)
            .ThenBy(access => access.CloudAccountId)
            .ThenBy(access => access.PrincipalId)
            .ThenBy(access => access.PermissionId)
            .ThenBy(access => access.Id)
            .Select(access => Analyze(access, permissionById[access.PermissionId], activityRecords, activityCoverage, evaluatedAtUtc))
            .ToArray();

        var summaries = ObservationWindows.Select(days =>
        {
            var windowResults = records.SelectMany(record => record.Windows).Where(window => window.Days == days).ToArray();
            return new PermissionUsageWindowSummary(
                days,
                windowResults.Count(window => window.State == PermissionUsageState.Used),
                windowResults.Count(window => window.State == PermissionUsageState.Unused),
                windowResults.Count(window => window.State == PermissionUsageState.Unknown));
        }).ToArray();

        return new PermissionUsageReport(evaluatedAtUtc, records, summaries);
    }

    private static PermissionUsageRecord Analyze(
        DomainEffectiveAccess access,
        CloudPermission permission,
        IReadOnlyList<ActivityRecord> activityRecords,
        IReadOnlyList<ActivityEvidenceCoverage> activityCoverage,
        DateTime evaluatedAtUtc)
    {
        var actionPattern = permission.Action ?? string.Empty;
        var attributedActions = string.IsNullOrWhiteSpace(actionPattern)
            ? Array.Empty<ActivityRecord>()
            : activityRecords
            .Where(activity => activity.CloudAccountId == access.CloudAccountId
                && activity.PrincipalId == access.PrincipalId
                && activity.Success
                && !string.IsNullOrWhiteSpace(activity.Action)
                && activity.Timestamp.ToUniversalTime() <= evaluatedAtUtc
                && (!access.RoleId.HasValue || activity.RoleId == access.RoleId)
                && (!access.ResourceId.HasValue || activity.ResourceId == access.ResourceId)
                && ActivityUsageEvaluator.ActionMatches(access.Provider, actionPattern, activity.Action))
            .OrderByDescending(activity => activity.Timestamp.ToUniversalTime())
            .ToArray();

        var coverage = activityCoverage.FirstOrDefault(item =>
            item.CloudAccountId == access.CloudAccountId && item.Provider == access.Provider);
        var windows = ObservationWindows.Select(days =>
        {
            var windowStartUtc = evaluatedAtUtc.AddDays(-days);
            var matchingActions = attributedActions
                .Where(activity => activity.Timestamp.ToUniversalTime() >= windowStartUtc)
                .ToArray();
            var complete = ActivityUsageEvaluator.Covers(
                coverage,
                windowStartUtc,
                evaluatedAtUtc,
                requiresActions: true,
                requiresResourceIds: access.ResourceId.HasValue,
                requiresRoleIds: access.RoleId.HasValue);
            var state = matchingActions.Length > 0
                ? PermissionUsageState.Used
                : complete ? PermissionUsageState.Unused : PermissionUsageState.Unknown;

            return new PermissionUsageWindow(
                days,
                state,
                matchingActions.FirstOrDefault()?.Timestamp.ToUniversalTime(),
                matchingActions.Length);
        }).ToArray();

        var ninetyDayStartUtc = evaluatedAtUtc.AddDays(-90);
        var frequencies = attributedActions
            .Where(activity => activity.Timestamp.ToUniversalTime() >= ninetyDayStartUtc)
            .GroupBy(activity => activity.Action, StringComparer.Ordinal)
            .Select(group => new PermissionActionFrequency(group.Key, group.Count()))
            .OrderByDescending(item => item.SuccessfulActionCount)
            .ThenBy(item => item.Action, StringComparer.Ordinal)
            .ToArray();

        return new PermissionUsageRecord(
            access.Id,
            access.CloudAccountId,
            access.Provider,
            access.PrincipalId,
            permission.Id,
            permission.Name,
            attributedActions.FirstOrDefault()?.Timestamp.ToUniversalTime(),
            windows,
            frequencies);
    }
}
