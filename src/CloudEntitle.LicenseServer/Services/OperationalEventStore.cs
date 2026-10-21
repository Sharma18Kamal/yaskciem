using System.Text.Json;
using CloudEntitle.Domain;
using CloudEntitle.IdentityGovernance.Models;
using CloudEntitle.Integration.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace CloudEntitle.LicenseServer.Services;

public sealed partial class SharedWorkspaceStore
{
    private static Guid OperationalId(string material)
        => new(Convert.FromHexString(SharedMonitoringProjection.Hash(material)).AsSpan(0, 16));

    private static async Task RecordOperationalEventAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid tenant, Guid organization, SharedDriftEventDto value, CancellationToken token,
        IReadOnlyList<SharedNotificationRuleDto>? rules = null)
    {
        await using var insert = new NpgsqlCommand("""
            INSERT INTO SharedDriftEvents (TenantId, OrganizationId, EventId, DetectedAtUtc, PayloadJson)
            VALUES (@tenant, @organization, @event, @now, @payload) ON CONFLICT DO NOTHING;
            """, connection, transaction);
        insert.Parameters.AddWithValue("tenant", tenant);
        insert.Parameters.AddWithValue("organization", organization);
        insert.Parameters.AddWithValue("event", value.EventId);
        insert.Parameters.AddWithValue("now", value.DetectedAtUtc);
        insert.Parameters.AddWithValue("payload", JsonSerializer.Serialize(value, JsonOptions));
        if (await insert.ExecuteNonQueryAsync(token) == 0) return;
        rules ??= await ReadRulesAsync(connection, transaction, tenant, organization, token);
        foreach (var rule in rules.Where(rule => Matches(rule, value)).OrderBy(rule => rule.RuleId))
            await QueueOperationalDeliveryAsync(connection, transaction, tenant, organization, value.EventId,
                rule.RuleId, rule.Channel, rule.ConnectorKind, null, token);
    }

    private static async Task QueueOperationalDeliveryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid tenant, Guid organization, Guid eventId, Guid ruleId, string channel, string kind, Guid? actor, CancellationToken token)
    {
        await using var delivery = new NpgsqlCommand("""
            INSERT INTO SharedNotificationDeliveries
                (TenantId, OrganizationId, DeliveryId, EventId, RuleId, Channel, ConnectorKind, RequestedBy, Status, CreatedAtUtc, NextAttemptAtUtc)
            VALUES (@tenant, @organization, @delivery, @event, @rule, @channel, @kind, @actor, 'Pending', @now, @now)
            ON CONFLICT (TenantId, OrganizationId, EventId, Channel) DO NOTHING;
            """, connection, transaction);
        delivery.Parameters.AddWithValue("tenant", tenant);
        delivery.Parameters.AddWithValue("organization", organization);
        delivery.Parameters.AddWithValue("delivery", OperationalId($"{eventId:D}|{channel}"));
        delivery.Parameters.AddWithValue("event", eventId);
        delivery.Parameters.AddWithValue("rule", ruleId);
        delivery.Parameters.AddWithValue("channel", channel);
        delivery.Parameters.AddWithValue("kind", kind);
        delivery.Parameters.Add("actor", NpgsqlDbType.Uuid).Value = (object?)actor ?? DBNull.Value;
        delivery.Parameters.AddWithValue("now", DateTime.UtcNow);
        await delivery.ExecuteNonQueryAsync(token);
    }

    public async Task<SharedDeliveryStatusDto> RequestOperationalTicketAsync(Guid tenant, Guid organization, Guid eventId,
        string channel, Guid actor, CancellationToken token = default)
    {
        if (actor == Guid.Empty || string.IsNullOrEmpty(channel) || channel.Length > 64
            || channel.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw new ArgumentException("A channel alias and authenticated actor are required.");
        await using var connection = await OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using var check = new NpgsqlCommand("""
            SELECT 1 FROM SharedDriftEvents WHERE TenantId=@tenant AND OrganizationId=@organization AND EventId=@event;
            """, connection, transaction);
        check.Parameters.AddWithValue("tenant", tenant);
        check.Parameters.AddWithValue("organization", organization);
        check.Parameters.AddWithValue("event", eventId);
        if (await check.ExecuteScalarAsync(token) is null) throw new KeyNotFoundException();
        await QueueOperationalDeliveryAsync(connection, transaction, tenant, organization, eventId, Guid.Empty, channel, "Jira", actor, token);
        await transaction.CommitAsync(token);
        var status = await GetNotificationDeliveryStatusAsync(tenant, organization, OperationalId($"{eventId:D}|{channel}"), token)
            ?? throw new InvalidOperationException("The queued ticket delivery is missing.");
        if (status.Delivery.ConnectorKind != "Jira")
            throw new InvalidOperationException("This event/channel already belongs to a webhook delivery. Use a separate Jira alias.");
        return status;
    }

    private static async Task RecordReviewOverdueEventsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid tenant, Guid organization, AccessReviewCampaign? previous, AccessReviewCampaign campaign, CancellationToken token)
    {
        if (campaign.Status is CampaignStatus.Cancelled or CampaignStatus.Draft) return;
        var historical = previous?.Items.SelectMany(item => item.AuditTrail.Where(entry => entry.Action == "Escalated")
            .Select(entry => (item.Id, entry.Timestamp))).ToHashSet() ?? new();
        foreach (var item in campaign.Items)
        foreach (var entry in item.AuditTrail.Where(entry => entry.Action == "Escalated" && entry.EventId != Guid.Empty
            && !historical.Contains((item.Id, entry.Timestamp))))
        {
            var value = new SharedDriftEventDto(OperationalId($"review.overdue|{tenant}|{organization}|{entry.EventId}"),
                Guid.Empty, Guid.Empty, item.Provider, item.ExternalAccountId ?? "", "review", "Changed",
                item.Id.ToString("D"), SharedAlertSeverity.High, null, null, entry.Timestamp)
                { EventType = "review.overdue", SubjectId = campaign.Id };
            await RecordOperationalEventAsync(connection, transaction, tenant, organization, value, token);
        }
    }

    private static Task RecordScanFailureAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid tenant, Guid organization, Guid job, CloudProvider provider, string account, Guid? scanId, DateTime now, CancellationToken token)
        => RecordOperationalEventAsync(connection, transaction, tenant, organization,
            new SharedDriftEventDto(OperationalId($"scan.failed|{tenant}|{organization}|{job}"),
                scanId ?? Guid.Empty, Guid.Empty, provider, account, "scan", "Changed",
                job.ToString("D"), SharedAlertSeverity.High, null, null, now)
                { EventType = "scan.failed", SubjectId = job }, token);
}
