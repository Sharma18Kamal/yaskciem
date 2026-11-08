using CloudEntitle.Integration.Contracts;

namespace CloudEntitle.Integration.Services;

public sealed class IdentityBehaviorAnalysisService
{
    public IReadOnlyList<IdentityBehaviorAnomaly> Analyze(CloudActivitySnapshot current, IReadOnlyList<CloudActivitySnapshot> baseline,
        IdentityBehaviorPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.PrivilegedActions is null || policy.PrivilegedActions.Count > 256 || policy.PrivilegedActions.Any(string.IsNullOrWhiteSpace)
            || policy.PermissionChangeActions is null || policy.PermissionChangeActions.Count > 256 || policy.PermissionChangeActions.Any(string.IsNullOrWhiteSpace)
            || policy.PermissionChangeBurstThreshold is < 2 or > 10_000
            || policy.BusinessStartHour is < 0 or > 23 || policy.BusinessEndHour is < 1 or > 24 || policy.BusinessEndHour <= policy.BusinessStartHour
            || policy.MinimumBaselineDays is < 1 or > 90 || policy.MinimumBaselineEvents is < 1 or > 20_000)
            throw new ArgumentException("Configure exact privileged actions, valid business hours and bounded baseline thresholds.");
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(policy.TimeZoneId); }
        catch (TimeZoneNotFoundException) { throw new ArgumentException("Configure a valid business-hours time zone."); }
        catch (InvalidTimeZoneException) { throw new ArgumentException("The configured business-hours time zone is invalid."); }
        if (baseline.Any(item => item.Provider != current.Provider || item.AccountId != current.AccountId
            || item.SnapshotId == current.SnapshotId || item.ThroughUtc > current.ThroughUtc))
            throw new ArgumentException("Behavior baselines must be prior observations for the exact account.");
        var privilegedActions = policy.PrivilegedActions.ToHashSet(StringComparer.Ordinal);
        var permissionChanges = policy.PermissionChangeActions.ToHashSet(StringComparer.Ordinal);
        var observed = baseline.SelectMany(item => item.Events).Concat(current.Events)
            .Where(item => item.Success && item.OutcomeKnown && item.Timestamp >= current.ThroughUtc.AddDays(-90) && item.Timestamp <= current.ThroughUtc)
            .DistinctBy(item => (item.EventId, item.Principal, item.Action))
            .GroupBy(item => item.Principal).ToDictionary(group => group.Key, group => group.OrderBy(item => item.Timestamp).ToArray(), StringComparer.Ordinal);
        var output = new List<IdentityBehaviorAnomaly>();
        foreach (var group in current.Events.DistinctBy(item => (item.EventId, item.Principal, item.Action))
            .Where(item => item.Success && item.OutcomeKnown && item.Principal is not ("Unattributed" or "Unknown")
                && !string.IsNullOrWhiteSpace(item.Principal) && item.Timestamp >= current.FromUtc && item.Timestamp <= current.ThroughUtc)
            .GroupBy(item => item.Principal))
        {
            var timeline = observed[group.Key];
            var index = 0;
            var priorIds = new HashSet<string>(StringComparer.Ordinal);
            var priorActions = new HashSet<string>(StringComparer.Ordinal);
            var countryIds = new HashSet<string>(StringComparer.Ordinal);
            var countries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var changeIds = new HashSet<string>(StringComparer.Ordinal);
            var recentChanges = new Queue<DateTimeOffset>();
            foreach (var item in group.OrderBy(item => item.Timestamp))
            {
                while (index < timeline.Length && timeline[index].Timestamp < item.Timestamp)
                {
                    var old = timeline[index++];
                    priorIds.Add(old.EventId);
                    priorActions.Add(old.PermissionAction ?? old.Action);
                    if (old.CountryCode is { Length: 2 } oldCountry && oldCountry.All(char.IsAsciiLetter))
                    {
                        countryIds.Add(old.EventId);
                        countries.Add(oldCountry);
                    }
                    if (permissionChanges.Contains(old.PermissionAction ?? old.Action) && changeIds.Add(old.EventId))
                        recentChanges.Enqueue(old.Timestamp);
                }
                while (recentChanges.TryPeek(out var time) && time < item.Timestamp.AddHours(-1)) recentChanges.Dequeue();
                var trained = priorIds.Count >= policy.MinimumBaselineEvents && item.Timestamp - timeline[0].Timestamp >= TimeSpan.FromDays(policy.MinimumBaselineDays);
                var action = item.PermissionAction ?? item.Action;
                var privileged = privilegedActions.Contains(action);
                if (trained && privileged && !priorActions.Contains(action))
                    Add("FirstObservedPrivilegedAction", $"Exact configured privileged action {action} was not present in this principal's prior observed baseline.");
                if (trained && item.CountryCode is { Length: 2 } country && country.All(char.IsAsciiLetter)
                    && countryIds.Count >= policy.MinimumBaselineEvents && !countries.Contains(country))
                    Add("UnusualObservedCountry", $"Source-supplied country {country.ToUpperInvariant()} differs from the prior observed baseline. No IP geolocation was inferred.");
                var changeCount = recentChanges.Count + (changeIds.Contains(item.EventId) ? 0 : 1);
                if (trained && permissionChanges.Contains(action) && changeCount >= policy.PermissionChangeBurstThreshold)
                    Add("ObservedPermissionChangeBurst", $"At least {changeCount} distinct successful configured permission-change calls occurred within one hour, reaching the client threshold {policy.PermissionChangeBurstThreshold}. This does not prove effective privilege escalation.");
                var local = TimeZoneInfo.ConvertTime(item.Timestamp, zone);
                if (privileged && (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || local.Hour < policy.BusinessStartHour || local.Hour >= policy.BusinessEndHour))
                    Add("PrivilegedActionOutsideBusinessHours", $"Configured privileged action occurred at {local:O} in {zone.Id}, outside weekday {policy.BusinessStartHour:00}:00-{policy.BusinessEndHour:00}:00.");
                void Add(string kind, string evidence)
                {
                    if (output.Count >= 2000) throw new InvalidOperationException("Behavior analysis exceeds 2,000 review signals. Narrow saved evidence; no truncated successful report was produced.");
                    output.Add(new(item.EventId, item.Principal, kind, item.Timestamp,
                        evidence + " Potential review signal only; partial sources do not establish first-ever use, malicious intent or complete history."));
                }
            }
        }
        return output;
    }
}
