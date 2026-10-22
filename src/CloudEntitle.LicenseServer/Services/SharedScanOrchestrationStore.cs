using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
using Npgsql;

namespace CloudEntitle.LicenseServer.Services;

public sealed record ScanJobLease(ScanJobDto Job, Guid AttemptId);
public sealed class TenantResourceLimitException(string resource)
    : InvalidOperationException($"The organization's tenant has reached its configured {resource} limit.");

public sealed partial class SharedWorkspaceStore
{
    public async Task<ScanScheduleDto?> CreateScanScheduleAsync(
        Guid tenantId,
        Guid organizationId,
        CreateScanScheduleRequest request,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var schedule = new ScanScheduleDto(Guid.NewGuid(), tenantId, organizationId, request.Provider,
            request.ExternalAccountId.Trim(), request.DisplayName.Trim(), request.IntervalMinutes, request.MaxRetries,
            true, now.AddMinutes(request.IntervalMinutes), now, now, null);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockScanQuotaAsync(connection, transaction, tenantId, cancellationToken);
        await using (var existing = new NpgsqlCommand("""
            SELECT 1 FROM SharedScanSchedules
            WHERE TenantId=@tenant AND OrganizationId=@organization AND Provider=@provider AND ExternalAccountId=@account;
            """, connection, transaction))
        {
            existing.Parameters.AddWithValue("tenant", tenantId);
            existing.Parameters.AddWithValue("organization", organizationId);
            existing.Parameters.AddWithValue("provider", request.Provider.ToString());
            existing.Parameters.AddWithValue("account", request.ExternalAccountId.Trim());
            if (await existing.ExecuteScalarAsync(cancellationToken) is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return null;
            }
        }
        await using (var count = new NpgsqlCommand(
            "SELECT count(*) FROM SharedScanSchedules WHERE TenantId=@tenant;", connection, transaction))
        {
            count.Parameters.AddWithValue("tenant", tenantId);
            if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture)
                >= _maxSchedulesPerTenant)
                throw new TenantResourceLimitException("scan schedule");
        }
        await using var command = new NpgsqlCommand("""
            INSERT INTO SharedScanSchedules
                (TenantId, OrganizationId, ScheduleId, Provider, ExternalAccountId, DisplayName, IntervalMinutes,
                 MaxRetries, Enabled, NextRunAtUtc, CreatedAtUtc, UpdatedAtUtc)
            VALUES (@tenant, @organization, @schedule, @provider, @account, @display, @interval, @retries,
                    true, @nextRun, @created, @updated)
            ON CONFLICT (TenantId, OrganizationId, Provider, ExternalAccountId) DO NOTHING;
            """, connection, transaction);
        AddScheduleParameters(command, schedule);
        var inserted = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return inserted == 1 ? schedule : null;
    }

    public async Task<IReadOnlyList<ScanScheduleDto>> GetScanSchedulesAsync(
        Guid tenantId, Guid organizationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT ScheduleId, TenantId, OrganizationId, Provider, ExternalAccountId, DisplayName,
                   IntervalMinutes, MaxRetries, Enabled, NextRunAtUtc, CreatedAtUtc, UpdatedAtUtc, LastSuccessfulScanAtUtc
            FROM SharedScanSchedules
            WHERE TenantId=@tenant AND OrganizationId=@organization
            ORDER BY CreatedAtUtc DESC;
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("organization", organizationId);
        var schedules = new List<ScanScheduleDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) schedules.Add(ReadSchedule(reader));
        return schedules;
    }

    public async Task<ScanScheduleDto?> UpdateScanScheduleAsync(
        Guid tenantId,
        Guid organizationId,
        Guid scheduleId,
        UpdateScanScheduleRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE SharedScanSchedules
            SET Enabled=@enabled, IntervalMinutes=@interval, MaxRetries=@retries,
                NextRunAtUtc=CASE WHEN @enabled THEN @nextRun ELSE NextRunAtUtc END, UpdatedAtUtc=@updated
            WHERE TenantId=@tenant AND OrganizationId=@organization AND ScheduleId=@schedule
            RETURNING ScheduleId, TenantId, OrganizationId, Provider, ExternalAccountId, DisplayName,
                      IntervalMinutes, MaxRetries, Enabled, NextRunAtUtc, CreatedAtUtc, UpdatedAtUtc, LastSuccessfulScanAtUtc;
            """, connection);
        command.Parameters.AddWithValue("enabled", request.Enabled);
        command.Parameters.AddWithValue("interval", request.IntervalMinutes);
        command.Parameters.AddWithValue("retries", request.MaxRetries);
        command.Parameters.AddWithValue("nextRun", DateTime.UtcNow.AddMinutes(request.IntervalMinutes));
        command.Parameters.AddWithValue("updated", DateTime.UtcNow);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("organization", organizationId);
        command.Parameters.AddWithValue("schedule", scheduleId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSchedule(reader) : null;
    }

    public async Task<ScanJobDto?> QueueScanNowAsync(
        Guid tenantId, Guid organizationId, Guid scheduleId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockScanQuotaAsync(connection, transaction, tenantId, cancellationToken);
        await using (var count = new NpgsqlCommand("""
            SELECT count(*) FROM SharedScanJobs WHERE TenantId=@tenant AND Status IN ('Queued', 'Running');
            """, connection, transaction))
        {
            count.Parameters.AddWithValue("tenant", tenantId);
            if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture)
                >= _maxActiveScanJobsPerTenant)
            {
                await using var existing = new NpgsqlCommand("""
                    SELECT JobId, ScheduleId, TenantId, OrganizationId, Provider, ExternalAccountId, DisplayName, Status,
                           AttemptCount, MaxRetries, QueuedAtUtc, StartedAtUtc, CompletedAtUtc, NextAttemptAtUtc,
                           DurationMilliseconds, ScanId, CancellationRequested, LastError
                    FROM SharedScanJobs
                    WHERE TenantId=@tenant AND OrganizationId=@organization AND ScheduleId=@schedule
                      AND Status IN ('Queued', 'Running')
                    ORDER BY QueuedAtUtc DESC LIMIT 1;
                    """, connection, transaction);
                existing.Parameters.AddWithValue("tenant", tenantId);
                existing.Parameters.AddWithValue("organization", organizationId);
                existing.Parameters.AddWithValue("schedule", scheduleId);
                await using var existingReader = await existing.ExecuteReaderAsync(cancellationToken);
                if (await existingReader.ReadAsync(cancellationToken))
                {
                    var activeJob = ReadJob(existingReader);
                    await existingReader.CloseAsync();
                    await transaction.CommitAsync(cancellationToken);
                    return activeJob;
                }
                throw new TenantResourceLimitException("active scan job");
            }
        }
        await using var enqueue = new NpgsqlCommand("""
            INSERT INTO SharedScanJobs
                (TenantId, OrganizationId, JobId, ScheduleId, Provider, ExternalAccountId, DisplayName,
                 Status, AttemptCount, MaxRetries, QueuedAtUtc)
            SELECT TenantId, OrganizationId, gen_random_uuid(), ScheduleId, Provider, ExternalAccountId,
                   DisplayName, 'Queued', 0, MaxRetries, @now
            FROM SharedScanSchedules
            WHERE TenantId=@tenant AND OrganizationId=@organization AND ScheduleId=@schedule
            ON CONFLICT DO NOTHING;
            """, connection, transaction);
        enqueue.Parameters.AddWithValue("now", DateTime.UtcNow);
        enqueue.Parameters.AddWithValue("tenant", tenantId);
        enqueue.Parameters.AddWithValue("organization", organizationId);
        enqueue.Parameters.AddWithValue("schedule", scheduleId);
        var inserted = await enqueue.ExecuteNonQueryAsync(cancellationToken);
        await using var read = new NpgsqlCommand("""
            SELECT JobId, ScheduleId, TenantId, OrganizationId, Provider, ExternalAccountId, DisplayName, Status,
                   AttemptCount, MaxRetries, QueuedAtUtc, StartedAtUtc, CompletedAtUtc, NextAttemptAtUtc,
                   DurationMilliseconds, ScanId, CancellationRequested, LastError
            FROM SharedScanJobs
            WHERE TenantId=@tenant AND OrganizationId=@organization AND ScheduleId=@schedule
              AND Status IN ('Queued', 'Running')
            ORDER BY QueuedAtUtc DESC LIMIT 1;
            """, connection, transaction);
        read.Parameters.AddWithValue("tenant", tenantId);
        read.Parameters.AddWithValue("organization", organizationId);
        read.Parameters.AddWithValue("schedule", scheduleId);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        var job = await reader.ReadAsync(cancellationToken) ? ReadJob(reader) : null;
        await reader.CloseAsync();
        await transaction.CommitAsync(cancellationToken);
        return inserted == 0 && job is null ? null : job;
    }

    public async Task<int> EnqueueDueScanSchedulesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var dueSchedules = new List<ScanScheduleDto>();
        await using (var due = new NpgsqlCommand("""
            SELECT ScheduleId, TenantId, OrganizationId, Provider, ExternalAccountId, DisplayName,
                   IntervalMinutes, MaxRetries, Enabled, NextRunAtUtc, CreatedAtUtc, UpdatedAtUtc, LastSuccessfulScanAtUtc
            FROM SharedScanSchedules
            WHERE Enabled AND NextRunAtUtc <= @now
            ORDER BY NextRunAtUtc
            FOR UPDATE SKIP LOCKED
            LIMIT 50;
            """, connection, transaction))
        {
            due.Parameters.AddWithValue("now", DateTime.UtcNow);
            await using var reader = await due.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) dueSchedules.Add(ReadSchedule(reader));
        }

        foreach (var tenant in dueSchedules.Select(schedule => schedule.TenantId).Distinct().Order())
            await LockScanQuotaAsync(connection, transaction, tenant, cancellationToken);
        var activeByTenant = new Dictionary<Guid, int>();
        foreach (var tenant in dueSchedules.Select(schedule => schedule.TenantId).Distinct())
        {
            await using var count = new NpgsqlCommand(
                "SELECT count(*) FROM SharedScanJobs WHERE TenantId=@tenant AND Status IN ('Queued', 'Running');",
                connection, transaction);
            count.Parameters.AddWithValue("tenant", tenant);
            activeByTenant[tenant] = checked((int)Convert.ToInt64(
                await count.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture));
        }

        var enqueued = 0;
        foreach (var schedule in dueSchedules)
        {
            if (activeByTenant[schedule.TenantId] < _maxActiveScanJobsPerTenant)
            {
                await using var insert = new NpgsqlCommand("""
                INSERT INTO SharedScanJobs
                    (TenantId, OrganizationId, JobId, ScheduleId, Provider, ExternalAccountId, DisplayName,
                     Status, AttemptCount, MaxRetries, QueuedAtUtc)
                VALUES (@tenant, @organization, gen_random_uuid(), @schedule, @provider, @account, @display,
                        'Queued', 0, @retries, @now)
                ON CONFLICT DO NOTHING;
                """, connection, transaction);
                insert.Parameters.AddWithValue("tenant", schedule.TenantId);
                insert.Parameters.AddWithValue("organization", schedule.OrganizationId);
                insert.Parameters.AddWithValue("schedule", schedule.ScheduleId);
                insert.Parameters.AddWithValue("provider", schedule.Provider.ToString());
                insert.Parameters.AddWithValue("account", schedule.ExternalAccountId);
                insert.Parameters.AddWithValue("display", schedule.DisplayName);
                insert.Parameters.AddWithValue("retries", schedule.MaxRetries);
                insert.Parameters.AddWithValue("now", DateTime.UtcNow);
                var inserted = await insert.ExecuteNonQueryAsync(cancellationToken);
                enqueued += inserted;
                activeByTenant[schedule.TenantId] += inserted;
            }

            await using var update = new NpgsqlCommand("""
                UPDATE SharedScanSchedules SET NextRunAtUtc=@nextRun, UpdatedAtUtc=@now
                WHERE TenantId=@tenant AND OrganizationId=@organization AND ScheduleId=@schedule;
                """, connection, transaction);
            var now = DateTime.UtcNow;
            update.Parameters.AddWithValue("nextRun", now.AddMinutes(schedule.IntervalMinutes));
            update.Parameters.AddWithValue("now", now);
            update.Parameters.AddWithValue("tenant", schedule.TenantId);
            update.Parameters.AddWithValue("organization", schedule.OrganizationId);
            update.Parameters.AddWithValue("schedule", schedule.ScheduleId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return enqueued;
    }

    private static async Task LockScanQuotaAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid tenantId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@tenant, 0));", connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.ToString("D"));
        await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task<ScanJobLease?> ClaimNextScanJobAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var recoveryTime = DateTime.UtcNow;
        await using (var recoverAttempts = new NpgsqlCommand("""
            UPDATE SharedScanJobAttempts a
            SET CompletedAtUtc=@now,
                DurationMilliseconds=GREATEST(0, (EXTRACT(EPOCH FROM (@now - a.StartedAtUtc)) * 1000)::bigint),
                Outcome='Interrupted', Error='Worker lease expired before the attempt completed.'
            FROM SharedScanJobs j
            WHERE a.TenantId=j.TenantId AND a.OrganizationId=j.OrganizationId AND a.JobId=j.JobId
              AND a.CompletedAtUtc IS NULL AND j.Status='Running' AND j.LeaseExpiresAtUtc < @now;
            """, connection, transaction))
        {
            recoverAttempts.Parameters.AddWithValue("now", recoveryTime);
            await recoverAttempts.ExecuteNonQueryAsync(cancellationToken);
        }
        var recoveredFailures = new List<(Guid Tenant, Guid Organization, Guid Job, CloudProvider Provider, string Account)>();
        await using (var recoverJobs = new NpgsqlCommand("""
            UPDATE SharedScanJobs
            SET Status=CASE WHEN CancellationRequested THEN 'Cancelled'
                            WHEN AttemptCount <= MaxRetries THEN 'Queued' ELSE 'Failed' END,
                CompletedAtUtc=CASE WHEN CancellationRequested OR AttemptCount > MaxRetries THEN @now ELSE NULL END,
                NextAttemptAtUtc=CASE WHEN NOT CancellationRequested AND AttemptCount <= MaxRetries THEN @now ELSE NULL END,
                LeaseExpiresAtUtc=NULL, ActiveAttemptId=NULL,
                LastError=CASE WHEN CancellationRequested THEN 'Cancellation requested while the worker lease expired.'
                               ELSE 'Worker lease expired before the attempt completed.' END
            WHERE Status='Running' AND LeaseExpiresAtUtc < @now
            RETURNING TenantId, OrganizationId, JobId, Provider, ExternalAccountId, Status;
            """, connection, transaction))
        {
            recoverJobs.Parameters.AddWithValue("now", recoveryTime);
            await using var reader = await recoverJobs.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (reader.GetString(5) == "Failed")
                    recoveredFailures.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2),
                        Enum.Parse<CloudProvider>(reader.GetString(3)), reader.GetString(4)));
        }
        foreach (var failure in recoveredFailures)
            await RecordScanFailureAsync(connection, transaction, failure.Tenant, failure.Organization, failure.Job,
                failure.Provider, failure.Account, null, recoveryTime, cancellationToken);
        ScanJobDto? job;
        var attemptId = Guid.NewGuid();
        await using (var claim = new NpgsqlCommand("""
            WITH candidate AS (
                SELECT TenantId, OrganizationId, JobId
                FROM SharedScanJobs
                WHERE Status='Queued' AND NOT CancellationRequested
                  AND (NextAttemptAtUtc IS NULL OR NextAttemptAtUtc <= @now)
                ORDER BY QueuedAtUtc
                FOR UPDATE SKIP LOCKED LIMIT 1
            )
            UPDATE SharedScanJobs j
            SET Status='Running', AttemptCount=j.AttemptCount+1, StartedAtUtc=COALESCE(j.StartedAtUtc, @now),
                NextAttemptAtUtc=NULL, LeaseExpiresAtUtc=@lease, ActiveAttemptId=@attempt
            FROM candidate c
            WHERE j.TenantId=c.TenantId AND j.OrganizationId=c.OrganizationId AND j.JobId=c.JobId
            RETURNING j.JobId, j.ScheduleId, j.TenantId, j.OrganizationId, j.Provider, j.ExternalAccountId,
                      j.DisplayName, j.Status, j.AttemptCount, j.MaxRetries, j.QueuedAtUtc, j.StartedAtUtc,
                      j.CompletedAtUtc, j.NextAttemptAtUtc, j.DurationMilliseconds, j.ScanId,
                      j.CancellationRequested, j.LastError;
            """, connection, transaction))
        {
            claim.Parameters.AddWithValue("now", DateTime.UtcNow);
            claim.Parameters.AddWithValue("lease", DateTime.UtcNow.AddMinutes(2));
            claim.Parameters.AddWithValue("attempt", attemptId);
            await using var reader = await claim.ExecuteReaderAsync(cancellationToken);
            job = await reader.ReadAsync(cancellationToken) ? ReadJob(reader) : null;
        }
        if (job is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        await using (var attempt = new NpgsqlCommand("""
            INSERT INTO SharedScanJobAttempts
                (TenantId, OrganizationId, JobId, AttemptId, AttemptNumber, StartedAtUtc, Outcome)
            VALUES (@tenant, @organization, @job, @attempt, @number, @started, 'Running');
            """, connection, transaction))
        {
            attempt.Parameters.AddWithValue("tenant", job.TenantId);
            attempt.Parameters.AddWithValue("organization", job.OrganizationId);
            attempt.Parameters.AddWithValue("job", job.JobId);
            attempt.Parameters.AddWithValue("attempt", attemptId);
            attempt.Parameters.AddWithValue("number", job.AttemptCount);
            attempt.Parameters.AddWithValue("started", DateTime.UtcNow);
            await attempt.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new ScanJobLease(job, attemptId);
    }

    public async Task CompleteScanJobAttemptAsync(
        ScanJobLease lease,
        string outcome,
        string? error,
        Guid? scanId,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var verify = new NpgsqlCommand("""
            SELECT CancellationRequested FROM SharedScanJobs
            WHERE TenantId=@tenant AND OrganizationId=@organization AND JobId=@job
              AND Status='Running' AND ActiveAttemptId=@attempt
            FOR UPDATE;
            """, connection, transaction))
        {
            verify.Parameters.AddWithValue("tenant", lease.Job.TenantId);
            verify.Parameters.AddWithValue("organization", lease.Job.OrganizationId);
            verify.Parameters.AddWithValue("job", lease.Job.JobId);
            verify.Parameters.AddWithValue("attempt", lease.AttemptId);
            var cancellationRequested = await verify.ExecuteScalarAsync(cancellationToken) as bool?
                ?? throw new InvalidOperationException("The scan worker no longer owns this job lease.");
            if (cancellationRequested)
            {
                outcome = "Cancelled";
                error ??= "Cancellation requested by an organization operator.";
            }
        }
        var cancelled = outcome == "Cancelled";
        var retry = outcome == "Failed" && lease.Job.AttemptCount <= lease.Job.MaxRetries && !cancelled;
        var status = retry ? "Queued" : outcome;
        var nextAttempt = retry ? now.AddSeconds(Math.Min(900, 15 * Math.Pow(2, lease.Job.AttemptCount - 1))) : (DateTime?)null;
        await using (var attempt = new NpgsqlCommand("""
            UPDATE SharedScanJobAttempts
            SET CompletedAtUtc=@completed,
                DurationMilliseconds=GREATEST(0, (EXTRACT(EPOCH FROM (@completed - StartedAtUtc)) * 1000)::bigint),
                Outcome=@outcome, Error=@error
            WHERE TenantId=@tenant AND OrganizationId=@organization AND JobId=@job AND AttemptId=@attempt;
            """, connection, transaction))
        {
            attempt.Parameters.AddWithValue("completed", now);
            attempt.Parameters.AddWithValue("outcome", retry ? "Retrying" : outcome);
            attempt.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);
            attempt.Parameters.AddWithValue("tenant", lease.Job.TenantId);
            attempt.Parameters.AddWithValue("organization", lease.Job.OrganizationId);
            attempt.Parameters.AddWithValue("job", lease.Job.JobId);
            attempt.Parameters.AddWithValue("attempt", lease.AttemptId);
            await attempt.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var job = new NpgsqlCommand("""
            UPDATE SharedScanJobs
            SET Status=@status, CompletedAtUtc=CASE WHEN @retry THEN NULL ELSE @completed END,
                NextAttemptAtUtc=@nextAttempt, DurationMilliseconds=CASE WHEN @retry THEN NULL ELSE @duration END,
                ScanId=COALESCE(@scan, ScanId), CancellationRequested=@cancelled, LastError=@error,
                LeaseExpiresAtUtc=NULL, ActiveAttemptId=NULL
            WHERE TenantId=@tenant AND OrganizationId=@organization AND JobId=@job AND ActiveAttemptId=@attempt;
            """, connection, transaction))
        {
            job.Parameters.AddWithValue("status", status);
            job.Parameters.AddWithValue("retry", retry);
            job.Parameters.AddWithValue("completed", now);
            job.Parameters.AddWithValue("nextAttempt", (object?)nextAttempt ?? DBNull.Value);
            job.Parameters.AddWithValue("duration", (long)Math.Max(0, (now - (lease.Job.StartedAtUtc ?? now)).TotalMilliseconds));
            job.Parameters.AddWithValue("scan", (object?)scanId ?? DBNull.Value);
            job.Parameters.AddWithValue("cancelled", cancelled);
            job.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);
            job.Parameters.AddWithValue("tenant", lease.Job.TenantId);
            job.Parameters.AddWithValue("organization", lease.Job.OrganizationId);
            job.Parameters.AddWithValue("job", lease.Job.JobId);
            job.Parameters.AddWithValue("attempt", lease.AttemptId);
            if (await job.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("The scan worker lost ownership of the job before completion.");
        }

        if (status == "Failed")
            await RecordScanFailureAsync(connection, transaction, lease.Job.TenantId, lease.Job.OrganizationId,
                lease.Job.JobId, lease.Job.Provider, lease.Job.ExternalAccountId, scanId, now, cancellationToken);
        if (outcome == "Succeeded" && scanId.HasValue)
        {
            await using var success = new NpgsqlCommand("""
                UPDATE SharedScanSchedules SET LastSuccessfulScanAtUtc=@completed
                WHERE TenantId=@tenant AND OrganizationId=@organization AND ScheduleId=@schedule;
                """, connection, transaction);
            success.Parameters.AddWithValue("completed", now);
            success.Parameters.AddWithValue("tenant", lease.Job.TenantId);
            success.Parameters.AddWithValue("organization", lease.Job.OrganizationId);
            success.Parameters.AddWithValue("schedule", lease.Job.ScheduleId);
            await success.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RenewScanJobLeaseAsync(ScanJobLease lease, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE SharedScanJobs SET LeaseExpiresAtUtc=@lease
            WHERE TenantId=@tenant AND OrganizationId=@organization AND JobId=@job
              AND Status='Running' AND ActiveAttemptId=@attempt;
            """, connection);
        command.Parameters.AddWithValue("lease", DateTime.UtcNow.AddMinutes(2));
        command.Parameters.AddWithValue("tenant", lease.Job.TenantId);
        command.Parameters.AddWithValue("organization", lease.Job.OrganizationId);
        command.Parameters.AddWithValue("job", lease.Job.JobId);
        command.Parameters.AddWithValue("attempt", lease.AttemptId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("The scan worker lost its job lease before renewal.");
    }

    public async Task SaveScanJobCheckpointAsync(
        ScanJobDto job, string dataSource, string status, int records, string? details,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO SharedScanJobCheckpoints
                (TenantId, OrganizationId, JobId, DataSource, Status, Records, UpdatedAtUtc, Details)
            VALUES (@tenant, @organization, @job, @source, @status, @records, @updated, @details)
            ON CONFLICT (TenantId, OrganizationId, JobId, DataSource) DO UPDATE SET
                Status=excluded.Status, Records=excluded.Records, UpdatedAtUtc=excluded.UpdatedAtUtc, Details=excluded.Details;
            """, connection);
        command.Parameters.AddWithValue("tenant", job.TenantId);
        command.Parameters.AddWithValue("organization", job.OrganizationId);
        command.Parameters.AddWithValue("job", job.JobId);
        command.Parameters.AddWithValue("source", dataSource);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("records", records);
        command.Parameters.AddWithValue("updated", DateTime.UtcNow);
        command.Parameters.AddWithValue("details", (object?)errorSafe(details) ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);

        static string? errorSafe(string? value) => value is null ? null : value.Length <= 2048 ? value : value[..2048];
    }

    public async Task<bool> RequestScanJobCancellationAsync(
        Guid tenantId, Guid organizationId, Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE SharedScanJobs
            SET CancellationRequested=true,
                Status=CASE WHEN Status='Queued' THEN 'Cancelled' ELSE Status END,
                CompletedAtUtc=CASE WHEN Status='Queued' THEN @now ELSE CompletedAtUtc END,
                DurationMilliseconds=CASE WHEN Status='Queued' THEN 0 ELSE DurationMilliseconds END,
                LastError=CASE WHEN Status='Queued' THEN 'Cancelled before execution.' ELSE LastError END
            WHERE TenantId=@tenant AND OrganizationId=@organization AND JobId=@job AND Status IN ('Queued', 'Running');
            """, connection);
        command.Parameters.AddWithValue("now", DateTime.UtcNow);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("organization", organizationId);
        command.Parameters.AddWithValue("job", jobId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> IsScanJobCancellationRequestedAsync(ScanJobDto job, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT CancellationRequested OR Status='Cancelled'
            FROM SharedScanJobs WHERE TenantId=@tenant AND OrganizationId=@organization AND JobId=@job;
            """, connection);
        command.Parameters.AddWithValue("tenant", job.TenantId);
        command.Parameters.AddWithValue("organization", job.OrganizationId);
        command.Parameters.AddWithValue("job", job.JobId);
        return await command.ExecuteScalarAsync(cancellationToken) as bool? ?? true;
    }

    public async Task<IReadOnlyList<ScanJobDto>> GetScanJobsAsync(
        Guid tenantId, Guid organizationId, int limit = 50, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT JobId, ScheduleId, TenantId, OrganizationId, Provider, ExternalAccountId, DisplayName, Status,
                   AttemptCount, MaxRetries, QueuedAtUtc, StartedAtUtc, CompletedAtUtc, NextAttemptAtUtc,
                   DurationMilliseconds, ScanId, CancellationRequested, LastError
            FROM SharedScanJobs WHERE TenantId=@tenant AND OrganizationId=@organization
            ORDER BY QueuedAtUtc DESC LIMIT @limit;
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("organization", organizationId);
        command.Parameters.AddWithValue("limit", limit);
        var jobs = new List<ScanJobDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) jobs.Add(ReadJob(reader));
        return jobs;
    }

    public async Task<ScanJobDto?> GetScanJobAsync(
        Guid tenantId, Guid organizationId, Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT JobId, ScheduleId, TenantId, OrganizationId, Provider, ExternalAccountId, DisplayName, Status,
                   AttemptCount, MaxRetries, QueuedAtUtc, StartedAtUtc, CompletedAtUtc, NextAttemptAtUtc,
                   DurationMilliseconds, ScanId, CancellationRequested, LastError
            FROM SharedScanJobs WHERE TenantId=@tenant AND OrganizationId=@organization AND JobId=@job;
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("organization", organizationId);
        command.Parameters.AddWithValue("job", jobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadJob(reader) : null;
    }

    public async Task<IReadOnlyList<ScanJobAttemptDto>> GetScanJobAttemptsAsync(
        Guid tenantId, Guid organizationId, Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT AttemptId, JobId, AttemptNumber, StartedAtUtc, CompletedAtUtc, DurationMilliseconds, Outcome, Error
            FROM SharedScanJobAttempts WHERE TenantId=@tenant AND OrganizationId=@organization AND JobId=@job
            ORDER BY AttemptNumber;
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("organization", organizationId);
        command.Parameters.AddWithValue("job", jobId);
        var attempts = new List<ScanJobAttemptDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            attempts.Add(new ScanJobAttemptDto(reader.GetGuid(0), reader.GetGuid(1), reader.GetInt32(2),
                reader.GetDateTime(3), reader.IsDBNull(4) ? null : reader.GetDateTime(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        return attempts;
    }

    public async Task<IReadOnlyList<ScanJobCheckpointDto>> GetScanJobCheckpointsAsync(
        Guid tenantId, Guid organizationId, Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT JobId, DataSource, Status, Records, UpdatedAtUtc, Details
            FROM SharedScanJobCheckpoints WHERE TenantId=@tenant AND OrganizationId=@organization AND JobId=@job
            ORDER BY DataSource;
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("organization", organizationId);
        command.Parameters.AddWithValue("job", jobId);
        var checkpoints = new List<ScanJobCheckpointDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            checkpoints.Add(new ScanJobCheckpointDto(reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetDateTime(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        return checkpoints;
    }

    private static void AddScheduleParameters(NpgsqlCommand command, ScanScheduleDto schedule)
    {
        command.Parameters.AddWithValue("tenant", schedule.TenantId);
        command.Parameters.AddWithValue("organization", schedule.OrganizationId);
        command.Parameters.AddWithValue("schedule", schedule.ScheduleId);
        command.Parameters.AddWithValue("provider", schedule.Provider.ToString());
        command.Parameters.AddWithValue("account", schedule.ExternalAccountId);
        command.Parameters.AddWithValue("display", schedule.DisplayName);
        command.Parameters.AddWithValue("interval", schedule.IntervalMinutes);
        command.Parameters.AddWithValue("retries", schedule.MaxRetries);
        command.Parameters.AddWithValue("nextRun", schedule.NextRunAtUtc);
        command.Parameters.AddWithValue("created", schedule.CreatedAtUtc);
        command.Parameters.AddWithValue("updated", schedule.UpdatedAtUtc);
    }

    private static ScanScheduleDto ReadSchedule(NpgsqlDataReader reader)
        => new(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), Enum.Parse<CloudProvider>(reader.GetString(3), true),
            reader.GetString(4), reader.GetString(5), reader.GetInt32(6), reader.GetInt32(7), reader.GetBoolean(8),
            reader.GetDateTime(9), reader.GetDateTime(10), reader.GetDateTime(11), reader.IsDBNull(12) ? null : reader.GetDateTime(12));

    private static ScanJobDto ReadJob(NpgsqlDataReader reader)
        => new(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3),
            Enum.Parse<CloudProvider>(reader.GetString(4), true), reader.GetString(5), reader.GetString(6), reader.GetString(7),
            reader.GetInt32(8), reader.GetInt32(9), reader.GetDateTime(10), reader.IsDBNull(11) ? null : reader.GetDateTime(11),
            reader.IsDBNull(12) ? null : reader.GetDateTime(12), reader.IsDBNull(13) ? null : reader.GetDateTime(13),
            reader.IsDBNull(14) ? null : reader.GetInt64(14), reader.IsDBNull(15) ? null : reader.GetGuid(15),
            reader.GetBoolean(16), reader.IsDBNull(17) ? null : reader.GetString(17));
}
