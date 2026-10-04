using System.IO;
using System.Text.Json;
using CloudEntitle.Application.Interfaces;

namespace CloudEntitle.Desktop.Services;

public sealed record LiveEffectiveAccessRecord(
    string Provider,
    string Principal,
    string Role,
    string Scope,
    string Source,
    bool IsPrivileged,
    string AssignmentType = "Assigned",
    DateTime? LastUsedAt = null,
    DateTime? LastChangedAt = null,
    string? LastChangedBy = null,
    string? AccountId = null);

public enum PostureChangeType { Added, Removed, Changed }

public sealed record LiveRiskFindingSnapshot(
    string Provider,
    string Fingerprint,
    string Severity,
    string Title,
    string Principal,
    string Resource,
    string Recommendation);

public sealed record PostureChangeRecord(
    string Provider,
    PostureChangeType Type,
    string EntityType,
    string EntityKey,
    string Severity,
    string Summary,
    string PreviousState,
    string CurrentState,
    DateTime DetectedAt,
    bool AlertEligible);

public sealed record PostureTrendRecord(
    DateTime CompletedAt,
    string Provider,
    int Accounts,
    int Identities,
    int Resources,
    int Roles,
    int Permissions,
    int Findings,
    int CriticalFindings,
    int HighFindings,
    int EffectiveAccess,
    int Added,
    int Removed,
    int Changed);

public sealed record LiveScanHistoryEntry(
    DateTime StartedAt,
    DateTime CompletedAt,
    string Provider,
    int Accounts,
    int Identities,
    int Resources,
    int Roles,
    int Permissions,
    int Findings,
    IReadOnlyList<LiveEffectiveAccessRecord> EffectiveAccess,
    IReadOnlyList<LiveScanAccountRecord> AccountDetails)
{
    public IReadOnlyList<string> ScannedAccountKeys { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ScanCoverageRecord> Coverage { get; init; } = Array.Empty<ScanCoverageRecord>();
    public IReadOnlyList<KubernetesWorkloadTrace> WorkloadTraces { get; init; } = Array.Empty<KubernetesWorkloadTrace>();
    public IReadOnlyList<LiveRiskFindingSnapshot> RiskFindingDetails { get; init; } = Array.Empty<LiveRiskFindingSnapshot>();
    public IReadOnlyList<PostureChangeRecord> PostureChanges { get; init; } = Array.Empty<PostureChangeRecord>();
    public IReadOnlyList<PostureChangeRecord> PendingNotifications { get; init; } = Array.Empty<PostureChangeRecord>();
    public PostureTrendRecord? PostureTrend { get; init; }
    public string NotificationDelivery { get; init; } = "Not attempted";
}

public interface ILiveScanHistoryStore
{
    Task RecordAsync(LiveScanHistoryEntry entry, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LiveScanHistoryEntry>> GetRecentAsync(CancellationToken cancellationToken = default);
}

public sealed class LocalLiveScanHistoryStore : ILiveScanHistoryStore
{
    private static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(7);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string _filePath;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public LocalLiveScanHistoryStore(string? filePath = null, Func<DateTime>? utcNow = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YASK-CIEM",
            "scan-history",
            "scans.jsonl");
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public async Task RecordAsync(LiveScanHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var entries = await ReadEntriesAsync(cancellationToken);
            var cutoff = _utcNow().ToUniversalTime() - RetentionPeriod;
            entries = entries.Where(item => item.CompletedAt.ToUniversalTime() >= cutoff).ToList();
            if (entry.CompletedAt.ToUniversalTime() >= cutoff)
            {
                entries.Add(entry);
            }
            await WriteEntriesAsync(entries, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<LiveScanHistoryEntry>> GetRecentAsync(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var entries = await ReadEntriesAsync(cancellationToken);
            var cutoff = _utcNow().ToUniversalTime() - RetentionPeriod;
            var recent = entries.Where(item => item.CompletedAt.ToUniversalTime() >= cutoff)
                .OrderByDescending(item => item.StartedAt)
                .ToList();
            if (recent.Count != entries.Count)
            {
                await WriteEntriesAsync(recent, cancellationToken);
            }
            return recent;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<List<LiveScanHistoryEntry>> ReadEntriesAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath)) return new List<LiveScanHistoryEntry>();

        var entries = new List<LiveScanHistoryEntry>();
        foreach (var line in await File.ReadAllLinesAsync(_filePath, cancellationToken))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<LiveScanHistoryEntry>(line, JsonOptions);
                if (entry is not null) entries.Add(NormalizeEntry(entry));
            }
            catch (JsonException)
            {
            }
        }
        return entries;
    }

    private static LiveScanHistoryEntry NormalizeEntry(LiveScanHistoryEntry entry) => entry with
    {
        ScannedAccountKeys = entry.ScannedAccountKeys ?? Array.Empty<string>(),
        EffectiveAccess = entry.EffectiveAccess ?? Array.Empty<LiveEffectiveAccessRecord>(),
        AccountDetails = entry.AccountDetails ?? Array.Empty<LiveScanAccountRecord>(),
        Coverage = entry.Coverage ?? Array.Empty<ScanCoverageRecord>(),
        WorkloadTraces = entry.WorkloadTraces ?? Array.Empty<KubernetesWorkloadTrace>(),
        RiskFindingDetails = entry.RiskFindingDetails ?? Array.Empty<LiveRiskFindingSnapshot>(),
        PostureChanges = entry.PostureChanges ?? Array.Empty<PostureChangeRecord>(),
        PendingNotifications = entry.PendingNotifications ?? Array.Empty<PostureChangeRecord>(),
        NotificationDelivery = entry.NotificationDelivery ?? "Legacy scan; no notification delivery status."
    };

    private async Task WriteEntriesAsync(IReadOnlyList<LiveScanHistoryEntry> entries, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var lines = entries.Select(entry => JsonSerializer.Serialize(entry)).ToArray();
        await File.WriteAllLinesAsync(_filePath, lines, cancellationToken);
    }
}
