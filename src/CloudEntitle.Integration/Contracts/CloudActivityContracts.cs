using CloudEntitle.Domain;

namespace CloudEntitle.Integration.Contracts;

/// <summary>A scoped, normalized audit event without raw request bodies or credentials.</summary>
public sealed record CloudActivityEvent(string EventId, string Principal, string Action, string? PermissionAction,
    string? Resource, DateTimeOffset Timestamp, bool Success)
{
    public bool OutcomeKnown { get; init; } = true;
    public string? PermissionEvidenceSource { get; init; }
}

public enum CloudActivitySource { Default, AwsCloudTrailLake, AwsS3Archive, AzureLogAnalytics, AzureBlobArchive }

/// <summary>Client-selected source identity and content digest, not a cloud attestation.</summary>
public sealed record CloudActivityProvenance(string SourceId, string Sha256, int SourceRecords);

/// <summary>Saved snapshot metadata; partial activity evidence, not historical proof of entitlement use.</summary>
public sealed record CloudActivityHistoryEntry(Guid SnapshotId, DateTimeOffset ThroughUtc, string Source,
    string? SourceScope, bool Truncated, int EventCount, Guid UploadedBy);

/// <summary>A bounded page of saved snapshots with an organization/account-scoped exclusive cursor.</summary>
public sealed record CloudActivityHistoryPage(IReadOnlyList<CloudActivityHistoryEntry> Snapshots, Guid? NextBeforeSnapshotId);

/// <summary>A bounded read-only collection. Coverage remains partial for these audit sources.</summary>
public sealed record CloudActivitySnapshot(Guid SnapshotId, CloudProvider Provider, string AccountId,
    DateTimeOffset FromUtc, DateTimeOffset ThroughUtc, string Source, string CoverageDetails,
    bool Truncated, IReadOnlyList<CloudActivityEvent> Events)
{
    public string CoverageStatus => "Partial";
    public string? SourceScope { get; init; }
    public IReadOnlyList<CloudActivityProvenance> Provenance { get; init; } = Array.Empty<CloudActivityProvenance>();
}

/// <summary>Explicitly selected cloud account and regional CLI context for an on-demand read.</summary>
public sealed record CloudActivityQuery(CloudProvider Provider, string AccountId, string Region,
    string Profile, DateTimeOffset ThroughUtc)
{
    public CloudActivitySource SourceKind { get; init; }
    public string Destination { get; init; } = "";
    public string Container { get; init; } = "";
    public string Prefix { get; init; } = "";
}

/// <summary>Observed action frequency; absence of an action is not evidence of unused permission.</summary>
public sealed record CloudActivityActionUsage(string Principal, string Action, string? PermissionAction,
    DateTimeOffset? LastUsedAtUtc, int Successful30Days, int Successful60Days, int Successful90Days, int Failed90Days);

public interface ICloudActivityCollector
{
    Task<CloudActivitySnapshot> CollectAsync(CloudActivityQuery query, CancellationToken cancellationToken = default);
}

public static class CloudActivityAnalysis
{
    public static IReadOnlyList<CloudActivityActionUsage> Analyze(CloudActivitySnapshot snapshot)
        => snapshot.Events.DistinctBy(item => (item.EventId, item.Principal, item.Action, item.PermissionAction))
            .Where(item => item.Timestamp >= snapshot.ThroughUtc.AddDays(-90) && item.Timestamp <= snapshot.ThroughUtc)
            .GroupBy(item => (item.Principal, item.Action, item.PermissionAction))
            .Select(group =>
            {
                var successes = group.Where(item => item.Success && item.OutcomeKnown).ToArray();
                return new CloudActivityActionUsage(group.Key.Principal, group.Key.Action, group.Key.PermissionAction,
                    successes.Length == 0 ? null : successes.Max(item => item.Timestamp),
                    successes.Count(item => item.Timestamp >= snapshot.ThroughUtc.AddDays(-30)),
                    successes.Count(item => item.Timestamp >= snapshot.ThroughUtc.AddDays(-60)),
                    successes.Length, group.Count(item => !item.Success && item.OutcomeKnown));
            })
            .OrderByDescending(item => item.Successful90Days).ThenBy(item => item.Principal, StringComparer.Ordinal)
            .ThenBy(item => item.Action, StringComparer.Ordinal).ToArray();
}
