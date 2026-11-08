using System.Text.Json;
using CloudEntitle.Integration.Contracts;
using Npgsql;

namespace CloudEntitle.LicenseServer.Services;

public sealed record PrivilegedAccessWork(Guid TenantId, Guid OrganizationId, PrivilegedAccessRequest Request);

public sealed partial class SharedWorkspaceStore
{
    public async Task<IAsyncDisposable> LockPrivilegedAccessAsync(Guid tenant, Guid organization, Guid request, CancellationToken token)
    {
        var connection = await OpenAsync(token);
        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_lock(hashtextextended(@key,0));", connection);
            command.Parameters.AddWithValue("key", $"jit:{tenant:D}:{organization:D}:{request:D}");
            await command.ExecuteNonQueryAsync(token);
            return new JitLease(connection, $"jit:{tenant:D}:{organization:D}:{request:D}");
        }
        catch { await connection.DisposeAsync(); throw; }
    }
    private sealed class JitLease(NpgsqlConnection connection, string key) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended(@key,0));", connection);
                unlock.Parameters.AddWithValue("key", key);
                await unlock.ExecuteNonQueryAsync();
            }
            finally { await connection.DisposeAsync(); }
        }
    }
    public async Task<PrivilegedAccessRequest?> GetPrivilegedAccessAsync(Guid tenant, Guid organization, Guid request, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT PayloadJson FROM SharedPrivilegedAccessRequests WHERE TenantId=@tenant AND OrganizationId=@organization AND RequestId=@request;
            """, connection);
        AddJitIdentity(command, tenant, organization, request);
        var payload = await command.ExecuteScalarAsync(token) as string;
        return payload is null ? null : ReadPrivilegedAccess(payload);
    }
    public async Task TouchPrivilegedAccessAsync(PrivilegedAccessWork work, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            UPDATE SharedPrivilegedAccessRequests SET UpdatedAtUtc=@now
            WHERE TenantId=@tenant AND OrganizationId=@organization AND RequestId=@request AND (PayloadJson::jsonb->>'version')::bigint=@version;
            """, connection);
        AddJitIdentity(command, work.TenantId, work.OrganizationId, work.Request.RequestId);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue("version", work.Request.Version);
        await command.ExecuteNonQueryAsync(token);
    }
    public async Task<IReadOnlyList<PrivilegedAccessRequest>> GetPrivilegedAccessAsync(Guid tenant, Guid organization, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT PayloadJson FROM SharedPrivilegedAccessRequests WHERE TenantId=@tenant AND OrganizationId=@organization
            ORDER BY UpdatedAtUtc DESC, RequestId LIMIT 100;
            """, connection);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("organization", organization);
        var rows = new List<PrivilegedAccessRequest>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) rows.Add(ReadPrivilegedAccess(reader.GetString(0)));
        return rows;
    }

    public async Task<IReadOnlyList<PrivilegedAccessAudit>> GetPrivilegedAccessAuditAsync(Guid tenant, Guid organization, Guid request, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT PayloadJson FROM SharedPrivilegedAccessAudit
            WHERE TenantId=@tenant AND OrganizationId=@organization AND RequestId=@request ORDER BY AtUtc, EventId LIMIT 500;
            """, connection);
        AddJitIdentity(command, tenant, organization, request);
        var rows = new List<PrivilegedAccessAudit>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            rows.Add(JsonSerializer.Deserialize<PrivilegedAccessAudit>(reader.GetString(0), JsonOptions) ?? throw new InvalidOperationException("Stored JIT audit is invalid."));
        return rows;
    }

    public async Task<PrivilegedAccessRequest> UpdatePrivilegedAccessAsync(Guid tenant, Guid organization, Guid request, Guid actor,
        string action, string reason, Func<PrivilegedAccessRequest?, PrivilegedAccessRequest> update, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using var select = new NpgsqlCommand("""
            SELECT PayloadJson FROM SharedPrivilegedAccessRequests
            WHERE TenantId=@tenant AND OrganizationId=@organization AND RequestId=@request FOR UPDATE;
            """, connection, transaction);
        AddJitIdentity(select, tenant, organization, request);
        var payload = await select.ExecuteScalarAsync(token) as string;
        var previous = payload is null ? null : ReadPrivilegedAccess(payload);
        var current = update(previous);
        if (current.RequestId != request || current.Version != (previous is null ? 0 : previous.Version + 1))
            throw new InvalidOperationException("Invalid JIT transition version.");
        await using var save = new NpgsqlCommand(previous is null ? """
            INSERT INTO SharedPrivilegedAccessRequests(TenantId,OrganizationId,RequestId,Status,UpdatedAtUtc,PayloadJson)
            VALUES(@tenant,@organization,@request,@status,@now,@payload);
            """ : """
            UPDATE SharedPrivilegedAccessRequests SET Status=@status,UpdatedAtUtc=@now,PayloadJson=@payload
            WHERE TenantId=@tenant AND OrganizationId=@organization AND RequestId=@request;
            """, connection, transaction);
        AddJitIdentity(save, tenant, organization, request);
        save.Parameters.AddWithValue("status", current.Status);
        save.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        save.Parameters.AddWithValue("payload", JsonSerializer.Serialize(current, JsonOptions));
        await save.ExecuteNonQueryAsync(token);
        var audit = new PrivilegedAccessAudit(Guid.NewGuid(), request, actor, action, reason, DateTimeOffset.UtcNow);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO SharedPrivilegedAccessAudit(TenantId,OrganizationId,RequestId,EventId,AtUtc,PayloadJson)
            VALUES(@tenant,@organization,@request,@event,@now,@payload);
            """, connection, transaction);
        AddJitIdentity(insert, tenant, organization, request);
        insert.Parameters.AddWithValue("event", audit.EventId);
        insert.Parameters.AddWithValue("now", audit.AtUtc);
        insert.Parameters.AddWithValue("payload", JsonSerializer.Serialize(audit, JsonOptions));
        await insert.ExecuteNonQueryAsync(token);
        await RecordOperationalEventAsync(connection, transaction, tenant, organization,
            new(audit.EventId, Guid.Empty, Guid.Empty, current.Provider, current.SubscriptionId, "jit", "Changed",
                request.ToString("D"), current.Emergency || current.FailureCode is not null ? SharedAlertSeverity.High : SharedAlertSeverity.Medium,
                null, null, audit.AtUtc.UtcDateTime) { EventType = $"jit.{action.ToLowerInvariant()}", SubjectId = request }, token);
        await transaction.CommitAsync(token);
        return current;
    }

    public async Task<IReadOnlyList<PrivilegedAccessWork>> GetPrivilegedAccessWorkAsync(CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT TenantId,OrganizationId,PayloadJson FROM SharedPrivilegedAccessRequests
            WHERE Status IN ('Queued','Issuing','Submitted','Active','ExpiryPending','OutcomeUnknown','RevokeQueued','RevokeIssuing','RevokeOutcomeUnknown')
            ORDER BY UpdatedAtUtc LIMIT 50;
            """, connection);
        var rows = new List<PrivilegedAccessWork>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) rows.Add(new(reader.GetGuid(0), reader.GetGuid(1), ReadPrivilegedAccess(reader.GetString(2))));
        return rows;
    }

    private static PrivilegedAccessRequest ReadPrivilegedAccess(string payload)
        => JsonSerializer.Deserialize<PrivilegedAccessRequest>(payload, JsonOptions) ?? throw new InvalidOperationException("Stored JIT request is invalid.");
    private static void AddJitIdentity(NpgsqlCommand command, Guid tenant, Guid organization, Guid request)
    {
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("organization", organization);
        command.Parameters.AddWithValue("request", request);
    }
}
