using CloudEntitle.Integration.Contracts;
using Npgsql;

namespace CloudEntitle.LicenseServer.Services;

public sealed partial class SharedWorkspaceStore
{
    public async Task<IReadOnlyList<ConnectorHealthDto>> GetConnectorHealthAsync(Guid tenant, Guid organization,
        IConfiguration configuration, CancellationToken token = default)
    {
        var channels = configuration.GetSection($"CentralNotifications:Organizations:{tenant:D}:{organization:D}:Channels");
        var result = new List<ConnectorHealthDto>();
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            WITH deliveries AS (
                SELECT Channel, ConnectorKind,
                    count(*) FILTER (WHERE Status='Pending') AS Pending,
                    count(*) FILTER (WHERE Status='Sending') AS Sending,
                    count(*) FILTER (WHERE Status='Pending' AND AttemptCount>0) AS Retrying,
                    count(*) FILTER (WHERE Status='Failed') AS Dead,
                    count(*) FILTER (WHERE Status='Delivered') AS Delivered,
                    max(CompletedAtUtc) FILTER (WHERE Status='Delivered') AS Success
                FROM SharedNotificationDeliveries WHERE TenantId=@tenant AND OrganizationId=@organization GROUP BY Channel, ConnectorKind
            ), attempts AS (
                SELECT d.Channel, d.ConnectorKind, max(a.StartedAtUtc) AS LastAttempt,
                    avg(GREATEST(0, EXTRACT(EPOCH FROM (a.CompletedAtUtc-a.StartedAtUtc))*1000))
                        FILTER (WHERE a.CompletedAtUtc IS NOT NULL) AS Latency
                FROM SharedNotificationAttempts a JOIN SharedNotificationDeliveries d
                    USING (TenantId, OrganizationId, DeliveryId)
                WHERE a.TenantId=@tenant AND a.OrganizationId=@organization GROUP BY d.Channel, d.ConnectorKind
            )
            SELECT d.Channel,d.ConnectorKind,d.Pending,d.Sending,d.Retrying,d.Dead,d.Delivered,d.Success,a.LastAttempt,a.Latency
            FROM deliveries d LEFT JOIN attempts a USING (Channel,ConnectorKind) ORDER BY d.Channel,d.ConnectorKind;
            """, connection);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("organization", organization);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var channel = reader.GetString(0);
            var kind = reader.GetString(1);
            var status = ConnectorConfiguration.Status(channels.GetSection(channel), kind);
            var dead = reader.GetInt64(5);
            var retrying = reader.GetInt64(4);
            DateTimeOffset? Date(int index) => reader.IsDBNull(index) ? null : new DateTimeOffset(reader.GetDateTime(index));
            result.Add(new(channel, kind, status, status != "Configured" ? status
                    : dead > 0 || retrying > 0 ? "Degraded" : reader.IsDBNull(7) ? "Unknown" : "Healthy",
                reader.GetInt64(2), reader.GetInt64(3), retrying, dead, reader.GetInt64(6), Date(8), Date(7),
                reader.IsDBNull(9) ? null : reader.GetDouble(9)));
        }
        foreach (var channel in channels.GetChildren())
        {
            var kind = ConnectorConfiguration.Kind(channel);
            if (result.Any(item => item.Channel == channel.Key && item.ConnectorKind == kind)) continue;
            var status = ConnectorConfiguration.Status(channel, kind);
            result.Add(new(channel.Key, kind, status, status == "Configured" ? "Unknown" : status,
                0, 0, 0, 0, 0, null, null, null));
        }
        return result.OrderBy(item => item.Channel).ThenBy(item => item.ConnectorKind).ToArray();
    }
}
