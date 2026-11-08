using CloudEntitle.Integration.Contracts;
using CloudEntitle.LicenseServer.Services;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed partial class CentralMonitoringTests
{
    [PostgreSqlFact]
    public async Task SiemChannels_RouteDeduplicatedEventsAndRetainScopedDeliveryHealth()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        Assert.True(await store.IsReadyAsync());
        var (tenant, organization) = Bind(store);
        var kinds = new[] { SecurityConnectorCatalog.Splunk, SecurityConnectorCatalog.QRadar, SecurityConnectorCatalog.Sentinel };
        foreach (var kind in kinds)
            await store.SaveNotificationRuleAsync(tenant, organization, Rule() with
            {
                ConnectorKind = kind, Channel = kind, EventTypes = new[] { "finding.created" }
            });
        var time = DateTime.UtcNow.AddHours(-1);
        await Save(store, Scan(tenant, organization, time), Array.Empty<SharedCiemRecordDto>());
        var scan = Scan(tenant, organization, time.AddMinutes(1));
        var records = new[] { Finding("admin", "High") };
        await Save(store, scan, records);
        await Save(store, scan, records);
        await store.InitializeAsync();
        Assert.True(await store.IsReadyAsync());

        var deliveries = await store.GetNotificationDeliveriesAsync(tenant, organization);
        Assert.Equal(3, deliveries.Count);
        Assert.Equal(kinds.Order(), deliveries.Select(item => item.ConnectorKind).Order());
        Assert.Empty(await store.GetNotificationDeliveriesAsync(Guid.NewGuid(), organization));
        var delivered = new HashSet<string>();
        for (var i = 0; i < 3; i++)
        {
            var lease = (await store.ClaimNotificationDeliveryAsync())!;
            Assert.True(await store.IsNotificationEnabledAsync(lease, default));
            delivered.Add(lease.Delivery.ConnectorKind);
            await store.CompleteNotificationDeliveryAsync(lease, new(true, false, null, null));
        }
        Assert.Equal(3, delivered.Count);
        var configuration = new ConfigurationBuilder().Build();
        var health = await store.GetConnectorHealthAsync(tenant, organization, configuration);
        Assert.Equal(3, health.Count);
        Assert.All(health, item => Assert.Equal(1, item.Delivered));
        Assert.All(health, item => Assert.NotEqual("Healthy", item.Health));
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "ALTER TABLE SharedNotificationDeliveries DROP CONSTRAINT ck_sharednotificationdeliveries_connectorkind_v2;",
                connection);
            await command.ExecuteNonQueryAsync();
        }
        Assert.False(await store.IsReadyAsync());
        await store.InitializeAsync();
        Assert.True(await store.IsReadyAsync());
        Assert.All(await store.GetNotificationDeliveriesAsync(tenant, organization), item => Assert.Equal("Delivered", item.Status));
    }

    [PostgreSqlFact]
    public async Task Qradar_DeadLettersAmbiguousSendsAndExpiredLeasesRatherThanBlindRetrying()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        var (tenant, organization) = Bind(store);
        await store.SaveNotificationRuleAsync(tenant, organization, Rule() with
        {
            ConnectorKind = SecurityConnectorCatalog.QRadar, Channel = "qradar", EventTypes = new[] { "finding.created" }
        });
        var time = DateTime.UtcNow.AddHours(-1);
        await Save(store, Scan(tenant, organization, time), Array.Empty<SharedCiemRecordDto>());
        await Save(store, Scan(tenant, organization, time.AddMinutes(1)), new[] { Finding("first", "High"), Finding("second", "High") });
        var first = (await store.ClaimNotificationDeliveryAsync())!;
        await store.CompleteNotificationDeliveryAsync(first, new(false, true, null, "outcome_unknown"));
        var status = (await store.GetNotificationDeliveryStatusAsync(tenant, organization, first.Delivery.DeliveryId))!;
        Assert.True(status.Delivery.DeadLettered);
        Assert.Null(status.Delivery.NextAttemptAtUtc);

        var expired = (await store.ClaimNotificationDeliveryAsync())!;
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "UPDATE SharedNotificationDeliveries SET LeaseExpiresAtUtc=now()-interval '1 second' WHERE DeliveryId=@delivery;",
                connection);
            command.Parameters.AddWithValue("delivery", expired.Delivery.DeliveryId);
            await command.ExecuteNonQueryAsync();
        }
        Assert.Null(await store.ClaimNotificationDeliveryAsync());
        status = (await store.GetNotificationDeliveryStatusAsync(tenant, organization, expired.Delivery.DeliveryId))!;
        Assert.True(status.Delivery.DeadLettered);
        Assert.Equal("lease_expired", status.Delivery.FailureCode);
    }
}
