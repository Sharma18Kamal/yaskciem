using CloudEntitle.Analytics.Models;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;

namespace CloudEntitle.Analytics.Services;

public sealed class PermissionUsageTrendService
{
    public IReadOnlyList<PermissionUsageTrend> Calculate(IEnumerable<CloudActivityEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var result = new List<PermissionUsageTrend>();
        foreach (var item in events)
        {
            var timestamp = item.EventTimestamp.ToUniversalTime();
            var daily = new DateTimeOffset(timestamp.Year, timestamp.Month, timestamp.Day, 0, 0, 0, TimeSpan.Zero);
            var monday = daily.AddDays(-(((int)daily.DayOfWeek + 6) % 7));
            var monthly = new DateTimeOffset(timestamp.Year, timestamp.Month, 1, 0, 0, 0, TimeSpan.Zero);
            var providerIdentity = item.IdentityId;
            result.Add(new(daily.UtcDateTime, daily.AddDays(1).UtcDateTime, "Daily", item.Provider, item.CloudAccountId,
                providerIdentity, item.PermissionMappingStatus == PermissionActionMappingStatus.ExactProviderEvidence ? 1 : 0,
                item.PermissionMappingStatus == PermissionActionMappingStatus.CuratedCandidate ? 1 : 0,
                item.PermissionMappingStatus == PermissionActionMappingStatus.Unknown ? 1 : 0));
            result.Add(new(monday.UtcDateTime, monday.AddDays(7).UtcDateTime, "Weekly", item.Provider, item.CloudAccountId,
                providerIdentity, item.PermissionMappingStatus == PermissionActionMappingStatus.ExactProviderEvidence ? 1 : 0,
                item.PermissionMappingStatus == PermissionActionMappingStatus.CuratedCandidate ? 1 : 0,
                item.PermissionMappingStatus == PermissionActionMappingStatus.Unknown ? 1 : 0));
            result.Add(new(monthly.UtcDateTime, monthly.AddMonths(1).UtcDateTime, "Monthly", item.Provider, item.CloudAccountId,
                providerIdentity, item.PermissionMappingStatus == PermissionActionMappingStatus.ExactProviderEvidence ? 1 : 0,
                item.PermissionMappingStatus == PermissionActionMappingStatus.CuratedCandidate ? 1 : 0,
                item.PermissionMappingStatus == PermissionActionMappingStatus.Unknown ? 1 : 0));
        }
        return result.GroupBy(item => (item.BucketStartUtc, item.BucketEndUtc, item.BucketType, item.Provider,
                item.CloudAccountId, item.IdentityId))
            .Select(group => group.First() with
            {
                ObservedActionCount = group.Sum(item => item.ObservedActionCount),
                CandidateActionCount = group.Sum(item => item.CandidateActionCount),
                UnknownMappingCount = group.Sum(item => item.UnknownMappingCount)
            })
            .OrderByDescending(item => item.BucketStartUtc)
            .ThenBy(item => item.Provider)
            .ThenBy(item => item.IdentityId)
            .ToArray();
    }
}
