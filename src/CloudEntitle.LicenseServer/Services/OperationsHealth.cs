using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CloudEntitle.LicenseServer.Services;

public sealed class OperationsReadinessHealthCheck(
    SharedWorkspaceStore store,
    OperationsMetrics metrics,
    WorkerHealthRegistry workers,
    IConfiguration configuration,
    ILogger<OperationsReadinessHealthCheck> logger) : IHealthCheck
{
    private static readonly TimeSpan SnapshotFreshness = TimeSpan.FromSeconds(60);
    private static readonly IReadOnlyDictionary<string, TimeSpan> WorkerFreshness = new Dictionary<string, TimeSpan>
    {
        ["scan"] = TimeSpan.FromSeconds(45),
        ["notifications"] = TimeSpan.FromSeconds(45),
        ["governance"] = TimeSpan.FromSeconds(90),
        ["operations"] = TimeSpan.FromSeconds(60),
        ["retention"] = TimeSpan.FromHours(36)
    };

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        try
        {
            if (!await store.CheckOperationsReadinessAsync(cancellationToken))
                errors.Add("PostgreSQL operations schema or required indexes unavailable.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            logger.LogError("Operations readiness database check failed ({ErrorType}).", exception.GetType().Name);
            errors.Add("PostgreSQL operations checks failed.");
        }

        var snapshot = metrics.Current;
        if (snapshot is null || DateTime.UtcNow - snapshot.CollectedAtUtc > SnapshotFreshness)
            errors.Add("Operations telemetry snapshot is unavailable or stale.");
        else if (snapshot.OldestQueuedScanSeconds > configuration.GetValue("Operations:Readiness:MaxQueueAgeSeconds", 3600))
            errors.Add("Scan queue age exceeds the configured readiness threshold.");
        else if (snapshot.QueuedScanJobs > configuration.GetValue("Operations:Readiness:MaxQueueDepth", 1000))
            errors.Add("Scan queue depth exceeds the configured readiness threshold.");

        var now = DateTime.UtcNow;
        var heartbeats = workers.Snapshot();
        foreach (var (name, maximumAge) in WorkerFreshness)
        {
            if (!heartbeats.TryGetValue(name, out var heartbeat)
                || now - heartbeat.LastHeartbeatUtc > maximumAge
                || name == "retention" && (heartbeat.LastSuccessUtc is null || now - heartbeat.LastSuccessUtc > maximumAge))
                errors.Add($"Worker '{name}' heartbeat is unavailable or stale.");
        }

        var data = new Dictionary<string, object>
        {
            ["database"] = errors.Any(error => error.StartsWith("PostgreSQL", StringComparison.Ordinal)) ? "unavailable" : "ready",
            ["telemetryCollectedAtUtc"] = snapshot?.CollectedAtUtc.ToString("O") ?? "unavailable",
            ["workers"] = heartbeats.ToDictionary(pair => pair.Key, pair => new
            {
                lastHeartbeatUtc = pair.Value.LastHeartbeatUtc,
                failures = pair.Value.Failures,
                lastSuccessUtc = pair.Value.LastSuccessUtc
            })
        };
        return errors.Count == 0
            ? HealthCheckResult.Healthy("Database, operations indexes, queue, telemetry sampler and background worker heartbeats are healthy.", data)
            : HealthCheckResult.Unhealthy(string.Join(" ", errors), data: data);
    }
}

public sealed class OperationsTelemetrySampler(
    SharedWorkspaceStore store,
    OperationsMetrics metrics,
    WorkerHealthRegistry workers,
    ILogger<OperationsTelemetrySampler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                metrics.SetSnapshot(await store.GetOperationsSnapshotAsync(stoppingToken));
                workers.Heartbeat("operations", success: true);
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                workers.Heartbeat("operations", failed: true);
                logger.LogError("Operations telemetry sampling failed ({ErrorType}).", exception.GetType().Name);
                try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }
}

public sealed class OperationsRetentionWorker(
    SharedWorkspaceStore store,
    IConfiguration configuration,
    WorkerHealthRegistry workers,
    ILogger<OperationsRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retentionDays = configuration.GetValue("Operations:RetentionDays", 90);
        if (retentionDays is < 30 or > 3650)
            throw new InvalidOperationException("Operations:RetentionDays must be between 30 and 3650 days.");
        var batchSize = configuration.GetValue("Operations:RetentionBatchSize", 500);
        if (batchSize is < 1 or > 5000)
            throw new InvalidOperationException("Operations:RetentionBatchSize must be between 1 and 5000.");
        var maxBatchesPerRun = configuration.GetValue("Operations:RetentionMaxBatchesPerRun", 20);
        if (maxBatchesPerRun is < 1 or > 100)
            throw new InvalidOperationException("Operations:RetentionMaxBatchesPerRun must be between 1 and 100.");

        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                long totalScanJobs = 0;
                long totalAttempts = 0;
                var moreEligibleRowsMayRemain = false;
                for (var batchNumber = 0; batchNumber < maxBatchesPerRun; batchNumber++)
                {
                    var deleted = await store.PruneOperationalHistoryAsync(
                        DateTime.UtcNow.AddDays(-retentionDays), batchSize, stoppingToken);
                    totalScanJobs += deleted.ScanJobs;
                    totalAttempts += deleted.Attempts;
                    moreEligibleRowsMayRemain = deleted.ScanJobs >= batchSize || deleted.Attempts >= batchSize;
                    if (!moreEligibleRowsMayRemain) break;
                }

                workers.Heartbeat("retention", success: true);
                logger.LogInformation("Operational retention removed {ScanJobs} terminal scan jobs and {Attempts} completed attempts for delivered/suppressed notifications older than {RetentionDays} days.",
                    totalScanJobs, totalAttempts, retentionDays);
                if (moreEligibleRowsMayRemain)
                    logger.LogWarning("Operational retention reached its {BatchLimit}-batch daily limit; additional eligible rows will be processed on the next run.",
                        maxBatchesPerRun);
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                workers.Heartbeat("retention", failed: true);
                logger.LogError("Operational retention failed ({ErrorType}); diagnostic and evidence history is unchanged.", exception.GetType().Name);
                try { await timer.WaitForNextTickAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }
}
