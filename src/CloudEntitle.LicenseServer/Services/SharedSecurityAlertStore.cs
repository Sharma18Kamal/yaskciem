using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudEntitle.Integration.Contracts;
using Npgsql;

namespace CloudEntitle.LicenseServer.Services;

public sealed partial class SharedWorkspaceStore
{
    public async Task<SecurityAlertImportReceipt> ImportSecurityAlertsAsync(Guid tenant, Guid organization, Guid actor,
        string product, string channel, ImportSecurityAlertsRequest request, CancellationToken token = default)
    {
        InboundSecurityProducts.Validate(product, channel, request);
        var json = JsonSerializer.Serialize(request, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > InboundSecurityProducts.MaximumBatchBytes) throw new ArgumentException("Alert batch exceeds 2 MiB.");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        await using var connection = await OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        // Serializes both first import and subsequent cursor updates for this tenant/organization/connector.
        await using (var locked = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@scope,0));", connection, transaction))
        {
            locked.Parameters.AddWithValue("scope", $"security-alerts:{tenant:D}:{organization:D}:{product}:{channel}");
            await locked.ExecuteNonQueryAsync(token);
        }
        NpgsqlCommand Command(string sql)
        {
            var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("tenant", tenant);
            command.Parameters.AddWithValue("organization", organization);
            command.Parameters.AddWithValue("product", product);
            command.Parameters.AddWithValue("channel", channel);
            return command;
        }
        await using (var replay = Command("""
            SELECT PayloadSha256, ReceiptJson FROM SharedSecurityAlertBatches
            WHERE TenantId=@tenant AND OrganizationId=@organization AND Product=@product AND Channel=@channel AND BatchId=@batch;
            """))
        {
            replay.Parameters.AddWithValue("batch", request.BatchId);
            await using var reader = await replay.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
            {
                if (reader.GetString(0) != hash) throw new InvalidOperationException("Batch identifier already has different evidence.");
                return JsonSerializer.Deserialize<SecurityAlertImportReceipt>(reader.GetString(1), JsonOptions)
                    ?? throw new InvalidOperationException("Invalid stored ingestion receipt.");
            }
        }
        await using (var cursor = Command("""
            SELECT Cursor FROM SharedSecurityAlertCursors WHERE TenantId=@tenant AND OrganizationId=@organization AND Product=@product AND Channel=@channel;
            """))
        {
            var current = await cursor.ExecuteScalarAsync(token) as string;
            if (current != request.PreviousCursor) throw new InvalidOperationException("Connector cursor changed; reload before importing.");
        }
        var accepted = 0;
        var received = DateTimeOffset.UtcNow;
        foreach (var alert in request.Alerts)
        {
            var payload = JsonSerializer.Serialize(alert, JsonOptions);
            await using var revision = Command("""
                INSERT INTO SharedSecurityAlertRevisions
                    (TenantId,OrganizationId,Product,Channel,AlertId,UpdatedAtUtc,ReceivedAtUtc,UploadedBy,PayloadJson)
                VALUES (@tenant,@organization,@product,@channel,@alert,@updated,@received,@actor,@payload)
                ON CONFLICT DO NOTHING;
                SELECT PayloadJson FROM SharedSecurityAlertRevisions
                WHERE TenantId=@tenant AND OrganizationId=@organization AND Product=@product AND Channel=@channel AND AlertId=@alert AND UpdatedAtUtc=@updated;
                """);
            revision.Parameters.AddWithValue("alert", alert.AlertId);
            revision.Parameters.AddWithValue("updated", alert.UpdatedAtUtc.ToUniversalTime());
            revision.Parameters.AddWithValue("received", received);
            revision.Parameters.AddWithValue("actor", actor);
            revision.Parameters.AddWithValue("payload", payload);
            if (await revision.ExecuteScalarAsync(token) as string != payload)
                throw new InvalidOperationException("Alert revision already has conflicting evidence. Cursor was not advanced.");
            accepted++;
        }
        var receipt = new SecurityAlertImportReceipt(request.BatchId, product, channel, request.Cursor, accepted);
        await using (var commit = Command("""
            INSERT INTO SharedSecurityAlertBatches (TenantId,OrganizationId,Product,Channel,BatchId,PayloadSha256,ReceiptJson)
            VALUES (@tenant,@organization,@product,@channel,@batch,@hash,@receipt);
            INSERT INTO SharedSecurityAlertCursors (TenantId,OrganizationId,Product,Channel,Cursor)
            VALUES (@tenant,@organization,@product,@channel,@cursor)
            ON CONFLICT (TenantId,OrganizationId,Product,Channel) DO UPDATE SET Cursor=EXCLUDED.Cursor;
            """))
        {
            commit.Parameters.AddWithValue("batch", request.BatchId);
            commit.Parameters.AddWithValue("hash", hash);
            commit.Parameters.AddWithValue("receipt", JsonSerializer.Serialize(receipt, JsonOptions));
            commit.Parameters.AddWithValue("cursor", request.Cursor);
            await commit.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
        return receipt;
    }

    public async Task<SecurityAlertCursor> GetSecurityAlertCursorAsync(Guid tenant, Guid organization, string product,
        string channel, CancellationToken token = default)
    {
        ValidateSecurityScope(product, channel);
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT Cursor FROM SharedSecurityAlertCursors WHERE TenantId=@tenant AND OrganizationId=@organization AND Product=@product AND Channel=@channel;
            """, connection);
        AddSecurityScope(command, tenant, organization, product, channel);
        return new(product, channel, await command.ExecuteScalarAsync(token) as string);
    }

    public async Task<IReadOnlyList<StoredSecurityAlert>> GetSecurityAlertsAsync(Guid tenant, Guid organization, string product,
        string channel, int limit = 100, CancellationToken token = default)
    {
        ValidateSecurityScope(product, channel);
        if (limit is < 1 or > 500) throw new ArgumentException("Alert limit must be 1 through 500.");
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT UploadedBy,ReceivedAtUtc,PayloadJson FROM (
                SELECT DISTINCT ON (AlertId) AlertId,UploadedBy,ReceivedAtUtc,UpdatedAtUtc,PayloadJson
                FROM SharedSecurityAlertRevisions
                WHERE TenantId=@tenant AND OrganizationId=@organization AND Product=@product AND Channel=@channel
                ORDER BY AlertId,UpdatedAtUtc DESC
            ) latest ORDER BY UpdatedAtUtc DESC,AlertId LIMIT @limit;
            """, connection);
        AddSecurityScope(command, tenant, organization, product, channel);
        command.Parameters.AddWithValue("limit", limit);
        var alerts = new List<StoredSecurityAlert>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            alerts.Add(new(product, channel, reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1),
                JsonSerializer.Deserialize<InboundSecurityAlert>(reader.GetString(2), JsonOptions)
                    ?? throw new InvalidOperationException("Invalid stored security alert evidence.")));
        return alerts;
    }

    private static void ValidateSecurityScope(string product, string channel)
    {
        if (!InboundSecurityProducts.Supported.Contains(product) || !InboundSecurityProducts.IsChannel(channel))
            throw new ArgumentException("Unsupported security product or channel.");
    }
    private static void AddSecurityScope(NpgsqlCommand command, Guid tenant, Guid organization, string product, string channel)
    {
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("organization", organization);
        command.Parameters.AddWithValue("product", product);
        command.Parameters.AddWithValue("channel", channel);
    }
}
