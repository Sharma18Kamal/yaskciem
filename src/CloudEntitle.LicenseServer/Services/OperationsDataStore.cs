using Npgsql;

namespace CloudEntitle.LicenseServer.Services;

public sealed record OperationalHistoryPruneResult(long ScanJobs, long Attempts);

public sealed partial class SharedWorkspaceStore
{
    public async Task<OperationsSnapshot> GetOperationsSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT
              (SELECT count(*) FROM SharedScanJobs WHERE Status IN ('Queued', 'Running')),
              (SELECT count(*) FROM SharedScanJobs WHERE Status='Queued'),
              COALESCE((SELECT greatest(0, extract(epoch FROM (now() - min(QueuedAtUtc)))::double precision)
                FROM SharedScanJobs WHERE Status='Queued'), 0::double precision),
              (SELECT greatest(0, max(extract(epoch FROM (CompletedAtUtc - QueuedAtUtc)))::double precision)
                FROM SharedScanJobs
                WHERE CompletedAtUtc >= now() - interval '24 hours' AND Status IN ('Succeeded', 'Partial')),
              (SELECT greatest(0, extract(epoch FROM (now() - max(CompletedAtUtc)))::double precision)
                FROM SharedCiemScans),
              (SELECT count(*) FROM SharedScanJobs
                WHERE CompletedAtUtc >= now() - interval '24 hours' AND Status='Succeeded'),
              (SELECT count(*) FROM SharedScanJobs
                WHERE CompletedAtUtc >= now() - interval '24 hours' AND Status='Partial'),
              (SELECT count(*) FROM SharedScanJobs
                WHERE CompletedAtUtc >= now() - interval '24 hours' AND Status='Failed'),
              (SELECT count(*) FROM SharedNotificationDeliveries WHERE Status IN ('Pending', 'Sending')),
              (SELECT count(*) FROM SharedNotificationDeliveries WHERE Status='Failed'),
              (SELECT count(*) FROM SharedNotificationAttempts
                WHERE StartedAtUtc >= now() - interval '24 hours'
                  AND (FailureCode IS NOT NULL OR (HttpStatus IS NOT NULL AND HttpStatus >= 400)));
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("PostgreSQL did not return the operations metrics snapshot.");
        return new OperationsSnapshot(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetDouble(2),
            reader.IsDBNull(3) ? null : reader.GetDouble(3),
            reader.IsDBNull(4) ? null : reader.GetDouble(4),
            reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7),
            reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10), DateTime.UtcNow);
    }

    public async Task<bool> CheckOperationsReadinessAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("""
                SELECT to_regclass('sharedscanjobs') IS NOT NULL
                  AND to_regclass('sharedscanjobattempts') IS NOT NULL
                  AND to_regclass('sharednotificationdeliveries') IS NOT NULL
                  AND to_regclass('sharednotificationattempts') IS NOT NULL
                  AND to_regclass('ix_sharedscanjobs_queue') IS NOT NULL
                  AND to_regclass('ix_sharedscanjobs_org_queued') IS NOT NULL
                  AND to_regclass('ix_sharedscanjobs_retention') IS NOT NULL
                  AND to_regclass('ix_sharednotificationdeliveries_queue') IS NOT NULL
                  AND to_regclass('ix_sharednotificationattempts_retention') IS NOT NULL
                  AND to_regclass('ix_sharednotificationattempts_failuretime') IS NOT NULL
                  AND to_regprocedure('yask_prune_operational_history(timestamp with time zone,integer)') IS NOT NULL
                  AND has_function_privilege(current_user,
                      'yask_prune_operational_history(timestamp with time zone,integer)', 'EXECUTE')
                  AND (SELECT count(*) = 6 FROM pg_index
                       WHERE indexrelid IN (
                         to_regclass('ix_sharedscanjobs_queue'), to_regclass('ix_sharedscanjobs_org_queued'),
                         to_regclass('ix_sharedscanjobs_retention'), to_regclass('ix_sharednotificationdeliveries_queue'),
                         to_regclass('ix_sharednotificationattempts_retention'),
                         to_regclass('ix_sharednotificationattempts_failuretime'))
                         AND indisvalid AND indisready);
                """, connection);
            return await command.ExecuteScalarAsync(cancellationToken) as bool? ?? false;
        }
        catch (OperationCanceledException) { throw; }
        catch (NpgsqlException) { return false; }
    }

    public async Task<OperationalHistoryPruneResult> PruneOperationalHistoryAsync(
        DateTime cutoffUtc, int batchSize, CancellationToken cancellationToken = default)
    {
        if (cutoffUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("Retention cutoff must be UTC.", nameof(cutoffUtc));
        if (batchSize is < 1 or > 5000) throw new ArgumentOutOfRangeException(nameof(batchSize));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT deleted_scan_jobs, deleted_notification_attempts FROM yask_prune_operational_history(@cutoff, @batch);",
            connection);
        command.Parameters.AddWithValue("cutoff", cutoffUtc);
        command.Parameters.AddWithValue("batch", batchSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("PostgreSQL did not return the operations retention result.");
        return new(reader.GetInt64(0), reader.GetInt64(1));
    }
}
