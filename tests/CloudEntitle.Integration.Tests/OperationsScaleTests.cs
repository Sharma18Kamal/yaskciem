using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.LicenseServer.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed class OperationsScaleTests
{
    [PostgreSqlFact]
    public async Task TenantScanLimits_ShouldBeEnforcedAtomicallyAndRemainIsolated()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString, maxSchedulesPerTenant: 2, maxActiveScanJobsPerTenant: 1);
        await store.InitializeAsync();
        var tenant = Guid.NewGuid();
        var organization = Guid.NewGuid();
        var first = await store.CreateScanScheduleAsync(tenant, organization,
            new CreateScanScheduleRequest(CloudProvider.Aws, "tenant-a-1", "Account 1", 60));
        var second = await store.CreateScanScheduleAsync(tenant, organization,
            new CreateScanScheduleRequest(CloudProvider.Aws, "tenant-a-2", "Account 2", 60));
        Assert.NotNull(first);
        Assert.NotNull(second);
        await Assert.ThrowsAsync<TenantResourceLimitException>(() => store.CreateScanScheduleAsync(tenant, organization,
            new CreateScanScheduleRequest(CloudProvider.Aws, "tenant-a-3", "Account 3", 60)));

        var active = await store.QueueScanNowAsync(tenant, organization, first.ScheduleId);
        Assert.NotNull(active);
        Assert.Equal(active.JobId, (await store.QueueScanNowAsync(tenant, organization, first.ScheduleId))!.JobId);
        await Assert.ThrowsAsync<TenantResourceLimitException>(() => store.QueueScanNowAsync(tenant, organization, second.ScheduleId));

        var otherTenant = await store.CreateScanScheduleAsync(Guid.NewGuid(), Guid.NewGuid(),
            new CreateScanScheduleRequest(CloudProvider.Aws, "other-tenant-account", "Other account", 60));
        Assert.NotNull(otherTenant);
        Assert.True(await store.CheckOperationsReadinessAsync());
    }

    [PostgreSqlFact]
    public async Task OperationsMetricsAndRetention_ShouldMeasureQueueAndPreserveDeadLettersAndScanEvidence()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        var tenant = Guid.NewGuid();
        var organization = Guid.NewGuid();
        var schedule = Assert.IsType<ScanScheduleDto>(await store.CreateScanScheduleAsync(tenant, organization,
            new CreateScanScheduleRequest(CloudProvider.Aws, "operations-account", "Operations account", 60)));
        var completed = Assert.IsType<ScanJobDto>(await store.QueueScanNowAsync(tenant, organization, schedule.ScheduleId));
        var queuedSchedule = Assert.IsType<ScanScheduleDto>(await store.CreateScanScheduleAsync(tenant, organization,
            new CreateScanScheduleRequest(CloudProvider.Aws, "operations-account-2", "Operations account 2", 60)));
        var queued = Assert.IsType<ScanJobDto>(await store.QueueScanNowAsync(tenant, organization, queuedSchedule.ScheduleId));

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        var retainedScanId = Guid.NewGuid();
        var deliveredId = Guid.NewGuid();
        var failedId = Guid.NewGuid();
        var deliveredEventId = Guid.NewGuid();
        var failedEventId = Guid.NewGuid();
        await using (var batch = new NpgsqlBatch(connection))
        {
            var scanCommand = new NpgsqlBatchCommand("""
            INSERT INTO SharedCiemScans (TenantId, OrganizationId, ScanId, CompletedAtUtc, RequestHash, PayloadJson)
            VALUES (@tenant, @org, @scan, now() - interval '2 days', 'hash', '{}')
            """);
            scanCommand.Parameters.AddWithValue("tenant", tenant);
            scanCommand.Parameters.AddWithValue("org", organization);
            scanCommand.Parameters.AddWithValue("scan", retainedScanId);
            batch.BatchCommands.Add(scanCommand);

            var recordCommand = new NpgsqlBatchCommand("""
            INSERT INTO SharedCiemScanRecords
                (TenantId, OrganizationId, ScanId, Provider, RecordKind, RecordId, ExternalAccountId, PayloadJson)
            VALUES (@tenant, @org, @scan, 'Aws', 'finding', 'retained-evidence', 'operations-account', '{}')
            """);
            recordCommand.Parameters.AddWithValue("tenant", tenant);
            recordCommand.Parameters.AddWithValue("org", organization);
            recordCommand.Parameters.AddWithValue("scan", retainedScanId);
            batch.BatchCommands.Add(recordCommand);

            var eventCommand = new NpgsqlBatchCommand("""
            INSERT INTO SharedDriftEvents (TenantId, OrganizationId, EventId, DetectedAtUtc, PayloadJson)
            VALUES (@tenant, @org, @event1, now() - interval '100 days', '{}'),
                   (@tenant, @org, @event2, now() - interval '100 days', '{}')
            """);
            eventCommand.Parameters.AddWithValue("tenant", tenant);
            eventCommand.Parameters.AddWithValue("org", organization);
            eventCommand.Parameters.AddWithValue("event1", deliveredEventId);
            eventCommand.Parameters.AddWithValue("event2", failedEventId);
            batch.BatchCommands.Add(eventCommand);

            var deliveryCommand = new NpgsqlBatchCommand("""
            INSERT INTO SharedNotificationDeliveries
                (TenantId, OrganizationId, DeliveryId, EventId, RuleId, Channel, ConnectorKind, Status,
                 AttemptCount, CreatedAtUtc, CompletedAtUtc)
            VALUES (@tenant, @org, @delivery1, @event1, @rule, 'webhook', 'SignedWebhook', 'Delivered', 1,
                    now() - interval '100 days', now() - interval '100 days'),
                   (@tenant, @org, @delivery2, @event2, @rule, 'webhook', 'SignedWebhook', 'Failed', 1,
                    now() - interval '100 days', now() - interval '100 days')
            """);
            deliveryCommand.Parameters.AddWithValue("tenant", tenant);
            deliveryCommand.Parameters.AddWithValue("org", organization);
            deliveryCommand.Parameters.AddWithValue("delivery1", deliveredId);
            deliveryCommand.Parameters.AddWithValue("delivery2", failedId);
            deliveryCommand.Parameters.AddWithValue("event1", deliveredEventId);
            deliveryCommand.Parameters.AddWithValue("event2", failedEventId);
            deliveryCommand.Parameters.AddWithValue("rule", Guid.NewGuid());
            batch.BatchCommands.Add(deliveryCommand);

            var attemptCommand = new NpgsqlBatchCommand("""
            INSERT INTO SharedNotificationAttempts
                (TenantId, OrganizationId, DeliveryId, AttemptId, AttemptNumber, StartedAtUtc,
                 CompletedAtUtc, HttpStatus, FailureCode)
            VALUES (@tenant, @org, @delivery1, @attempt1, 1, now() - interval '100 days',
                    now() - interval '100 days', 200, NULL),
                   (@tenant, @org, @delivery2, @attempt2, 1, now(),
                    now(), 503, 'http_rejected')
            """);
            attemptCommand.Parameters.AddWithValue("tenant", tenant);
            attemptCommand.Parameters.AddWithValue("org", organization);
            attemptCommand.Parameters.AddWithValue("delivery1", deliveredId);
            attemptCommand.Parameters.AddWithValue("delivery2", failedId);
            attemptCommand.Parameters.AddWithValue("attempt1", Guid.NewGuid());
            attemptCommand.Parameters.AddWithValue("attempt2", Guid.NewGuid());
            batch.BatchCommands.Add(attemptCommand);

            var jobCommand = new NpgsqlBatchCommand("""
            UPDATE SharedScanJobs SET Status='Succeeded', CompletedAtUtc=now() - interval '100 days'
            WHERE TenantId=@tenant AND OrganizationId=@org AND JobId=@job
            """);
            jobCommand.Parameters.AddWithValue("tenant", tenant);
            jobCommand.Parameters.AddWithValue("org", organization);
            jobCommand.Parameters.AddWithValue("job", completed.JobId);
            batch.BatchCommands.Add(jobCommand);
            await batch.ExecuteNonQueryAsync();
        }

        var snapshot = await store.GetOperationsSnapshotAsync();
        Assert.Equal(1, snapshot.ActiveScanJobs);
        Assert.Equal(1, snapshot.QueuedScanJobs);
        Assert.Equal(0, snapshot.SuccessfulScans24Hours);
        Assert.Equal(1, snapshot.ConnectorFailures24Hours);
        Assert.Equal(1, snapshot.DeadLetterDeliveries);
        var pruned = await store.PruneOperationalHistoryAsync(DateTime.UtcNow.AddDays(-90), 1);
        Assert.Equal(1, pruned.ScanJobs);
        Assert.Equal(1, pruned.Attempts);

        await using var verify = new NpgsqlCommand("""
            SELECT
              (SELECT count(*) FROM SharedCiemScanRecords WHERE TenantId=@tenant AND OrganizationId=@org AND ScanId=@scan),
              (SELECT count(*) FROM SharedScanJobs WHERE TenantId=@tenant AND OrganizationId=@org AND JobId=@job),
              (SELECT count(*) FROM SharedNotificationDeliveries WHERE TenantId=@tenant AND OrganizationId=@org
                AND DeliveryId=@delivered AND Status='Delivered'),
              (SELECT count(*) FROM SharedNotificationAttempts WHERE TenantId=@tenant AND OrganizationId=@org
                AND DeliveryId=@delivered),
              (SELECT count(*) FROM SharedNotificationDeliveries WHERE TenantId=@tenant AND OrganizationId=@org
                AND DeliveryId=@failed AND Status='Failed'),
              (SELECT count(*) FROM SharedNotificationAttempts WHERE TenantId=@tenant AND OrganizationId=@org
                AND DeliveryId=@failed),
              (SELECT count(*) FROM SharedScanJobs WHERE TenantId=@tenant AND OrganizationId=@org
                AND JobId=@queuedJob AND Status='Queued');
            """, connection);
        verify.Parameters.AddWithValue("tenant", tenant);
        verify.Parameters.AddWithValue("org", organization);
        verify.Parameters.AddWithValue("scan", retainedScanId);
        verify.Parameters.AddWithValue("job", completed.JobId);
        verify.Parameters.AddWithValue("queuedJob", queued.JobId);
        verify.Parameters.AddWithValue("delivered", deliveredId);
        verify.Parameters.AddWithValue("failed", failedId);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(0L, reader.GetInt64(1));
        Assert.Equal(1L, reader.GetInt64(2));
        Assert.Equal(0L, reader.GetInt64(3));
        Assert.Equal(1L, reader.GetInt64(4));
        Assert.Equal(1L, reader.GetInt64(5));
        Assert.Equal(1L, reader.GetInt64(6));
    }

    [PostgreSqlFact]
    public async Task OperationsReadiness_ShouldRequireDatabaseTelemetryQueueAndWorkerHealth()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();

        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var metrics = new OperationsMetrics();
        metrics.SetSnapshot(new OperationsSnapshot(
            ActiveScanJobs: 1, QueuedScanJobs: 1, OldestQueuedScanSeconds: 10,
            IngestionLagSeconds: 10, LastScanCompletionAgeSeconds: 10,
            SuccessfulScans24Hours: 1, PartialScans24Hours: 0, FailedScans24Hours: 0,
            PendingDeliveries: 0, DeadLetterDeliveries: 0, ConnectorFailures24Hours: 0,
            CollectedAtUtc: DateTime.UtcNow));
        var workers = new WorkerHealthRegistry();
        foreach (var name in new[] { "scan", "notifications", "governance", "operations" })
            workers.Heartbeat(name, success: true);
        workers.Heartbeat("retention", success: true);
        var healthCheck = new OperationsReadinessHealthCheck(
            store, metrics, workers, configuration, NullLogger<OperationsReadinessHealthCheck>.Instance);

        var healthy = await healthCheck.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, healthy.Status);

        configuration["Operations:Readiness:MaxQueueDepth"] = "0";
        var overloaded = await healthCheck.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, overloaded.Status);
        Assert.Contains("queue depth", overloaded.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrometheusMetrics_ShouldUseOnlyBoundedRouteTemplatesAndKeepNoTenantLabels()
    {
        using var metrics = new OperationsMetrics();
        metrics.RecordApiRequest("GET", "/api/v1/organizations/{organizationId:guid}/findings", 200, 3.5);
        metrics.RecordApiRequest("CUSTOM-UNBOUNDED", "/api/{unmatched}", 500, 12);
        var text = metrics.ToPrometheusText();
        Assert.Contains("yask_api_requests_total{method=\"GET\",route=\"/api/v1/organizations/{organizationId:guid}/findings\"} 1", text);
        Assert.Contains("method=\"OTHER\"", text);
        Assert.DoesNotContain("tenantId", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CUSTOM-UNBOUNDED", text);
        Assert.Contains("yask_scan_success_rate_24h 0", text);
    }

    [Fact]
    public void WorkerHealthRegistry_ShouldTrackSuccessAndFailureWithoutLosingLastSuccess()
    {
        var registry = new WorkerHealthRegistry();
        registry.Heartbeat("scan", failed: true);
        var failed = registry.Snapshot()["scan"];
        registry.Heartbeat("scan", success: true);
        var recovered = registry.Snapshot()["scan"];

        Assert.Equal(1, failed.Failures);
        Assert.Null(failed.LastSuccessUtc);
        Assert.Equal(1, recovered.Failures);
        Assert.NotNull(recovered.LastSuccessUtc);
        Assert.True(recovered.LastHeartbeatUtc >= failed.LastHeartbeatUtc);
    }
}
