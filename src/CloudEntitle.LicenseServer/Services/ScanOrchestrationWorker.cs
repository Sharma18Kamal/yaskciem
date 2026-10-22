using CloudEntitle.Integration.Contracts;

namespace CloudEntitle.LicenseServer.Services;

public sealed class ScanOrchestrationWorker(
    SharedWorkspaceStore store,
    IScanJobExecutor executor,
    ILogger<ScanOrchestrationWorker> logger,
    WorkerHealthRegistry workers,
    OperationsMetrics metrics) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await store.EnqueueDueScanSchedulesAsync(stoppingToken);
                var lease = await store.ClaimNextScanJobAsync(stoppingToken);
                workers.Heartbeat("scan", success: true);
                if (lease is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
                    continue;
                }

                await ExecuteLeaseAsync(lease, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The scheduled scan queue worker encountered an error.");
                workers.Heartbeat("scan", failed: true);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ExecuteLeaseAsync(ScanJobLease lease, CancellationToken stoppingToken)
    {
        using var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var execution = executor.ExecuteAsync(lease, jobCancellation.Token);
        ScanJobExecutionResult? result = null;
        try
        {
            while (!execution.IsCompleted)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                workers.Heartbeat("scan", success: true);
                if (await store.IsScanJobCancellationRequestedAsync(lease.Job, stoppingToken))
                {
                    jobCancellation.Cancel();
                    result = new ScanJobExecutionResult("Cancelled", null, "Cancellation requested by an organization operator.");
                    try { await execution; }
                    catch (OperationCanceledException) { }
                    catch (Exception exception)
                    {
                        logger.LogWarning(exception, "Scheduled scan executor failed while job {JobId} was being cancelled.", lease.Job.JobId);
                    }
                    break;
                }
                await store.RenewScanJobLeaseAsync(lease, stoppingToken);
            }

            if (execution.IsCompleted)
            {
                try
                {
                    result = await execution;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    result = new ScanJobExecutionResult("Failed", null, "Worker is shutting down; the scan will be retried if attempts remain.");
                }
                catch (OperationCanceledException)
                {
                    result = new ScanJobExecutionResult("Cancelled", null, "Scan execution was cancelled.");
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Scheduled scan executor failed for job {JobId}", lease.Job.JobId);
                    result = new ScanJobExecutionResult("Failed", null, exception.Message);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            jobCancellation.Cancel();
            try { await execution; }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Scheduled scan execution stopped during worker shutdown for job {JobId}.", lease.Job.JobId);
            }
            result = new ScanJobExecutionResult("Failed", null, "Worker is shutting down; the scan will be retried if attempts remain.");
        }
        catch
        {
            jobCancellation.Cancel();
            try { await execution; }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Scheduled scan execution stopped after job {JobId} lost its lease.", lease.Job.JobId);
            }
            throw;
        }

        result ??= new ScanJobExecutionResult("Failed", null, "Worker execution ended without a result.");
        await store.CompleteScanJobAttemptAsync(lease, result.Outcome, result.Error, result.ScanId, CancellationToken.None);
        metrics.RecordScanOutcome(lease.Job.Provider.ToString(), result.Outcome);
        workers.Heartbeat("scan", success: true);
    }
}
