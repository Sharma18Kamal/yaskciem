using System.Text.Json;
using CloudEntitle.Domain;
using CloudEntitle.IdentityGovernance.Services;
using CloudEntitle.IdentityGovernance.Models;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.LicenseServer.Services;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed partial class CentralMonitoringTests
{
    [PostgreSqlFact]
    public async Task Operations_ShouldStandardizeEventsAndRequireTicketPolicyOrExplicitRequest()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        await store.InitializeAsync();
        var (tenant, organization) = Bind(store);
        var allow = OperationalEventTypes.All.ToArray();
        await store.SaveNotificationRuleAsync(tenant, organization, Rule() with { EventTypes = allow });
        var ticketRule = Rule() with { RuleId = Guid.NewGuid(), Channel = "jira", ConnectorKind = "Jira", EventTypes = allow };
        await store.SaveNotificationRuleAsync(tenant, organization, ticketRule);
        var time = DateTime.UtcNow.AddHours(-1);
        await Save(store, Scan(tenant, organization, time), Array.Empty<SharedCiemRecordDto>());
        var createdScan = Scan(tenant, organization, time.AddMinutes(1));
        await Save(store, createdScan, new[] { Finding("user", "High"), Access("user", true) });
        var created = await store.GetDriftEventsAsync(tenant, organization);
        Assert.Contains(created, item => item.EventType == "finding.created");
        Assert.Contains(created, item => item.EventType == "access.added");
        Assert.All(await store.GetNotificationDeliveriesAsync(tenant, organization), item => Assert.Equal("SignedWebhook", item.ConnectorKind));
        await store.SaveNotificationRuleAsync(tenant, organization, ticketRule with { AllowAutomaticTickets = true });
        var resolved = Finding("user", "High") with { PayloadJson = JsonSerializer.Serialize(new
            { ruleId = "admin-rule", principal = "user", title = "Admin risk", severity = "High", status = 1 }) };
        await Save(store, Scan(tenant, organization, time.AddMinutes(2)), new[] { resolved });
        Assert.Contains(await store.GetDriftEventsAsync(tenant, organization), item => item.EventType == "finding.resolved");
        Assert.Contains(await store.GetNotificationDeliveriesAsync(tenant, organization), item => item.ConnectorKind == "Jira");
        var firstEvent = created.Single(item => item.EventType == "finding.created");
        var actor = Guid.NewGuid();
        var explicitRequests = await Task.WhenAll(
            store.RequestOperationalTicketAsync(tenant, organization, firstEvent.EventId, "jira", actor),
            store.RequestOperationalTicketAsync(tenant, organization, firstEvent.EventId, "jira", actor));
        Assert.Equal(explicitRequests[0].Delivery.DeliveryId, explicitRequests[1].Delivery.DeliveryId);
        Assert.Equal(actor, explicitRequests[0].Delivery.RequestedBy);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            store.RequestOperationalTicketAsync(Guid.NewGuid(), organization, firstEvent.EventId, "jira", actor));

        var campaign = new IdentityGovernanceService().CreateCampaign("Review", new[]
        {
            new AccessReviewCandidate(Guid.NewGuid(), Guid.NewGuid(), null, CloudProvider.Aws, "Review access", "Captured evidence")
                { ExternalAccountId = "123456789012" }
        }, TimeSpan.FromDays(1), asOf: DateTime.UtcNow.AddDays(-2));
        await store.SaveAccessReviewAsync(tenant, organization, campaign);
        var future = DateTime.UtcNow.AddMinutes(2);
        await store.ProcessGovernanceDeadlinesAsync(future);
        await store.ProcessGovernanceDeadlinesAsync(future.AddMinutes(2));
        var overdue = Assert.Single(await store.GetDriftEventsAsync(tenant, organization), item => item.EventType == "review.overdue");
        Assert.Equal(campaign.Id, overdue.SubjectId);
        Assert.DoesNotContain("Captured evidence", JsonSerializer.Serialize(overdue));

        var schedule = (await store.CreateScanScheduleAsync(tenant, organization,
            new CreateScanScheduleRequest(CloudProvider.Aws, "123456789012", "AWS", 5, 1)))!;
        await store.QueueScanNowAsync(tenant, organization, schedule.ScheduleId);
        var lease = (await store.ClaimNextScanJobAsync())!;
        await store.CompleteScanJobAttemptAsync(lease, "Failed", "sensitive source error", null);
        Assert.DoesNotContain(await store.GetDriftEventsAsync(tenant, organization), item => item.EventType == "scan.failed");
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var due = new NpgsqlCommand("UPDATE SharedScanJobs SET NextAttemptAtUtc=now()-interval '1 second';", connection);
            await due.ExecuteNonQueryAsync();
        }
        lease = (await store.ClaimNextScanJobAsync())!;
        await store.CompleteScanJobAttemptAsync(lease, "Failed", "sensitive source error", null);
        var failed = Assert.Single(await store.GetDriftEventsAsync(tenant, organization), item => item.EventType == "scan.failed");
        Assert.Equal(lease.Job.JobId, failed.SubjectId);
        Assert.DoesNotContain("sensitive source error", JsonSerializer.Serialize(failed));
        Assert.All(await store.GetNotificationDeliveriesAsync(tenant, organization), item => Assert.NotEqual(Guid.Empty, item.DeliveryId));
    }

    [PostgreSqlFact]
    public async Task Operations_ShouldTrackDeadLettersLatencyAndFenceExpiredJiraSends()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        var (tenant, organization) = Bind(store);
        await store.SaveNotificationRuleAsync(tenant, organization, Rule() with
        {
            Channel = "jira", ConnectorKind = "Jira", AllowAutomaticTickets = true, EventTypes = new[] { "finding.created" }
        });
        var time = DateTime.UtcNow.AddHours(-1);
        await Save(store, Scan(tenant, organization, time), Array.Empty<SharedCiemRecordDto>());
        await Save(store, Scan(tenant, organization, time.AddMinutes(1)), new[] { Finding("user", "High") });
        var lease = (await store.ClaimNotificationDeliveryAsync())!;
        await store.CompleteNotificationDeliveryAsync(lease, new(false, true, 429, "http_rejected"));
        var status = (await store.GetNotificationDeliveryStatusAsync(tenant, organization, lease.Delivery.DeliveryId))!;
        Assert.Equal("Pending", status.Delivery.Status);
        Assert.True(Assert.Single(status.Attempts).LatencyMilliseconds >= 0);
        await MakeDue(database.ConnectionString, lease.Delivery.DeliveryId);
        lease = (await store.ClaimNotificationDeliveryAsync())!;
        await store.CompleteNotificationDeliveryAsync(lease, new(false, true, 503, "outcome_unknown"));
        status = (await store.GetNotificationDeliveryStatusAsync(tenant, organization, lease.Delivery.DeliveryId))!;
        Assert.True(status.Delivery.DeadLettered);
        Assert.Null(status.Delivery.NextAttemptAtUtc);
        Assert.Null(await store.ClaimNotificationDeliveryAsync());
        var root = $"CentralNotifications:Organizations:{tenant:D}:{organization:D}:Channels";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{root}:jira:Kind"] = "Jira", [$"{root}:jira:Url"] = "https://jira.invalid",
            [$"{root}:jira:UserName"] = "bot@example.invalid", [$"{root}:jira:ApiToken"] = "test-api-secret",
            [$"{root}:jira:ProjectKey"] = "SEC", [$"{root}:jira:IssueType"] = "Task",
            [$"{root}:security:Url"] = "https://receiver.invalid", [$"{root}:security:SigningKey"] = new string('x', 32)
        }).Build();
        var health = await store.GetConnectorHealthAsync(tenant, organization, configuration);
        var jira = Assert.Single(health, item => item.ConnectorKind == "Jira");
        Assert.Equal("Degraded", jira.Health);
        Assert.Equal(1, jira.DeadLettered);
        Assert.Equal(0, jira.Retrying);
        Assert.NotNull(jira.AverageLatencyMilliseconds);
        Assert.Equal("Unknown", Assert.Single(health, item => item.ConnectorKind == "SignedWebhook").Health);
        Assert.DoesNotContain("test-api-secret", JsonSerializer.Serialize(health));
        Assert.Empty(await store.GetConnectorHealthAsync(Guid.NewGuid(), organization, configuration));

        await Save(store, Scan(tenant, organization, time.AddMinutes(2)), new[] { Finding("user", "High"), Finding("other", "High") });
        var abandoned = (await store.ClaimNotificationDeliveryAsync())!;
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var expire = new NpgsqlCommand("UPDATE SharedNotificationDeliveries SET LeaseExpiresAtUtc=now()-interval '1 second' WHERE Status='Sending';", connection);
            await expire.ExecuteNonQueryAsync();
        }
        Assert.Null(await store.ClaimNotificationDeliveryAsync());
        Assert.True((await store.GetNotificationDeliveryStatusAsync(tenant, organization, abandoned.Delivery.DeliveryId))!.Delivery.DeadLettered);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CompleteNotificationDeliveryAsync(abandoned, new(true, false, 201, null)));
    }

    [Fact]
    public void Operations_ShouldValidatePolicyAndKeepLegacyRulesWebhookOnly()
    {
        Assert.Equal("SignedWebhook", Rule().ConnectorKind);
        SharedWorkspaceStore.ValidateNotificationRule(Rule());
        Assert.Throws<ArgumentException>(() => SharedWorkspaceStore.ValidateNotificationRule(Rule() with
            { ConnectorKind = "Jira", AllowAutomaticTickets = true }));
        Assert.Throws<ArgumentException>(() => SharedWorkspaceStore.ValidateNotificationRule(Rule() with { EventTypes = new[] { "anything" } }));
        SharedWorkspaceStore.ValidateNotificationRule(Rule() with { ConnectorKind = "Jira",
            AllowAutomaticTickets = true, EventTypes = new[] { "review.overdue", "scan.failed" } });
    }

    [PostgreSqlFact]
    public async Task Operations_ShouldPublishExhaustedLeaseFailureButNotBackfillOldReviewEscalations()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        var (tenant, organization) = Bind(store);
        var governance = new IdentityGovernanceService();
        var old = governance.CreateCampaign("Legacy overdue", new[]
        {
            new AccessReviewCandidate(Guid.NewGuid(), Guid.NewGuid(), null, CloudProvider.Aws, "Admin", "Evidence")
        }, TimeSpan.FromDays(1), asOf: DateTime.UtcNow.AddDays(-3));
        old = governance.ReevaluateCampaign(old, DateTime.UtcNow.AddDays(-1));
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var insert = new NpgsqlCommand("""
                INSERT INTO SharedAccessReviewCampaigns (TenantId,OrganizationId,CampaignId,CreatedAtUtc,PayloadJson,NextGovernanceCheckAtUtc)
                VALUES (@tenant,@organization,@id,@created,@payload,now()-interval '1 second');
                """, connection);
            insert.Parameters.AddWithValue("tenant", tenant);
            insert.Parameters.AddWithValue("organization", organization);
            insert.Parameters.AddWithValue("id", old.Id);
            insert.Parameters.AddWithValue("created", old.CreatedAt);
            insert.Parameters.AddWithValue("payload", JsonSerializer.Serialize(old));
            await insert.ExecuteNonQueryAsync();
        }
        await store.SaveNotificationRuleAsync(tenant, organization, Rule() with { EventTypes = new[] { "review.overdue", "scan.failed" } });
        await store.ProcessGovernanceDeadlinesAsync(DateTime.UtcNow);
        Assert.Empty(await store.GetDriftEventsAsync(tenant, organization));
        var schedule = (await store.CreateScanScheduleAsync(tenant, organization,
            new(CloudProvider.Aws, "123456789012", "Production", 5, 0)))!;
        await store.QueueScanNowAsync(tenant, organization, schedule.ScheduleId);
        var lease = (await store.ClaimNextScanJobAsync())!;
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var expire = new NpgsqlCommand("UPDATE SharedScanJobs SET LeaseExpiresAtUtc=now()-interval '1 second' WHERE Status='Running';", connection);
            await expire.ExecuteNonQueryAsync();
        }
        Assert.Null(await store.ClaimNextScanJobAsync());
        var failed = Assert.Single(await store.GetDriftEventsAsync(tenant, organization));
        Assert.Equal("scan.failed", failed.EventType);
        Assert.Equal(lease.Job.JobId, failed.SubjectId);
        Assert.Single(await store.GetNotificationDeliveriesAsync(tenant, organization));
        Assert.Null(await store.ClaimNextScanJobAsync());
        Assert.Single(await store.GetDriftEventsAsync(tenant, organization));
        var queued = (await store.QueueScanNowAsync(tenant, organization, schedule.ScheduleId))!;
        Assert.True(await store.RequestScanJobCancellationAsync(tenant, organization, queued.JobId));
        Assert.Single(await store.GetDriftEventsAsync(tenant, organization));
    }

    [PostgreSqlFact]
    public async Task Operations_ShouldSuppressRevokedAutomaticTicketPolicyWithoutSuppressingExplicitRequests()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        var (tenant, organization) = Bind(store);
        var rule = Rule() with { Channel = "jira", ConnectorKind = "Jira", AllowAutomaticTickets = true,
            EventTypes = new[] { "finding.created" } };
        await store.SaveNotificationRuleAsync(tenant, organization, rule);
        var time = DateTime.UtcNow.AddHours(-1);
        await Save(store, Scan(tenant, organization, time), Array.Empty<SharedCiemRecordDto>());
        await Save(store, Scan(tenant, organization, time.AddMinutes(1)), new[] { Finding("user", "High") });
        var lease = (await store.ClaimNotificationDeliveryAsync())!;
        Assert.True(await store.IsNotificationEnabledAsync(lease, default));
        await store.SaveNotificationRuleAsync(tenant, organization, rule with { AllowAutomaticTickets = false });
        Assert.False(await store.IsNotificationEnabledAsync(lease, default));
        await store.CompleteNotificationDeliveryAsync(lease, new(false, false, null, "rule_disabled", Suppressed: true));
        Assert.Equal("Suppressed", (await store.GetNotificationDeliveryStatusAsync(tenant, organization, lease.Delivery.DeliveryId))!.Delivery.Status);
        await Save(store, Scan(tenant, organization, time.AddMinutes(2)), new[] { Finding("user", "High"), Finding("other", "High") });
        var explicitEvent = (await store.GetDriftEventsAsync(tenant, organization)).First(item => item.EventId != lease.Event.EventId);
        await store.RequestOperationalTicketAsync(tenant, organization, explicitEvent.EventId, "jira", Guid.NewGuid());
        lease = (await store.ClaimNotificationDeliveryAsync())!;
        Assert.True(await store.IsNotificationEnabledAsync(lease, default));
    }
}
