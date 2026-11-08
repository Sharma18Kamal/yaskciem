using System.Text.Json;

namespace CloudEntitle.LicenseServer.Services;

public sealed class ContinuousChangeFeedWorker(SharedWorkspaceStore store, IConfiguration configuration,
    IContinuousChangeBridge bridge, ILogger<ContinuousChangeFeedWorker> logger, WorkerHealthRegistry workers) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var lease = await store.ClaimContinuousFeedAsync(stoppingToken);
                if (lease is null)
                {
                    workers.Heartbeat("continuous-change-feeds", success: true);
                    await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
                    continue;
                }
                await PollLeaseAsync(lease, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                // Exception bodies may include transport secrets. Log safe scope/code only.
                logger.LogWarning("Continuous cloud change queue failed; retrying after bounded delay.");
                workers.Heartbeat("continuous-change-feeds", failed: true);
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    public async Task PollLeaseAsync(ContinuousChangeLease lease, CancellationToken token)
    {
        try
        {
            var source = ContinuousChangeSourceConfiguration.Resolve(configuration, lease.TenantId, lease.OrganizationId, lease.State.Alias);
            if (source is null) throw new ChangeBridgeException("server_source_disabled");
            if (source.ConfigurationSha256 != lease.ConfigurationSha256) throw new ChangeBridgeException("source_reset_required");
            if (!await store.IsContinuousFeedLeaseValidAsync(lease, token)) throw new ChangeBridgeException("scope_or_lease_revoked");
            var batch = await bridge.PollAsync(source, lease.State, token);
            // Live configuration may have been revoked/replaced while the request was in flight.
            var current = ContinuousChangeSourceConfiguration.Resolve(configuration, lease.TenantId, lease.OrganizationId, lease.State.Alias);
            if (current?.ConfigurationSha256 != source.ConfigurationSha256) throw new ChangeBridgeException("source_changed_during_poll");
            await store.CommitContinuousBatchAsync(lease, batch, source.PollSeconds, token);
            workers.Heartbeat("continuous-change-feeds", success: true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            var code = exception switch
            {
                ChangeBridgeException known => known.Code,
                OperationCanceledException => "bridge_timeout_uncertain",
                HttpRequestException => "bridge_transport_uncertain",
                JsonException or ArgumentException => "bridge_invalid_response",
                UnauthorizedAccessException => "scope_not_active",
                InvalidOperationException => "cursor_or_lease_conflict",
                _ => "persistence_uncertain"
            };
            await store.FailContinuousFeedAsync(lease, code, token);
            workers.Heartbeat("continuous-change-feeds", failed: true);
            logger.LogWarning("Continuous cloud change feed {Alias} attempt failed with {Code}; cursor retained.",
                lease.State.Alias, code);
        }
    }
}
