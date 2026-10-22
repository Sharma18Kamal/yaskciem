using System.Text.Json;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.LicenseServer.Services;
using CloudEntitle.OrganizationManagement.Models;
using CloudEntitle.OrganizationManagement.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed partial class CentralMonitoringTests
{
    [PostgreSqlFact]
    public async Task Drift_ShouldCompareStableStateDeduplicateAndIsolateOrganizations()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        var (tenant, organization) = Bind(store);
        var rule = Rule();
        await store.SaveNotificationRuleAsync(tenant, organization, rule);
        await store.SaveNotificationRuleAsync(tenant, organization, rule with { RuleId = Guid.NewGuid(), Name = "Overlapping rule" });
        var start = DateTime.UtcNow.AddHours(-1);
        var before = new[] { Finding("changed", "High"), Finding("removed", "High"), Access("changed", false), Access("removed", true),
            ResourceFinding("changed", "Open"), ResourceFinding("removed", "Open") };
        var first = Scan(tenant, organization, start);
        await Save(store, first, before);
        Assert.Empty(await store.GetDriftEventsAsync(tenant, organization));

        var after = new[] { Finding("changed", "Critical"), Finding("added", "High"), Access("changed", true), Access("added", true),
            ResourceFinding("changed", "Resolved"), ResourceFinding("added", "Open") };
        var second = Scan(tenant, organization, start.AddMinutes(1));
        await Save(store, second, after);
        var events = await store.GetDriftEventsAsync(tenant, organization);
        Assert.Equal(9, events.Count);
        foreach (var kind in new[] { "finding", "resource-finding", "effective-access" })
            Assert.Equal(new[] { "Added", "Changed", "Removed" }, events.Where(item => item.Kind == kind).Select(item => item.Change).Order());
        var deliveries = await store.GetNotificationDeliveriesAsync(tenant, organization);
        Assert.Equal(9, deliveries.Count);
        Assert.Equal(9, deliveries.Select(item => item.DeliveryId).Distinct().Count());
        Assert.All(events, item => Assert.NotEqual(Guid.Empty, item.EventId));
        await Save(store, second, after);
        Assert.Equal(9, (await store.GetNotificationDeliveriesAsync(tenant, organization)).Count);

        // Record IDs and collection order are not the entity identity; unchanged scans must remain quiet.
        await Save(store, Scan(tenant, organization, start.AddMinutes(2)), Enumerable.Reverse(after).Select(item => item with { RecordId = Guid.NewGuid().ToString() }).ToArray());
        Assert.Equal(9, (await store.GetDriftEventsAsync(tenant, organization)).Count);
        await Save(store, Scan(tenant, organization, start.AddSeconds(30)), Array.Empty<SharedCiemRecordDto>());
        Assert.Equal(9, (await store.GetDriftEventsAsync(tenant, organization)).Count);
        Assert.Empty(await store.GetDriftEventsAsync(Guid.NewGuid(), organization));
        Assert.Empty(await store.GetNotificationDeliveriesAsync(tenant, Guid.NewGuid()));
        Assert.Null(await store.GetNotificationDeliveryStatusAsync(Guid.NewGuid(), organization, deliveries[0].DeliveryId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveCiemScanAsync(second, after, "conflicting-replay"));
    }

    [PostgreSqlFact]
    public async Task IncompleteScans_ShouldNotRemoveStateOrReplaceComparableBaseline()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        var (tenant, organization) = Bind(store);
        var start = DateTime.UtcNow.AddHours(-1);
        await Save(store, Scan(tenant, organization, start), new[] { Access("admin", true), Finding("admin", "High") });
        var partial = Scan(tenant, organization, start.AddMinutes(1)) with
        {
            Coverage = new[] { new SharedScanCoverageDto("Aws", "Effective access", "Partial", 0, "incomplete") },
            MonitoringScopes = Array.Empty<SharedMonitoringScopeDto>()
        };
        await Save(store, partial, Array.Empty<SharedCiemRecordDto>());
        Assert.Empty(await store.GetDriftEventsAsync(tenant, organization));
        var invalid = partial with
        {
            ScanId = Guid.NewGuid(),
            MonitoringScopes = new[] { new SharedMonitoringScopeDto(CloudProvider.Aws, "123456789012", "effective-access", "effective-access-v1") }
        };
        await Assert.ThrowsAsync<ArgumentException>(() => Save(store, invalid, Array.Empty<SharedCiemRecordDto>()));
        await Save(store, Scan(tenant, organization, start.AddMinutes(2)), Array.Empty<SharedCiemRecordDto>());
        Assert.All(await store.GetDriftEventsAsync(tenant, organization), item => Assert.Equal("Removed", item.Change));
        Assert.Equal(2, (await store.GetDriftEventsAsync(tenant, organization)).Count);
    }

    [PostgreSqlFact]
    public async Task NotificationRules_ShouldApplySeveritySourcesChangesAndSuppressDisabledPendingDelivery()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        var (tenant, organization) = Bind(store);
        var rule = Rule() with { MinimumSeverity = SharedAlertSeverity.Critical, EffectiveAccess = false, Removed = false, Changed = false };
        await store.SaveNotificationRuleAsync(tenant, organization, rule);
        var start = DateTime.UtcNow.AddHours(-1);
        await Save(store, Scan(tenant, organization, start), Array.Empty<SharedCiemRecordDto>());
        await Save(store, Scan(tenant, organization, start.AddMinutes(1)),
            new[] { Finding("high", "High"), Finding("critical", "Critical"), Access("admin", true) });
        Assert.Equal(3, (await store.GetDriftEventsAsync(tenant, organization)).Count);
        var delivery = Assert.Single(await store.GetNotificationDeliveriesAsync(tenant, organization));
        var lease = Assert.IsType<NotificationDeliveryLease>(await store.ClaimNotificationDeliveryAsync());
        Assert.True(await store.IsNotificationEnabledAsync(lease, default));
        await store.SaveNotificationRuleAsync(tenant, organization, rule with { Enabled = false });
        Assert.False(await store.IsNotificationEnabledAsync(lease, default));
        await store.CompleteNotificationDeliveryAsync(lease, new(false, false, null, "rule_disabled", true));
        Assert.Equal("Suppressed", (await store.GetNotificationDeliveryStatusAsync(tenant, organization, delivery.DeliveryId))!.Delivery.Status);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveNotificationRuleAsync(tenant, organization,
            rule with { Channel = "https://secret.invalid/token" }));
    }

    [PostgreSqlFact]
    public async Task Deliveries_ShouldRetryWithStableIdsAndStopAfterFiveAttempts()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        var (tenant, organization) = Bind(store);
        await store.SaveNotificationRuleAsync(tenant, organization, Rule());
        var start = DateTime.UtcNow.AddHours(-1);
        await Save(store, Scan(tenant, organization, start), Array.Empty<SharedCiemRecordDto>());
        await Save(store, Scan(tenant, organization, start.AddMinutes(1)), new[] { Access("admin", true) });
        var delivery = Assert.Single(await store.GetNotificationDeliveriesAsync(tenant, organization));
        for (var number = 1; number <= 5; number++)
        {
            var lease = Assert.IsType<NotificationDeliveryLease>(await store.ClaimNotificationDeliveryAsync());
            Assert.Equal(delivery.DeliveryId, lease.Delivery.DeliveryId);
            Assert.Equal(delivery.EventId, lease.Event.EventId);
            Assert.Equal(number, lease.Delivery.AttemptCount);
            Assert.Null(await store.ClaimNotificationDeliveryAsync());
            await store.CompleteNotificationDeliveryAsync(lease, new(false, true, 503, "http_rejected"));
            var status = Assert.IsType<SharedDeliveryStatusDto>(await store.GetNotificationDeliveryStatusAsync(tenant, organization, delivery.DeliveryId));
            Assert.Equal(number, status.Attempts.Count);
            if (number < 5)
            {
                Assert.Equal("Pending", status.Delivery.Status);
                var delay = Assert.IsType<DateTime>(status.Delivery.NextAttemptAtUtc) - DateTime.UtcNow;
                var expectedSeconds = 30 * Math.Pow(2, number - 1);
                Assert.InRange(delay.TotalSeconds, expectedSeconds - 2, expectedSeconds + 1);
                await MakeDue(database.ConnectionString, delivery.DeliveryId);
            }
            else
            {
                Assert.Equal("Failed", status.Delivery.Status);
                Assert.Null(status.Delivery.NextAttemptAtUtc);
                Assert.NotNull(status.Delivery.CompletedAtUtc);
            }
        }
        Assert.Null(await store.ClaimNotificationDeliveryAsync());
    }

    [PostgreSqlFact]
    public async Task ExpiredDeliveryLease_ShouldRecoverAndFenceStaleCompletion()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        var (tenant, organization) = Bind(store);
        await store.SaveNotificationRuleAsync(tenant, organization, Rule());
        var start = DateTime.UtcNow.AddHours(-1);
        await Save(store, Scan(tenant, organization, start), Array.Empty<SharedCiemRecordDto>());
        await Save(store, Scan(tenant, organization, start.AddMinutes(1)), new[] { Access("admin", true) });
        var stale = Assert.IsType<NotificationDeliveryLease>(await store.ClaimNotificationDeliveryAsync());
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("UPDATE SharedNotificationDeliveries SET LeaseExpiresAtUtc=@time WHERE DeliveryId=@delivery;", connection);
            command.Parameters.AddWithValue("time", DateTime.UtcNow.AddMinutes(-1));
            command.Parameters.AddWithValue("delivery", stale.Delivery.DeliveryId);
            await command.ExecuteNonQueryAsync();
        }
        Assert.Null(await store.ClaimNotificationDeliveryAsync());
        await MakeDue(database.ConnectionString, stale.Delivery.DeliveryId);
        var current = Assert.IsType<NotificationDeliveryLease>(await store.ClaimNotificationDeliveryAsync());
        Assert.NotEqual(stale.AttemptId, current.AttemptId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CompleteNotificationDeliveryAsync(stale, new(true, false, 200, null)));
        await store.CompleteNotificationDeliveryAsync(current, new(true, false, 204, null));
        var status = (await store.GetNotificationDeliveryStatusAsync(tenant, organization, stale.Delivery.DeliveryId))!;
        Assert.Equal("Delivered", status.Delivery.Status);
        Assert.Equal("lease_expired", status.Attempts[0].FailureCode);
        Assert.Equal(204, status.Attempts[1].HttpStatus);
    }

    [PostgreSqlFact]
    public async Task HostedWorker_ShouldConsumeDurableOutboxAndPersistSuccessfulDelivery()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        var (tenant, organization) = Bind(store);
        await store.SaveNotificationRuleAsync(tenant, organization, Rule());
        var start = DateTime.UtcNow.AddHours(-1);
        await Save(store, Scan(tenant, organization, start), Array.Empty<SharedCiemRecordDto>());
        await Save(store, Scan(tenant, organization, start.AddMinutes(1)), new[] { Access("admin", true) });
        var delivery = Assert.Single(await store.GetNotificationDeliveriesAsync(tenant, organization));
        var transport = new RecordingTransport();
        using var worker = new CentralNotificationWorker(store, transport, NullLogger<CentralNotificationWorker>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await worker.StartAsync(timeout.Token);
            SharedDeliveryStatusDto status;
            do
            {
                await Task.Delay(20, timeout.Token);
                status = (await store.GetNotificationDeliveryStatusAsync(tenant, organization, delivery.DeliveryId, timeout.Token))!;
            } while (status.Delivery.Status != "Delivered");
            Assert.Equal(delivery.EventId, Assert.Single(transport.Sent).Event.EventId);
            Assert.Equal(204, Assert.Single(status.Attempts).HttpStatus);
        }
        finally
        {
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await worker.StopAsync(stopTimeout.Token);
        }
    }

    private static (Guid Tenant, Guid Organization) Bind(SharedWorkspaceStore store)
    {
        var tenant = Guid.NewGuid();
        var owner = new OrganizationIdentity(Guid.NewGuid(), "owner", tenant.ToString(), "owner@example.invalid");
        var service = new OrganizationManagementService(store.CreateOrganizationStore(tenant));
        var organization = service.CreateOrganization("Monitoring", Guid.NewGuid().ToString(), tenant.ToString(), owner);
        service.BindCloudAccount(organization.Id, owner, CloudProvider.Aws, Guid.NewGuid(), "123456789012", "AWS");
        return (tenant, organization.Id);
    }

    private static SharedNotificationRuleDto Rule() => new(Guid.NewGuid(), "Important changes", true, SharedAlertSeverity.Informational,
        true, true, true, true, true, "security");

    private static SharedScanSummaryDto Scan(Guid tenant, Guid organization, DateTime time) =>
        new(Guid.NewGuid(), organization, tenant, Guid.NewGuid(), time.AddSeconds(-1), time, "Aws",
            new[] { new SharedScanAccountDto(CloudProvider.Aws, "123456789012", "AWS") }, 0, 0, 0, 0, 0, 0, 0, 0,
            new[] { new SharedScanCoverageDto("Aws", "Effective access", "Collected", 0, ""),
                new SharedScanCoverageDto("Aws", "Risk findings", "Collected", 0, "") })
        {
            MonitoringScopes = new[] { new SharedMonitoringScopeDto(CloudProvider.Aws, "123456789012", "effective-access", "effective-access-v1"),
                new SharedMonitoringScopeDto(CloudProvider.Aws, "123456789012", "finding", "desktop-risk-v1"),
                new SharedMonitoringScopeDto(CloudProvider.Aws, "123456789012", "resource-finding", "desktop-resource-risk-v1") }
        };

    private static SharedCiemRecordDto Finding(string principal, string severity) => new(CloudProvider.Aws, "finding", Guid.NewGuid().ToString(),
        "123456789012", JsonSerializer.Serialize(new { ruleId = "admin-rule", principal, title = "Admin risk", severity, status = "Open" }));
    private static SharedCiemRecordDto Access(string principal, bool privileged) => new(CloudProvider.Aws, "effective-access", Guid.NewGuid().ToString(),
        "123456789012", JsonSerializer.Serialize(new { principal, role = "Operator", scope = "*", source = "IAM", isPrivileged = privileged, assignmentType = "Assigned" }));
    private static SharedCiemRecordDto ResourceFinding(string fingerprint, string status) => new(CloudProvider.Aws, "resource-finding", Guid.NewGuid().ToString(),
        "123456789012", JsonSerializer.Serialize(new { ruleId = "resource-rule", title = "Resource risk", fingerprint, severity = "High", status }));
    private static Task<(SharedScanSummaryDto Summary, bool Created)> Save(SharedWorkspaceStore store, SharedScanSummaryDto scan,
        IReadOnlyList<SharedCiemRecordDto> records) => store.SaveCiemScanAsync(scan, records,
            SharedMonitoringProjection.Hash(JsonSerializer.Serialize(new { scan, records })));

    private static async Task MakeDue(string connectionString, Guid deliveryId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE SharedNotificationDeliveries SET NextAttemptAtUtc=@time WHERE DeliveryId=@delivery AND Status='Pending';", connection);
        command.Parameters.AddWithValue("time", DateTime.UtcNow.AddSeconds(-1));
        command.Parameters.AddWithValue("delivery", deliveryId);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class RecordingTransport : ICentralNotificationTransport
    {
        public List<NotificationDeliveryLease> Sent { get; } = new();
        public Task<NotificationSendResult> SendAsync(NotificationDeliveryLease lease, CancellationToken cancellationToken)
        {
            Sent.Add(lease);
            return Task.FromResult(new NotificationSendResult(true, false, 204, null));
        }
    }
}
