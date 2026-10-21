using System.Text.Json;
using CloudEntitle.Integration.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace CloudEntitle.LicenseServer.Services;

public sealed record NotificationDeliveryLease(Guid TenantId, Guid OrganizationId, SharedDeliveryDto Delivery,
    Guid AttemptId, SharedDriftEventDto Event);

public sealed partial class SharedWorkspaceStore
{
    private const string DeliveryColumns = """
        DeliveryId, EventId, RuleId, Channel, Status, AttemptCount, CreatedAtUtc,
        NextAttemptAtUtc, CompletedAtUtc, FailureCode, ConnectorKind, RequestedBy
        """;

    public async Task<IReadOnlyList<SharedDeliveryDto>> GetNotificationDeliveriesAsync(Guid tenantId, Guid organizationId,
        int limit = 100, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT {DeliveryColumns} FROM SharedNotificationDeliveries
            WHERE TenantId=@tenant AND OrganizationId=@organization ORDER BY CreatedAtUtc DESC, DeliveryId LIMIT @limit;
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("organization", organizationId);
        command.Parameters.AddWithValue("limit", limit);
        var result = new List<SharedDeliveryDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadDelivery(reader));
        return result;
    }

    public async Task<SharedDeliveryStatusDto?> GetNotificationDeliveryStatusAsync(Guid tenantId, Guid organizationId,
        Guid deliveryId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        SharedDeliveryDto delivery;
        await using (var command = new NpgsqlCommand($"""
            SELECT {DeliveryColumns} FROM SharedNotificationDeliveries
            WHERE TenantId=@tenant AND OrganizationId=@organization AND DeliveryId=@delivery;
            """, connection))
        {
            AddDeliveryIdentity(command, tenantId, organizationId, deliveryId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            delivery = ReadDelivery(reader);
        }
        await using var attempts = new NpgsqlCommand("""
            SELECT AttemptId, AttemptNumber, StartedAtUtc, CompletedAtUtc, HttpStatus, FailureCode
            FROM SharedNotificationAttempts
            WHERE TenantId=@tenant AND OrganizationId=@organization AND DeliveryId=@delivery ORDER BY AttemptNumber;
            """, connection);
        AddDeliveryIdentity(attempts, tenantId, organizationId, deliveryId);
        var result = new List<SharedDeliveryAttemptDto>();
        await using var attemptReader = await attempts.ExecuteReaderAsync(cancellationToken);
        while (await attemptReader.ReadAsync(cancellationToken))
            result.Add(new SharedDeliveryAttemptDto(attemptReader.GetGuid(0), attemptReader.GetInt32(1), attemptReader.GetDateTime(2),
                attemptReader.IsDBNull(3) ? null : attemptReader.GetDateTime(3), attemptReader.IsDBNull(4) ? null : attemptReader.GetInt32(4),
                attemptReader.IsDBNull(5) ? null : attemptReader.GetString(5)));
        return new SharedDeliveryStatusDto(delivery, result);
    }

    public async Task<NotificationDeliveryLease?> ClaimNotificationDeliveryAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var expired = new List<(Guid Tenant, Guid Organization, Guid Delivery, Guid Attempt)>();
        await using (var recover = new NpgsqlCommand("""
            SELECT TenantId, OrganizationId, DeliveryId, ActiveAttemptId FROM SharedNotificationDeliveries
            WHERE Status='Sending' AND LeaseExpiresAtUtc < @now
            ORDER BY LeaseExpiresAtUtc FOR UPDATE SKIP LOCKED LIMIT 50;
            """, connection, transaction))
        {
            recover.Parameters.AddWithValue("now", now);
            await using var reader = await recover.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                expired.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3)));
        }
        foreach (var item in expired)
        {
            await using var recover = new NpgsqlCommand("""
                UPDATE SharedNotificationAttempts SET CompletedAtUtc=@now, FailureCode='lease_expired'
                WHERE TenantId=@tenant AND OrganizationId=@organization AND DeliveryId=@delivery AND AttemptId=@attempt;
                UPDATE SharedNotificationDeliveries
                SET Status=CASE WHEN AttemptCount >= 5 OR ConnectorKind='Jira' THEN 'Failed' ELSE 'Pending' END,
                    NextAttemptAtUtc=CASE WHEN AttemptCount >= 5 OR ConnectorKind='Jira' THEN NULL ELSE @retry END,
                    CompletedAtUtc=CASE WHEN AttemptCount >= 5 OR ConnectorKind='Jira' THEN @now ELSE NULL END,
                    FailureCode='lease_expired', ActiveAttemptId=NULL, LeaseExpiresAtUtc=NULL
                WHERE TenantId=@tenant AND OrganizationId=@organization AND DeliveryId=@delivery;
                """, connection, transaction);
            AddDeliveryIdentity(recover, item.Tenant, item.Organization, item.Delivery);
            recover.Parameters.AddWithValue("attempt", item.Attempt);
            recover.Parameters.AddWithValue("now", now);
            recover.Parameters.AddWithValue("retry", now.AddSeconds(30));
            await recover.ExecuteNonQueryAsync(cancellationToken);
        }
        Guid tenantId;
        Guid organizationId;
        SharedDeliveryDto delivery;
        var attemptId = Guid.NewGuid();
        await using (var claim = new NpgsqlCommand($"""
            WITH candidate AS (
                SELECT TenantId, OrganizationId, DeliveryId FROM SharedNotificationDeliveries
                WHERE Status='Pending' AND NextAttemptAtUtc <= @now AND AttemptCount < 5
                ORDER BY CreatedAtUtc FOR UPDATE SKIP LOCKED LIMIT 1
            )
            UPDATE SharedNotificationDeliveries d SET Status='Sending', AttemptCount=AttemptCount+1,
                ActiveAttemptId=@attempt, LeaseExpiresAtUtc=@lease, NextAttemptAtUtc=NULL
            FROM candidate c WHERE d.TenantId=c.TenantId AND d.OrganizationId=c.OrganizationId AND d.DeliveryId=c.DeliveryId
            RETURNING d.TenantId, d.OrganizationId, {string.Join(", ", DeliveryColumns.Split(',', StringSplitOptions.TrimEntries).Select(column => $"d.{column}"))};
            """, connection, transaction))
        {
            claim.Parameters.AddWithValue("now", now);
            claim.Parameters.AddWithValue("attempt", attemptId);
            claim.Parameters.AddWithValue("lease", now.AddMinutes(1));
            await using var reader = await claim.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await reader.CloseAsync();
                await transaction.CommitAsync(cancellationToken);
                return null;
            }
            tenantId = reader.GetGuid(0);
            organizationId = reader.GetGuid(1);
            delivery = ReadDelivery(reader, 2);
        }
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO SharedNotificationAttempts
                (TenantId, OrganizationId, DeliveryId, AttemptId, AttemptNumber, StartedAtUtc)
            VALUES (@tenant, @organization, @delivery, @attempt, @number, @now);
            """, connection, transaction))
        {
            AddDeliveryIdentity(insert, tenantId, organizationId, delivery.DeliveryId);
            insert.Parameters.AddWithValue("attempt", attemptId);
            insert.Parameters.AddWithValue("number", delivery.AttemptCount);
            insert.Parameters.AddWithValue("now", now);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var readEvent = new NpgsqlCommand("""
            SELECT PayloadJson FROM SharedDriftEvents WHERE TenantId=@tenant AND OrganizationId=@organization AND EventId=@event;
            """, connection, transaction);
        readEvent.Parameters.AddWithValue("tenant", tenantId);
        readEvent.Parameters.AddWithValue("organization", organizationId);
        readEvent.Parameters.AddWithValue("event", delivery.EventId);
        var payload = await readEvent.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new InvalidOperationException("The notification event is missing.");
        var drift = JsonSerializer.Deserialize<SharedDriftEventDto>(payload, JsonOptions)
            ?? throw new InvalidOperationException("The notification event is invalid.");
        await transaction.CommitAsync(cancellationToken);
        return new NotificationDeliveryLease(tenantId, organizationId, delivery, attemptId, drift);
    }

    public async Task<bool> IsNotificationEnabledAsync(NotificationDeliveryLease lease, CancellationToken cancellationToken)
        => lease.Delivery.RequestedBy.HasValue || (await GetNotificationRulesAsync(lease.TenantId, lease.OrganizationId, cancellationToken))
            .Any(rule => rule.Channel == lease.Delivery.Channel && rule.ConnectorKind == lease.Delivery.ConnectorKind && Matches(rule, lease.Event));

    public async Task CompleteNotificationDeliveryAsync(NotificationDeliveryLease lease, NotificationSendResult result,
        CancellationToken cancellationToken = default)
    {
        if (!NotificationSendResult.IsSafe(result)) throw new ArgumentException("Invalid notification transport result.", nameof(result));
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var retry = result.Retryable && lease.Delivery.AttemptCount < 5 && !result.Delivered && !result.Suppressed
            && (lease.Delivery.ConnectorKind != "Jira" || result.HttpStatus == 429);
        await using var complete = new NpgsqlCommand("""
            UPDATE SharedNotificationDeliveries
            SET Status=@status, NextAttemptAtUtc=@retry, CompletedAtUtc=@completed, FailureCode=@failure,
                ActiveAttemptId=NULL, LeaseExpiresAtUtc=NULL
            WHERE TenantId=@tenant AND OrganizationId=@organization AND DeliveryId=@delivery
                AND ActiveAttemptId=@attempt AND Status='Sending';
            """, connection, transaction);
        AddDeliveryIdentity(complete, lease.TenantId, lease.OrganizationId, lease.Delivery.DeliveryId);
        complete.Parameters.AddWithValue("attempt", lease.AttemptId);
        complete.Parameters.AddWithValue("status", result.Delivered ? "Delivered" : result.Suppressed ? "Suppressed" : retry ? "Pending" : "Failed");
        complete.Parameters.Add("retry", NpgsqlDbType.TimestampTz).Value = retry
            ? now.AddSeconds(Math.Min(900, 30 * Math.Pow(2, lease.Delivery.AttemptCount - 1))) : DBNull.Value;
        complete.Parameters.Add("completed", NpgsqlDbType.TimestampTz).Value = retry ? DBNull.Value : now;
        complete.Parameters.Add("failure", NpgsqlDbType.Text).Value = (object?)result.FailureCode ?? DBNull.Value;
        if (await complete.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("The notification worker no longer owns this delivery lease.");
        await using var attempt = new NpgsqlCommand("""
            UPDATE SharedNotificationAttempts SET CompletedAtUtc=@now, HttpStatus=@http, FailureCode=@failure
            WHERE TenantId=@tenant AND OrganizationId=@organization AND DeliveryId=@delivery AND AttemptId=@attempt;
            """, connection, transaction);
        AddDeliveryIdentity(attempt, lease.TenantId, lease.OrganizationId, lease.Delivery.DeliveryId);
        attempt.Parameters.AddWithValue("attempt", lease.AttemptId);
        attempt.Parameters.AddWithValue("now", now);
        attempt.Parameters.Add("http", NpgsqlDbType.Integer).Value = (object?)result.HttpStatus ?? DBNull.Value;
        attempt.Parameters.Add("failure", NpgsqlDbType.Text).Value = (object?)result.FailureCode ?? DBNull.Value;
        await attempt.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static SharedDeliveryDto ReadDelivery(NpgsqlDataReader reader, int offset = 0)
        => new(reader.GetGuid(offset), reader.GetGuid(offset + 1), reader.GetGuid(offset + 2), reader.GetString(offset + 3),
            reader.GetString(offset + 4), reader.GetInt32(offset + 5), reader.GetDateTime(offset + 6),
            reader.IsDBNull(offset + 7) ? null : reader.GetDateTime(offset + 7),
            reader.IsDBNull(offset + 8) ? null : reader.GetDateTime(offset + 8),
            reader.IsDBNull(offset + 9) ? null : reader.GetString(offset + 9))
        {
            ConnectorKind = reader.GetString(offset + 10),
            RequestedBy = reader.IsDBNull(offset + 11) ? null : reader.GetGuid(offset + 11)
        };

    private static void AddDeliveryIdentity(NpgsqlCommand command, Guid tenantId, Guid organizationId, Guid deliveryId)
    {
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("organization", organizationId);
        command.Parameters.AddWithValue("delivery", deliveryId);
    }
}
