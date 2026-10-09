using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.Security.Services;

namespace CloudEntitle.LicenseServer.Services;

public interface IAuditRetentionStore
{
    Task<CentralAuditEventDto> AppendAsync(Guid tenantId, Guid actorObjectId, CentralAuditAppendRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CentralAuditEventDto>> ReadAsync(Guid tenantId, long afterSequence, int limit, CancellationToken cancellationToken = default);
    Task<AuditStoreReadiness> CheckReadinessAsync(CancellationToken cancellationToken = default);
}

public sealed record AuditStoreReadiness(bool Ready, bool ProductionReady, string Details);

public sealed class AppendOnlyAuditRetentionStore : IAuditRetentionStore, IDisposable
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> TenantLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _directory;
    private readonly int _minimumRetentionDays;
    private readonly byte[] _integrityKey;

    public AppendOnlyAuditRetentionStore(string directory, int minimumRetentionDays, ReadOnlySpan<byte> integrityKey)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("An audit retention directory is required.", nameof(directory));
        if (minimumRetentionDays is < 1 or > 36500) throw new ArgumentOutOfRangeException(nameof(minimumRetentionDays));
        if (integrityKey.Length < 32) throw new ArgumentException("The audit integrity key must contain at least 32 bytes.", nameof(integrityKey));
        _directory = Path.GetFullPath(directory);
        _minimumRetentionDays = minimumRetentionDays;
        _integrityKey = integrityKey.ToArray();
    }

    public async Task<CentralAuditEventDto> AppendAsync(Guid tenantId, Guid actorObjectId, CentralAuditAppendRequest request, CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty || actorObjectId == Guid.Empty) throw new ArgumentException("Authenticated tenant and actor IDs are required.");
        Validate(request);
        var path = GetTenantPath(tenantId);
        var gate = TenantLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var entries = await ReadAndVerifyAsync(tenantId, path, cancellationToken);
            var eventType = request.EventType.Trim();
            var description = SensitiveDataRedactor.RedactText(request.Description.Trim());
            var sourceTimestamp = request.SourceTimestampUtc?.ToUniversalTime();
            var metadata = SensitiveDataRedactor.RedactMetadata(request.Metadata)
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            if (request.EventId != Guid.Empty)
            {
                var duplicate = entries.FirstOrDefault(entry => entry.EventId == request.EventId);
                if (duplicate is not null)
                {
                    if (duplicate.EventType != eventType || duplicate.Description != description
                        || duplicate.SourceTimestampUtc != sourceTimestamp || !MetadataEquals(duplicate.Metadata, metadata))
                        throw new InvalidOperationException("The audit event ID is already assigned to different event content.");
                    return duplicate;
                }
            }
            var previousHash = entries.Count == 0 ? new string('0', 64) : entries[^1].Hash;
            var recordedAtUtc = DateTime.UtcNow;
            var entry = new CentralAuditEventDto(entries.Count == 0 ? 1 : entries[^1].Sequence + 1,
                request.EventId == Guid.Empty ? Guid.NewGuid() : request.EventId, tenantId, actorObjectId,
                eventType, description, recordedAtUtc, sourceTimestamp, metadata,
                previousHash, string.Empty, recordedAtUtc.AddDays(_minimumRetentionDays));
            entry = entry with { Hash = ComputeHash(entry) };

            Directory.CreateDirectory(_directory);
            var line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, JsonOptions) + "\n");
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
            await stream.WriteAsync(line, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
            CryptographicOperations.ZeroMemory(line);
            return entry;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<CentralAuditEventDto>> ReadAsync(Guid tenantId, long afterSequence, int limit, CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("An authenticated tenant ID is required.", nameof(tenantId));
        if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        var path = GetTenantPath(tenantId);
        var gate = TenantLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return (await ReadAndVerifyAsync(tenantId, path, cancellationToken))
                .Where(entry => entry.Sequence > afterSequence)
                .Take(limit)
                .ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<AuditStoreReadiness> CheckReadinessAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new AuditStoreReadiness(true, false,
            "Development file audit store is available but is not multi-instance safe or storage-immutable."));

    private string GetTenantPath(Guid tenantId) => Path.Combine(_directory, $"{tenantId:N}.audit.jsonl");

    private static void Validate(CentralAuditAppendRequest? request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.EventType) || request.EventType.Length > 120)
            throw new ArgumentException("Event type must contain 1 to 120 characters.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Description) || request.Description.Length > 4000)
            throw new ArgumentException("Description must contain 1 to 4000 characters.", nameof(request));
        if (request.Metadata is null || request.Metadata.Count > 50)
            throw new ArgumentException("Metadata must contain at most 50 entries.", nameof(request));
        if (request.Metadata.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 100 || pair.Value is null || pair.Value.Length > 2000))
            throw new ArgumentException("Metadata keys are limited to 100 characters and values to 2000 characters.", nameof(request));
    }

    private async Task<List<CentralAuditEventDto>> ReadAndVerifyAsync(Guid tenantId, string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return new List<CentralAuditEventDto>();
        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        var entries = new List<CentralAuditEventDto>(lines.Length);
        var previousHash = new string('0', 64);
        long expectedSequence = 1;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) throw new InvalidDataException("The central audit log contains an empty or truncated record.");
            CentralAuditEventDto entry;
            try
            {
                entry = JsonSerializer.Deserialize<CentralAuditEventDto>(line, JsonOptions)
                    ?? throw new InvalidDataException("The central audit log contains an invalid record.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The central audit log contains an invalid record; refusing to read or append.", exception);
            }
            if (entry.TenantId != tenantId || entry.EventId == Guid.Empty || entry.Sequence != expectedSequence
                || !string.Equals(entry.PreviousHash, previousHash, StringComparison.Ordinal)
                || !FixedEquals(entry.Hash, ComputeHash(entry)))
                throw new InvalidDataException("The central audit log hash chain is invalid; refusing to read or append.");
            entries.Add(entry);
            previousHash = entry.Hash;
            expectedSequence++;
        }
        return entries;
    }

    private string ComputeHash(CentralAuditEventDto entry)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            entry.Sequence,
            entry.EventId,
            entry.TenantId,
            entry.ActorObjectId,
            entry.EventType,
            entry.Description,
            entry.RecordedAtUtc,
            entry.SourceTimestampUtc,
            metadata = entry.Metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value),
            entry.PreviousHash,
            entry.RetainUntilUtc
        }, JsonOptions);
        return Convert.ToHexString(HMACSHA256.HashData(_integrityKey, payload)).ToLowerInvariant();
    }

    private static bool FixedEquals(string first, string second)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(first), Convert.FromHexString(second)); }
        catch (FormatException) { return false; }
    }

    private static bool MetadataEquals(IReadOnlyDictionary<string, string> first, IReadOnlyDictionary<string, string> second)
        => first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out var value) && value == pair.Value);

    public void Dispose() => CryptographicOperations.ZeroMemory(_integrityKey);
}
