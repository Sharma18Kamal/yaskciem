using System.Security.Cryptography;
using System.Text.Json;
using CloudEntitle.Integration.Contracts;
using Npgsql;

namespace CloudEntitle.LicenseServer.Services;

public sealed record SealedAuditSegment(
    Guid TenantId,
    long FirstSequence,
    long LastSequence,
    string PreviousHash,
    string LastHash,
    string Sha256,
    string ObjectKey,
    DateTime RetainUntilUtc,
    byte[] Payload);

public sealed record AuditArchiveProgress(long PendingEvents, DateTime? OldestPendingAtUtc, DateTime? LastSuccessfulArchiveAtUtc);

public interface IAuditSegmentArchive
{
    Task CheckReadinessAsync(CancellationToken cancellationToken);
    Task ArchiveAsync(SealedAuditSegment segment, CancellationToken cancellationToken);
}

public sealed class PostgresAuditSegmentArchiver : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _ownsDataSource;
    private readonly AuditIntegrityKeyRing _integrityKeys;
    private readonly bool _ownsIntegrityKeys;
    private readonly int _segmentEventCount;
    private readonly int _minimumRetentionDays;
    private readonly string _objectPrefix;

    public PostgresAuditSegmentArchiver(string connectionString, ReadOnlySpan<byte> integrityKey,
        int segmentEventCount, int minimumRetentionDays, string objectPrefix)
        : this(new NpgsqlDataSourceBuilder(connectionString).Build(), CreateLegacyKeyRing(integrityKey), segmentEventCount,
            minimumRetentionDays, objectPrefix, ownsDataSource: true, ownsIntegrityKeys: true)
    {
    }

    public PostgresAuditSegmentArchiver(NpgsqlDataSource dataSource, ReadOnlySpan<byte> integrityKey,
        int segmentEventCount, int minimumRetentionDays, string objectPrefix)
        : this(dataSource, CreateLegacyKeyRing(integrityKey), segmentEventCount, minimumRetentionDays, objectPrefix,
            ownsDataSource: false, ownsIntegrityKeys: true)
    {
    }

    public PostgresAuditSegmentArchiver(NpgsqlDataSource dataSource, AuditIntegrityKeyRing integrityKeys,
        int segmentEventCount, int minimumRetentionDays, string objectPrefix)
        : this(dataSource, integrityKeys, segmentEventCount, minimumRetentionDays, objectPrefix,
            ownsDataSource: false, ownsIntegrityKeys: false)
    {
    }

    private PostgresAuditSegmentArchiver(NpgsqlDataSource dataSource, AuditIntegrityKeyRing integrityKeys,
        int segmentEventCount, int minimumRetentionDays, string objectPrefix, bool ownsDataSource, bool ownsIntegrityKeys)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(integrityKeys);
        if (segmentEventCount is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(segmentEventCount));
        if (minimumRetentionDays is < 1 or > 36500) throw new ArgumentOutOfRangeException(nameof(minimumRetentionDays));
        if (string.IsNullOrWhiteSpace(objectPrefix) || objectPrefix.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("A valid archive object prefix is required.", nameof(objectPrefix));
        _dataSource = dataSource;
        _ownsDataSource = ownsDataSource;
        _integrityKeys = integrityKeys;
        _ownsIntegrityKeys = ownsIntegrityKeys;
        _segmentEventCount = segmentEventCount;
        _minimumRetentionDays = minimumRetentionDays;
        _objectPrefix = objectPrefix.Trim('/');
    }

    public async Task<bool> ArchiveNextAsync(IAuditSegmentArchive archive, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archive);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        Guid tenantId;
        await using (var candidate = new NpgsqlCommand("""
            SELECT h.tenant_id
            FROM ciem_audit_heads h
            LEFT JOIN LATERAL (
                SELECT last_sequence, archived_at_utc
                FROM ciem_audit_archive_segments
                WHERE tenant_id = h.tenant_id
                ORDER BY last_sequence DESC
                LIMIT 1
            ) p ON true
            WHERE h.last_sequence > COALESCE(p.last_sequence, 0)
            ORDER BY p.archived_at_utc NULLS FIRST, h.tenant_id
            LIMIT 1
            """, connection))
        {
            var result = await candidate.ExecuteScalarAsync(cancellationToken);
            if (result is null or DBNull) return false;
            tenantId = (Guid)result;
        }

        var lockAcquired = false;
        try
        {
            await using (var lockCommand = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtextextended(@tenant_id, 0))", connection))
            {
                lockCommand.Parameters.AddWithValue("tenant_id", tenantId.ToString("D"));
                lockAcquired = (bool)(await lockCommand.ExecuteScalarAsync(cancellationToken) ?? false);
            }
            if (!lockAcquired) return false;

            var progress = await GetProgressAsync(connection, tenantId, cancellationToken);
            var head = await GetHeadAsync(connection, tenantId, cancellationToken);
            if (head.Sequence <= progress.Sequence) return false;
            var firstSequence = progress.Sequence + 1;
            var lastSequence = Math.Min(firstSequence + _segmentEventCount - 1, head.Sequence);
            var entries = await ReadAndVerifySegmentAsync(connection, tenantId, firstSequence, lastSequence,
                progress.Hash, cancellationToken);
            if (lastSequence == head.Sequence && !FixedEquals(entries[^1].Hash, head.Hash))
                throw new InvalidDataException("The audit archive tail does not match the PostgreSQL tenant head.");
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                tenantId,
                firstSequence,
                lastSequence,
                previousHash = progress.Hash,
                lastHash = entries[^1].Hash,
                sealedAtUtc = entries[^1].RecordedAtUtc,
                events = entries
            }, JsonOptions);
            var sha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            var objectKey = $"{_objectPrefix}/{tenantId:N}/{firstSequence:D20}-{lastSequence:D20}-{sha256}.json";
            var segment = new SealedAuditSegment(tenantId, firstSequence, lastSequence, progress.Hash,
                entries[^1].Hash, sha256, objectKey, DateTime.UtcNow.AddDays(_minimumRetentionDays), payload);

            await archive.ArchiveAsync(segment, cancellationToken);
            await SaveArchiveProgressAsync(connection, segment, cancellationToken);
            return true;
        }
        finally
        {
            if (lockAcquired)
            {
                await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended(@tenant_id, 0))", connection);
                unlock.Parameters.AddWithValue("tenant_id", tenantId.ToString("D"));
                await unlock.ExecuteScalarAsync(CancellationToken.None);
            }
        }
    }

    public async Task<AuditArchiveProgress> GetArchiveProgressAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH latest_progress AS (
                SELECT tenant_id, MAX(last_sequence) AS last_sequence, MAX(archived_at_utc) AS last_archived_at_utc
                FROM ciem_audit_archive_segments
                GROUP BY tenant_id
            ), remaining AS (
                SELECT h.tenant_id, h.last_sequence, COALESCE(p.last_sequence, 0) AS archived_sequence
                FROM ciem_audit_heads h
                LEFT JOIN latest_progress p ON p.tenant_id = h.tenant_id
            )
            SELECT
                COALESCE((SELECT SUM(last_sequence - archived_sequence) FROM remaining), 0),
                (SELECT MIN((e.event_json::jsonb ->> 'recordedAtUtc')::timestamptz)
                 FROM ciem_audit_events e
                 JOIN remaining r ON r.tenant_id = e.tenant_id
                 WHERE e.sequence > r.archived_sequence),
                (SELECT MAX(last_archived_at_utc) FROM latest_progress)
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new AuditArchiveProgress(0, null, null);
        return new AuditArchiveProgress(reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetDateTime(1),
            reader.IsDBNull(2) ? null : reader.GetDateTime(2));
    }

    private async Task<(long Sequence, string Hash)> GetProgressAsync(NpgsqlConnection connection, Guid tenantId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT last_sequence, last_hash
            FROM ciem_audit_archive_segments
            WHERE tenant_id = @tenant_id
            ORDER BY last_sequence DESC
            LIMIT 1
            """, connection);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetInt64(0), reader.GetString(1))
            : (0, new string('0', 64));
    }

    private static async Task<(long Sequence, string Hash)> GetHeadAsync(NpgsqlConnection connection, Guid tenantId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT last_sequence, last_hash FROM ciem_audit_heads WHERE tenant_id = @tenant_id", connection);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidDataException("The audit tenant head is unavailable to the archiver.");
        return (reader.GetInt64(0), reader.GetString(1));
    }

    private async Task<List<CentralAuditEventDto>> ReadAndVerifySegmentAsync(NpgsqlConnection connection, Guid tenantId,
        long firstSequence, long lastSequence, string previousHash, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT sequence, event_json
            FROM ciem_audit_events
            WHERE tenant_id = @tenant_id AND sequence BETWEEN @first_sequence AND @last_sequence
            ORDER BY sequence
            """, connection);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("first_sequence", firstSequence);
        command.Parameters.AddWithValue("last_sequence", lastSequence);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var expectedCount = checked((int)(lastSequence - firstSequence + 1));
        var entries = new List<CentralAuditEventDto>(expectedCount);
        var expectedSequence = firstSequence;
        while (await reader.ReadAsync(cancellationToken))
        {
            var rowSequence = reader.GetInt64(0);
            CentralAuditEventDto entry;
            try
            {
                entry = JsonSerializer.Deserialize<CentralAuditEventDto>(reader.GetString(1), JsonOptions)
                    ?? throw new InvalidDataException("The audit archive source contains an invalid event.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The audit archive source contains invalid event JSON.", exception);
            }

            if (rowSequence != expectedSequence || entry.Sequence != expectedSequence || entry.TenantId != tenantId
                || entry.EventId == Guid.Empty || !string.Equals(entry.PreviousHash, previousHash, StringComparison.Ordinal)
                || !FixedEquals(entry.Hash, ComputeHash(entry)))
                throw new InvalidDataException("The audit archive source failed sequence or HMAC-chain verification.");

            entries.Add(entry);
            previousHash = entry.Hash;
            expectedSequence++;
        }

        if (entries.Count != expectedCount || expectedSequence - 1 != lastSequence)
            throw new InvalidDataException("The requested audit archive segment is incomplete.");
        return entries;
    }

    private async Task SaveArchiveProgressAsync(NpgsqlConnection connection, SealedAuditSegment segment,
        CancellationToken cancellationToken)
    {
        await using var segmentCommand = new NpgsqlCommand("""
            INSERT INTO ciem_audit_archive_segments
                (tenant_id, first_sequence, last_sequence, last_hash, sha256, object_key, retain_until_utc, archived_at_utc)
            VALUES (@tenant_id, @first_sequence, @last_sequence, @last_hash, @sha256, @object_key, @retain_until, @archived_at)
            """, connection);
        segmentCommand.Parameters.AddWithValue("tenant_id", segment.TenantId);
        segmentCommand.Parameters.AddWithValue("first_sequence", segment.FirstSequence);
        segmentCommand.Parameters.AddWithValue("last_sequence", segment.LastSequence);
        segmentCommand.Parameters.AddWithValue("last_hash", segment.LastHash);
        segmentCommand.Parameters.AddWithValue("sha256", segment.Sha256);
        segmentCommand.Parameters.AddWithValue("object_key", segment.ObjectKey);
        segmentCommand.Parameters.AddWithValue("retain_until", segment.RetainUntilUtc);
        segmentCommand.Parameters.AddWithValue("archived_at", DateTime.UtcNow);
        await segmentCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private string ComputeHash(CentralAuditEventDto entry)
    {
        var metadata = entry.Metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value);
        var payload = entry.IntegrityKeyId == "legacy"
            ? JsonSerializer.SerializeToUtf8Bytes(new
            {
                entry.Sequence,
                entry.EventId,
                entry.TenantId,
                entry.ActorObjectId,
                entry.EventType,
                entry.Description,
                entry.RecordedAtUtc,
                entry.SourceTimestampUtc,
                metadata,
                entry.PreviousHash,
                entry.RetainUntilUtc
            }, JsonOptions)
            : JsonSerializer.SerializeToUtf8Bytes(new
            {
                entry.Sequence,
                entry.EventId,
                entry.TenantId,
                entry.ActorObjectId,
                entry.EventType,
                entry.Description,
                entry.RecordedAtUtc,
                entry.SourceTimestampUtc,
                metadata,
                entry.PreviousHash,
                entry.RetainUntilUtc,
                entry.IntegrityKeyId
            }, JsonOptions);
        return Convert.ToHexString(HMACSHA256.HashData(_integrityKeys.GetKey(entry.IntegrityKeyId), payload)).ToLowerInvariant();
    }

    private static AuditIntegrityKeyRing CreateLegacyKeyRing(ReadOnlySpan<byte> integrityKey)
    {
        if (integrityKey.Length < 32) throw new ArgumentException("The audit integrity key must contain at least 32 bytes.", nameof(integrityKey));
        var key = integrityKey.ToArray();
        try { return new AuditIntegrityKeyRing("legacy", new Dictionary<string, byte[]> { ["legacy"] = key }); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static bool FixedEquals(string first, string second)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(first), Convert.FromHexString(second)); }
        catch (FormatException) { return false; }
    }

    public void Dispose()
    {
        if (_ownsIntegrityKeys) _integrityKeys.Dispose();
        if (_ownsDataSource) _dataSource.Dispose();
    }
}
