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
    string? LastChangedBy = null);

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
    IReadOnlyList<LiveScanAccountRecord> AccountDetails);

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
                if (entry is not null) entries.Add(entry);
            }
            catch (JsonException)
            {
            }
        }
        return entries;
    }

    private async Task WriteEntriesAsync(IReadOnlyList<LiveScanHistoryEntry> entries, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var lines = entries.Select(entry => JsonSerializer.Serialize(entry)).ToArray();
        await File.WriteAllLinesAsync(_filePath, lines, cancellationToken);
    }
}
