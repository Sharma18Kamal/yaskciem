using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.Security.Services;
using Npgsql;

namespace CloudEntitle.LicenseServer.Services;

public sealed class PostgresAuditRetentionStore : IAuditRetentionStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _ownsDataSource;
    private readonly int _minimumRetentionDays;
    private readonly AuditIntegrityKeyRing _integrityKeys;
    private readonly bool _ownsIntegrityKeys;

    public PostgresAuditRetentionStore(string connectionString, int minimumRetentionDays, ReadOnlySpan<byte> integrityKey)
        : this(new NpgsqlDataSourceBuilder(connectionString).Build(), minimumRetentionDays,
            CreateLegacyKeyRing(integrityKey), ownsDataSource: true, ownsIntegrityKeys: true)
    {
    }

    public PostgresAuditRetentionStore(NpgsqlDataSource dataSource, int minimumRetentionDays, ReadOnlySpan<byte> integrityKey)
        : this(dataSource, minimumRetentionDays, CreateLegacyKeyRing(integrityKey), ownsDataSource: false, ownsIntegrityKeys: true)
    {
    }

    public PostgresAuditRetentionStore(NpgsqlDataSource dataSource, int minimumRetentionDays, AuditIntegrityKeyRing integrityKeys)
        : this(dataSource, minimumRetentionDays, integrityKeys, ownsDataSource: false, ownsIntegrityKeys: false)
    {
    }

    private PostgresAuditRetentionStore(NpgsqlDataSource dataSource, int minimumRetentionDays, AuditIntegrityKeyRing integrityKeys,
        bool ownsDataSource, bool ownsIntegrityKeys)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        if (minimumRetentionDays is < 1 or > 36500) throw new ArgumentOutOfRangeException(nameof(minimumRetentionDays));
        ArgumentNullException.ThrowIfNull(integrityKeys);
        _dataSource = dataSource;
        _ownsDataSource = ownsDataSource;
        _minimumRetentionDays = minimumRetentionDays;
        _integrityKeys = integrityKeys;
        _ownsIntegrityKeys = ownsIntegrityKeys;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        const string createHeads = """
            CREATE TABLE IF NOT EXISTS ciem_audit_heads (
                tenant_id uuid PRIMARY KEY,
                last_sequence bigint NOT NULL CHECK (last_sequence >= 0),
                last_hash text NOT NULL
            )
            """;
        const string createEvents = """
            CREATE TABLE IF NOT EXISTS ciem_audit_events (
                tenant_id uuid NOT NULL,
                sequence bigint NOT NULL CHECK (sequence > 0),
                event_id uuid NOT NULL,
                event_json text NOT NULL,
                PRIMARY KEY (tenant_id, sequence),
                UNIQUE (tenant_id, event_id)
            )
            """;
        const string protectEvents = """
            CREATE OR REPLACE FUNCTION yask_reject_audit_event_mutation() RETURNS trigger
            LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'Audit events are append-only';
            END;
            $$;
            DROP TRIGGER IF EXISTS TR_CiemAuditEvents_AppendOnly ON ciem_audit_events;
            CREATE TRIGGER TR_CiemAuditEvents_AppendOnly
            BEFORE UPDATE OR DELETE ON ciem_audit_events
            FOR EACH ROW EXECUTE FUNCTION yask_reject_audit_event_mutation()
            """;
        const string isolateByTenant = """
            ALTER TABLE ciem_audit_heads ENABLE ROW LEVEL SECURITY;
            ALTER TABLE ciem_audit_heads FORCE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS ciem_audit_heads_tenant_isolation ON ciem_audit_heads;
            CREATE POLICY ciem_audit_heads_tenant_isolation ON ciem_audit_heads
                USING (tenant_id::text = current_setting('app.tenant_id', true))
                WITH CHECK (tenant_id::text = current_setting('app.tenant_id', true));
            ALTER TABLE ciem_audit_events ENABLE ROW LEVEL SECURITY;
            ALTER TABLE ciem_audit_events FORCE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS ciem_audit_events_tenant_isolation ON ciem_audit_events;
            CREATE POLICY ciem_audit_events_tenant_isolation ON ciem_audit_events
                USING (tenant_id::text = current_setting('app.tenant_id', true))
                WITH CHECK (tenant_id::text = current_setting('app.tenant_id', true))
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand(createHeads, connection))
            await command.ExecuteNonQueryAsync(cancellationToken);
        await using (var command = new NpgsqlCommand(createEvents, connection))
            await command.ExecuteNonQueryAsync(cancellationToken);
        await using (var command = new NpgsqlCommand(protectEvents, connection))
            await command.ExecuteNonQueryAsync(cancellationToken);
        await using (var command = new NpgsqlCommand(isolateByTenant, connection))
            await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AuditStoreReadiness> CheckReadinessAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("""
                     SELECT to_regclass('ciem_audit_heads') IS NOT NULL
                         AND to_regclass('ciem_audit_events') IS NOT NULL
                         AND EXISTS (
                              SELECT 1 FROM pg_trigger
                              WHERE tgrelid = to_regclass('ciem_audit_events')
                                 AND tgname = 'tr_ciemauditevents_appendonly'
                                 AND tgenabled IN ('O', 'A')
                         )
                        AND EXISTS (
                            SELECT 1 FROM pg_class
                            WHERE oid = to_regclass('ciem_audit_heads')
                              AND relrowsecurity AND relforcerowsecurity
                        )
                        AND EXISTS (
                            SELECT 1 FROM pg_class
                            WHERE oid = to_regclass('ciem_audit_events')
                              AND relrowsecurity AND relforcerowsecurity
                        )
                                                 AND EXISTS (
                                                            SELECT 1 FROM pg_policies
                                                            WHERE schemaname = current_schema()
                                                                AND tablename = 'ciem_audit_heads'
                                                                AND policyname = 'ciem_audit_heads_tenant_isolation'
                                                 )
                                                 AND EXISTS (
                                                            SELECT 1 FROM pg_policies
                                                            WHERE schemaname = current_schema()
                                                                AND tablename = 'ciem_audit_events'
                                                                AND policyname = 'ciem_audit_events_tenant_isolation'
                                                 )
                """, connection);
            var schemaReady = (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
            return schemaReady
                ? new AuditStoreReadiness(true, true, "PostgreSQL audit schema is available; application writes use a tenant-scoped HMAC chain.")
                : new AuditStoreReadiness(false, false, "PostgreSQL audit schema is not initialized.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return new AuditStoreReadiness(false, false,
                $"PostgreSQL audit readiness check failed: {SensitiveDataRedactor.RedactText(exception.Message)}");
        }
    }

    public async Task<CentralAuditEventDto> AppendAsync(Guid tenantId, Guid actorObjectId, CentralAuditAppendRequest request,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty || actorObjectId == Guid.Empty) throw new ArgumentException("Authenticated tenant and actor IDs are required.");
        Validate(request);
        var readiness = await CheckReadinessAsync(cancellationToken);
        if (!readiness.ProductionReady) throw new InvalidOperationException(readiness.Details);

        var eventType = request.EventType.Trim();
        var description = SensitiveDataRedactor.RedactText(request.Description.Trim());
        var sourceTimestamp = request.SourceTimestampUtc?.ToUniversalTime();
        var metadata = SensitiveDataRedactor.RedactMetadata(request.Metadata)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetTenantContextAsync(connection, transaction, tenantId, cancellationToken);
        await using (var ensureHead = new NpgsqlCommand("""
            INSERT INTO ciem_audit_heads (tenant_id, last_sequence, last_hash)
            VALUES (@tenant_id, 0, @zero_hash)
            ON CONFLICT (tenant_id) DO NOTHING
            """, connection, transaction))
        {
            ensureHead.Parameters.AddWithValue("tenant_id", tenantId);
            ensureHead.Parameters.AddWithValue("zero_hash", new string('0', 64));
            await ensureHead.ExecuteNonQueryAsync(cancellationToken);
        }

        long lastSequence;
        string lastHash;
        await using (var readHead = new NpgsqlCommand("""
            SELECT last_sequence, last_hash
            FROM ciem_audit_heads
            WHERE tenant_id = @tenant_id
            FOR UPDATE
            """, connection, transaction))
        {
            readHead.Parameters.AddWithValue("tenant_id", tenantId);
            await using var reader = await readHead.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidDataException("The PostgreSQL audit tenant head is missing.");
            lastSequence = reader.GetInt64(0);
            lastHash = reader.GetString(1);
        }

        var verifiedEvents = await ReadAndVerifyAsync(connection, transaction, tenantId, lastSequence, lastHash, cancellationToken);
        if (request.EventId != Guid.Empty)
        {
            var duplicate = verifiedEvents.FirstOrDefault(entry => entry.EventId == request.EventId);
            if (duplicate is not null)
            {
                if (duplicate.EventType != eventType || duplicate.Description != description
                    || duplicate.SourceTimestampUtc != sourceTimestamp || !MetadataEquals(duplicate.Metadata, metadata))
                    throw new InvalidOperationException("The audit event ID is already assigned to different event content.");
                await transaction.CommitAsync(cancellationToken);
                return duplicate;
            }
        }

        var recordedAtUtc = DateTime.UtcNow;
        var entry = new CentralAuditEventDto(lastSequence + 1,
            request.EventId == Guid.Empty ? Guid.NewGuid() : request.EventId, tenantId, actorObjectId,
            eventType, description, recordedAtUtc, sourceTimestamp, metadata,
            lastHash, string.Empty, recordedAtUtc.AddDays(_minimumRetentionDays))
        {
            IntegrityKeyId = _integrityKeys.CurrentKeyId
        };
        entry = entry with { Hash = ComputeHash(entry) };

        await using (var insertEvent = new NpgsqlCommand("""
            INSERT INTO ciem_audit_events (tenant_id, sequence, event_id, event_json)
            VALUES (@tenant_id, @sequence, @event_id, @event_json)
            """, connection, transaction))
        {
            insertEvent.Parameters.AddWithValue("tenant_id", tenantId);
            insertEvent.Parameters.AddWithValue("sequence", entry.Sequence);
            insertEvent.Parameters.AddWithValue("event_id", entry.EventId);
            insertEvent.Parameters.AddWithValue("event_json", JsonSerializer.Serialize(entry, JsonOptions));
            await insertEvent.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var updateHead = new NpgsqlCommand("""
            UPDATE ciem_audit_heads
            SET last_sequence = @sequence, last_hash = @hash
            WHERE tenant_id = @tenant_id
            """, connection, transaction))
        {
            updateHead.Parameters.AddWithValue("tenant_id", tenantId);
            updateHead.Parameters.AddWithValue("sequence", entry.Sequence);
            updateHead.Parameters.AddWithValue("hash", entry.Hash);
            if (await updateHead.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidDataException("The PostgreSQL audit tenant head could not be updated.");
        }

        await transaction.CommitAsync(cancellationToken);
        return entry;
    }

    public async Task<IReadOnlyList<CentralAuditEventDto>> ReadAsync(Guid tenantId, long afterSequence, int limit,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("An authenticated tenant ID is required.", nameof(tenantId));
        if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        var readiness = await CheckReadinessAsync(cancellationToken);
        if (!readiness.ProductionReady) throw new InvalidOperationException(readiness.Details);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await SetTenantContextAsync(connection, transaction, tenantId, cancellationToken);
        var head = await ReadHeadAsync(connection, transaction, tenantId, cancellationToken);
        var entries = await ReadAndVerifyAsync(connection, transaction, tenantId, head.Sequence, head.Hash, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return entries.Where(entry => entry.Sequence > afterSequence).Take(limit).ToArray();
    }

    private async Task<(long Sequence, string Hash)> ReadHeadAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid tenantId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT last_sequence, last_hash
            FROM ciem_audit_heads
            WHERE tenant_id = @tenant_id
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetInt64(0), reader.GetString(1))
            : (0, new string('0', 64));
    }

    private static async Task SetTenantContextAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid tenantId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT set_config('app.tenant_id', @tenant_id, true)", connection, transaction);
        command.Parameters.AddWithValue("tenant_id", tenantId.ToString("D"));
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private async Task<List<CentralAuditEventDto>> ReadAndVerifyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid tenantId, long headSequence, string headHash, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT event_json
            FROM ciem_audit_events
            WHERE tenant_id = @tenant_id
            ORDER BY sequence
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<CentralAuditEventDto>();
        var previousHash = new string('0', 64);
        long expectedSequence = 1;
        while (await reader.ReadAsync(cancellationToken))
        {
            CentralAuditEventDto entry;
            try
            {
                entry = JsonSerializer.Deserialize<CentralAuditEventDto>(reader.GetString(0), JsonOptions)
                    ?? throw new InvalidDataException("The PostgreSQL audit log contains an invalid record.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The PostgreSQL audit log contains an invalid record; refusing read or append.", exception);
            }

            if (entry.TenantId != tenantId || entry.EventId == Guid.Empty || entry.Sequence != expectedSequence
                || !string.Equals(entry.PreviousHash, previousHash, StringComparison.Ordinal)
                || !FixedEquals(entry.Hash, ComputeHash(entry)))
                throw new InvalidDataException("The PostgreSQL audit hash chain is invalid; refusing read or append.");
            entries.Add(entry);
            previousHash = entry.Hash;
            expectedSequence++;
        }

        if (headSequence != expectedSequence - 1 || !string.Equals(headHash, previousHash, StringComparison.Ordinal))
            throw new InvalidDataException("The PostgreSQL audit tenant head does not match its event chain.");
        return entries;
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

    private static bool MetadataEquals(IReadOnlyDictionary<string, string> first, IReadOnlyDictionary<string, string> second)
        => first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out var value) && value == pair.Value);

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
