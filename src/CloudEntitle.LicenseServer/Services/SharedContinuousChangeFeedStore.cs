using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.OrganizationManagement.Models;
using Npgsql;
using NpgsqlTypes;

namespace CloudEntitle.LicenseServer.Services;

public sealed record ContinuousChangeLease(Guid TenantId, Guid OrganizationId, Guid AttemptId,
    ContinuousChangeFeedStatus State, string ConfigurationSha256);

public sealed partial class SharedWorkspaceStore
{
    private const string FeedColumns = """
        Alias,Provider,AccountId,SourceId,SourceEpoch,Enabled,Connected,Cursor,Health,ConsecutiveFailures,
        LastSuccessAtUtc,NextPollAtUtc,FailureCode,CoverageJson,SourceWatermarkUtc,ConfigurationSha256
        """;

    public async Task<ContinuousChangeFeedStatus> ConfigureContinuousFeedAsync(Guid tenant, Guid organization,
        ContinuousChangeSource source, ConfigureContinuousChangeFeedRequest request, CancellationToken token = default)
    {
        if (request.Connected && !request.Enabled) throw new ArgumentException("Connected requires explicit enablement.");
        await using var connection = await OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await LockFeedAsync(connection, transaction, tenant, organization, source.Alias, token);
        await RequireActiveFeedBindingAsync(connection, transaction, tenant, organization, source.Provider, source.AccountId, token);
        var old = await ReadFeedAsync(connection, transaction, tenant, organization, source.Alias, token);
        if (old is not null && (old.Value.State.Provider != source.Provider || old.Value.State.AccountId != source.AccountId))
            throw new InvalidOperationException("Changing a source account requires a distinct alias.");
        var replacement = old is not null && old.Value.Fingerprint != source.ConfigurationSha256;
        if (replacement && !request.ResetSource && request.Enabled)
            throw new InvalidOperationException("Source changed; an explicit source reset and reconnect is required.");
        if (request.ResetSource && (!request.Enabled || !request.Connected))
            throw new ArgumentException("A new source epoch requires explicit enable and connect.");
        var reset = old is null || request.ResetSource;
        var epoch = reset ? Guid.NewGuid() : old!.Value.State.SourceEpoch;
        var coverage = reset ? ContinuousChangeFeedValidation.UnknownCoverage() : old!.Value.State.Coverage;
        await using (var supersede = FeedCommand("""
            UPDATE SharedContinuousChangeAttempts SET CompletedAtUtc=now(),Outcome='Superseded',FailureCode='operator_reconfigured'
            WHERE TenantId=@tenant AND OrganizationId=@organization AND Alias=@alias AND CompletedAtUtc IS NULL;
            """, connection, transaction, tenant, organization, source.Alias))
            await supersede.ExecuteNonQueryAsync(token);
        await using var command = FeedCommand("""
            INSERT INTO SharedContinuousChangeFeeds
                (TenantId,OrganizationId,Alias,Provider,AccountId,SourceId,ConfigurationSha256,SourceEpoch,
                 Enabled,Connected,Cursor,Health,CoverageJson,NextPollAtUtc)
            VALUES (@tenant,@organization,@alias,@provider,@account,@source,@fingerprint,@epoch,
                    @enabled,@connected,NULL,@health,@coverage,@next)
            ON CONFLICT (TenantId,OrganizationId,Alias) DO UPDATE SET
                Provider=CASE WHEN @reset THEN EXCLUDED.Provider ELSE SharedContinuousChangeFeeds.Provider END,
                AccountId=CASE WHEN @reset THEN EXCLUDED.AccountId ELSE SharedContinuousChangeFeeds.AccountId END,
                SourceId=CASE WHEN @reset THEN EXCLUDED.SourceId ELSE SharedContinuousChangeFeeds.SourceId END,
                ConfigurationSha256=CASE WHEN @reset THEN EXCLUDED.ConfigurationSha256 ELSE SharedContinuousChangeFeeds.ConfigurationSha256 END,
                SourceEpoch=@epoch,Enabled=@enabled,Connected=@connected,Health=@health,NextPollAtUtc=@next,
                Cursor=CASE WHEN @reset THEN NULL ELSE SharedContinuousChangeFeeds.Cursor END,
                CoverageJson=@coverage,ConsecutiveFailures=0,FailureCode=NULL,
                LastSuccessAtUtc=CASE WHEN @reset THEN NULL ELSE SharedContinuousChangeFeeds.LastSuccessAtUtc END,
                SourceWatermarkUtc=CASE WHEN @reset THEN NULL ELSE SharedContinuousChangeFeeds.SourceWatermarkUtc END,
                ActiveAttemptId=NULL,LeaseExpiresAtUtc=NULL;
            """, connection, transaction, tenant, organization, source.Alias);
        command.Parameters.AddWithValue("provider", source.Provider.ToString());
        command.Parameters.AddWithValue("account", source.AccountId);
        command.Parameters.AddWithValue("source", source.SourceId);
        command.Parameters.AddWithValue("fingerprint", source.ConfigurationSha256);
        command.Parameters.AddWithValue("epoch", epoch);
        command.Parameters.AddWithValue("enabled", request.Enabled);
        command.Parameters.AddWithValue("connected", request.Connected);
        command.Parameters.AddWithValue("reset", reset);
        command.Parameters.AddWithValue("health", request.Connected ? "Pending" : "Paused");
        command.Parameters.AddWithValue("coverage", JsonSerializer.Serialize(coverage, JsonOptions));
        AddNullable(command, "next", NpgsqlDbType.TimestampTz, request.Connected ? DateTimeOffset.UtcNow : null);
        await command.ExecuteNonQueryAsync(token);
        var state = (await ReadFeedAsync(connection, transaction, tenant, organization, source.Alias, token))!.Value.State;
        await transaction.CommitAsync(token);
        return state;
    }

    public async Task PauseContinuousFeedAsync(Guid tenant, Guid organization, string alias, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await LockFeedAsync(connection, transaction, tenant, organization, alias, token);
        await using var command = FeedCommand("""
            UPDATE SharedContinuousChangeFeeds SET Enabled=false,Connected=false,Health='Paused',
                NextPollAtUtc=NULL,ActiveAttemptId=NULL,LeaseExpiresAtUtc=NULL
            WHERE TenantId=@tenant AND OrganizationId=@organization AND Alias=@alias;
            """, connection, transaction, tenant, organization, alias);
        if (await command.ExecuteNonQueryAsync(token) != 1) throw new KeyNotFoundException();
        await using var supersede = FeedCommand("""
            UPDATE SharedContinuousChangeAttempts SET CompletedAtUtc=now(),Outcome='Superseded',FailureCode='operator_paused'
            WHERE TenantId=@tenant AND OrganizationId=@organization AND Alias=@alias AND CompletedAtUtc IS NULL;
            """, connection, transaction, tenant, organization, alias);
        await supersede.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
    }

    public async Task<IReadOnlyList<(ContinuousChangeFeedStatus State, string Fingerprint)>> GetContinuousFeedStatesAsync(
        Guid tenant, Guid organization, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand($"""
            SELECT {FeedColumns} FROM SharedContinuousChangeFeeds
            WHERE TenantId=@tenant AND OrganizationId=@organization ORDER BY Alias LIMIT 100;
            """, connection);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("organization", organization);
        var result = new List<(ContinuousChangeFeedStatus, string)>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) result.Add(ReadFeedRow(reader));
        return result;
    }

    public async Task<ContinuousChangeLease?> ClaimContinuousFeedAsync(CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        Guid tenant, organization;
        (ContinuousChangeFeedStatus State, string Fingerprint) feed;
        await using (var command = new NpgsqlCommand($"""
            SELECT TenantId,OrganizationId,{FeedColumns} FROM SharedContinuousChangeFeeds
            WHERE Enabled AND Connected AND NextPollAtUtc <= now()
                AND (ActiveAttemptId IS NULL OR LeaseExpiresAtUtc <= now())
            ORDER BY NextPollAtUtc FOR UPDATE SKIP LOCKED LIMIT 1;
            """, connection, transaction))
        {
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return null;
            tenant = reader.GetGuid(0);
            organization = reader.GetGuid(1);
            feed = ReadFeedRow(reader, 2);
        }
        // Claim only takes the row lock; imports cannot complete until the claim commits.
        try { await RequireActiveFeedBindingAsync(connection, transaction, tenant, organization, feed.State.Provider, feed.State.AccountId, token); }
        catch (UnauthorizedAccessException)
        {
            await using var blocked = FeedCommand("""
                UPDATE SharedContinuousChangeFeeds SET Health='Blocked',FailureCode='scope_not_active',
                    NextPollAtUtc=now()+interval '5 minutes',ActiveAttemptId=NULL,LeaseExpiresAtUtc=NULL
                WHERE TenantId=@tenant AND OrganizationId=@organization AND Alias=@alias;
                """, connection, transaction, tenant, organization, feed.State.Alias);
            await blocked.ExecuteNonQueryAsync(token);
            await transaction.CommitAsync(token);
            return null;
        }
        var attempt = Guid.NewGuid();
        await using var claim = FeedCommand("""
            UPDATE SharedContinuousChangeAttempts SET CompletedAtUtc=now(),Outcome='Expired',FailureCode='lease_expired'
            WHERE TenantId=@tenant AND OrganizationId=@organization AND Alias=@alias AND CompletedAtUtc IS NULL;
            INSERT INTO SharedContinuousChangeAttempts (TenantId,OrganizationId,Alias,SourceEpoch,AttemptId,StartedAtUtc,Outcome)
            VALUES (@tenant,@organization,@alias,@epoch,@attempt,now(),'Polling');
            UPDATE SharedContinuousChangeFeeds SET ActiveAttemptId=@attempt,LeaseExpiresAtUtc=now()+interval '90 seconds',
                Health='Polling' WHERE TenantId=@tenant AND OrganizationId=@organization AND Alias=@alias;
            """, connection, transaction, tenant, organization, feed.State.Alias);
        claim.Parameters.AddWithValue("attempt", attempt);
        claim.Parameters.AddWithValue("epoch", feed.State.SourceEpoch);
        await claim.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
        return new(tenant, organization, attempt, feed.State, feed.Fingerprint);
    }

    public async Task<ContinuousChangeReceipt> CommitContinuousBatchAsync(ContinuousChangeLease lease,
        ContinuousChangeBatch batch, int pollSeconds, CancellationToken token = default)
    {
        ContinuousChangeFeedValidation.Validate(batch, lease.State);
        if (pollSeconds is < 15 or > 3600) throw new ArgumentException("Invalid polling bounds.");
        var json = JsonSerializer.Serialize(batch, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > ContinuousChangeFeedValidation.MaximumBatchBytes)
            throw new ArgumentException("Normalized change batch exceeds 2 MiB.");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        await using var connection = await OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await LockFeedAsync(connection, transaction, lease.TenantId, lease.OrganizationId, lease.State.Alias, token);
        await RequireActiveFeedBindingAsync(connection, transaction, lease.TenantId, lease.OrganizationId,
            lease.State.Provider, lease.State.AccountId, token);
        NpgsqlCommand Command(string sql) => FeedCommand(sql, connection, transaction,
            lease.TenantId, lease.OrganizationId, lease.State.Alias);
        // Reject fenced/expired workers even for replay; uncertain commits are recovered by a new valid lease.
        await using (var guard = Command("""
            SELECT 1 FROM SharedContinuousChangeFeeds WHERE TenantId=@tenant AND OrganizationId=@organization AND Alias=@alias
                AND SourceEpoch=@epoch AND ConfigurationSha256=@fingerprint AND Enabled AND Connected
                AND ActiveAttemptId=@attempt AND LeaseExpiresAtUtc > now() FOR UPDATE;
            """))
        {
            guard.Parameters.AddWithValue("epoch", lease.State.SourceEpoch);
            guard.Parameters.AddWithValue("fingerprint", lease.ConfigurationSha256);
            guard.Parameters.AddWithValue("attempt", lease.AttemptId);
            if (await guard.ExecuteScalarAsync(token) is null) throw new InvalidOperationException("Feed worker lost its lease.");
        }
        var current = (await ReadFeedAsync(connection, transaction, lease.TenantId, lease.OrganizationId, lease.State.Alias, token))!.Value.State;
        await using (var replay = Command("""
            SELECT PayloadSha256,ReceiptJson FROM SharedContinuousChangeBatches
            WHERE TenantId=@tenant AND OrganizationId=@organization AND Alias=@alias AND SourceEpoch=@epoch AND BatchId=@batch;
            """))
        {
            replay.Parameters.AddWithValue("epoch", batch.SourceEpoch);
            replay.Parameters.AddWithValue("batch", batch.BatchId);
            await using var reader = await replay.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
            {
                if (reader.GetString(0) != hash) throw new InvalidOperationException("Conflicting batch replay.");
                var receipt = JsonSerializer.Deserialize<ContinuousChangeReceipt>(reader.GetString(1), JsonOptions)!;
                await reader.DisposeAsync();
                await ReleaseSuccessfulFeedAsync(connection, transaction, lease, pollSeconds, token);
                await transaction.CommitAsync(token);
                return receipt;
            }
        }
        if (current.Cursor != batch.PreviousCursor || current.SourceWatermarkUtc > batch.SourceWatermarkUtc)
            throw new InvalidOperationException("Cursor or source watermark conflict; no evidence was advanced.");
        var accepted = 0;
        var received = DateTimeOffset.UtcNow;
        var rules = await ReadRulesAsync(connection, transaction, lease.TenantId, lease.OrganizationId, token);
        foreach (var change in batch.Changes)
        {
            var payload = JsonSerializer.Serialize(change, JsonOptions);
            await using var revision = Command("""
                INSERT INTO SharedContinuousChangeRevisions
                    (TenantId,OrganizationId,Alias,SourceEpoch,SourceId,SourceEventId,RevisionId,ReceivedAtUtc,SourceEventAtUtc,PayloadJson)
                VALUES (@tenant,@organization,@alias,@epoch,@source,@event,@revision,@received,@occurred,@payload)
                ON CONFLICT DO NOTHING RETURNING 1;
                """);
            revision.Parameters.AddWithValue("epoch", batch.SourceEpoch);
            revision.Parameters.AddWithValue("source", batch.SourceId);
            revision.Parameters.AddWithValue("event", change.SourceEventId);
            revision.Parameters.AddWithValue("revision", change.RevisionId);
            revision.Parameters.AddWithValue("received", received);
            revision.Parameters.AddWithValue("occurred", change.SourceEventAtUtc.ToUniversalTime());
            revision.Parameters.AddWithValue("payload", payload);
            if (await revision.ExecuteScalarAsync(token) is null)
            {
                await using var existing = Command("""
                    SELECT PayloadJson FROM SharedContinuousChangeRevisions WHERE TenantId=@tenant AND OrganizationId=@organization
                        AND Alias=@alias AND SourceEpoch=@epoch AND SourceEventId=@event AND RevisionId=@revision;
                    """);
                existing.Parameters.AddWithValue("epoch", batch.SourceEpoch);
                existing.Parameters.AddWithValue("event", change.SourceEventId);
                existing.Parameters.AddWithValue("revision", change.RevisionId);
                if (await existing.ExecuteScalarAsync(token) as string != payload)
                    throw new InvalidOperationException("Conflicting source revision; cursor was not advanced.");
                continue;
            }
            accepted++;
            var id = OperationalId($"continuous-change|{lease.TenantId}|{lease.OrganizationId}|{lease.State.Alias}|{batch.SourceEpoch}|{change.SourceEventId}|{change.RevisionId}");
            await RecordOperationalEventAsync(connection, transaction, lease.TenantId, lease.OrganizationId,
                new(id, batch.BatchId, Guid.Empty, batch.Provider, batch.AccountId, "continuous-cloud-change", change.Operation,
                    $"{change.Category}|{id:D}", SharedAlertSeverity.Medium, change.BeforeSha256, change.AfterSha256,
                    change.SourceEventAtUtc.UtcDateTime)
                { EventType = "cloud-change.observed", SubjectId = batch.BatchId }, token, rules);
        }
        var result = new ContinuousChangeReceipt(batch.BatchId, batch.SourceEpoch, batch.Cursor, accepted, received);
        // A source claim cannot silently heal a previously recorded gap. Retain the gap until a new explicitly reset epoch.
        var coverage = batch.Coverage.Select(c => current.Coverage.FirstOrDefault(old =>
            old.Category == c.Category && old.State == "Gap") is { } gap && c.State != "Gap" ? gap : c).ToArray();
        foreach (var gap in batch.Coverage.Where(c => c.State == "Gap"))
        {
            var id = OperationalId($"continuous-gap|{lease.TenantId}|{lease.OrganizationId}|{lease.State.Alias}|{batch.SourceEpoch}|{gap.Category}|{gap.GapCode}");
            await RecordOperationalEventAsync(connection, transaction, lease.TenantId, lease.OrganizationId,
                new(id, batch.BatchId, Guid.Empty, batch.Provider, batch.AccountId, "continuous-cloud-change", "Changed",
                    $"{gap.Category}|{gap.GapCode}", SharedAlertSeverity.High, null, null, received.UtcDateTime)
                { EventType = "cloud-change.coverage-gap", SubjectId = batch.BatchId }, token, rules);
        }
        await using (var commit = Command("""
            INSERT INTO SharedContinuousChangeBatches
                (TenantId,OrganizationId,Alias,SourceEpoch,BatchId,PayloadSha256,ReceiptJson,CoverageJson,PayloadJson)
            VALUES (@tenant,@organization,@alias,@epoch,@batch,@hash,@receipt,@batchcoverage,@payload);
            UPDATE SharedContinuousChangeFeeds SET Cursor=@cursor,CoverageJson=@coverage,SourceWatermarkUtc=@watermark,
                LastSuccessAtUtc=@received,ConsecutiveFailures=0,FailureCode=NULL,Health=@health,
                ActiveAttemptId=NULL,LeaseExpiresAtUtc=NULL,NextPollAtUtc=now()+make_interval(secs=>@poll)
            WHERE TenantId=@tenant AND OrganizationId=@organization AND Alias=@alias;
            UPDATE SharedContinuousChangeAttempts SET CompletedAtUtc=now(),Outcome='Committed'
            WHERE TenantId=@tenant AND OrganizationId=@organization AND Alias=@alias
                AND SourceEpoch=@epoch AND AttemptId=@attempt AND CompletedAtUtc IS NULL;
            """))
        {
            commit.Parameters.AddWithValue("epoch", batch.SourceEpoch);
            commit.Parameters.AddWithValue("batch", batch.BatchId);
            commit.Parameters.AddWithValue("hash", hash);
            commit.Parameters.AddWithValue("receipt", JsonSerializer.Serialize(result, JsonOptions));
            commit.Parameters.AddWithValue("payload", json);
            commit.Parameters.AddWithValue("batchcoverage", JsonSerializer.Serialize(batch.Coverage, JsonOptions));
            commit.Parameters.AddWithValue("coverage", JsonSerializer.Serialize(coverage, JsonOptions));
            commit.Parameters.AddWithValue("watermark", batch.SourceWatermarkUtc.ToUniversalTime());
            commit.Parameters.AddWithValue("cursor", batch.Cursor);
            commit.Parameters.AddWithValue("received", received);
            commit.Parameters.AddWithValue("health", coverage.Any(c => c.State == "Gap") ? "Gap" : "Partial");
            commit.Parameters.AddWithValue("poll", pollSeconds);
            commit.Parameters.AddWithValue("attempt", lease.AttemptId);
            await commit.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
        return result;
    }

    public async Task<bool> IsContinuousFeedLeaseValidAsync(ContinuousChangeLease lease, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        try
        {
            await RequireActiveFeedBindingAsync(connection, transaction, lease.TenantId, lease.OrganizationId,
                lease.State.Provider, lease.State.AccountId, token);
        }
        catch (UnauthorizedAccessException) { return false; }
        await using var command = FeedCommand("""
            SELECT 1 FROM SharedContinuousChangeFeeds WHERE TenantId=@tenant AND OrganizationId=@organization AND Alias=@alias
                AND Enabled AND Connected AND SourceEpoch=@epoch AND ConfigurationSha256=@fingerprint
                AND ActiveAttemptId=@attempt AND LeaseExpiresAtUtc>now();
            """, connection, transaction, lease.TenantId, lease.OrganizationId, lease.State.Alias);
        command.Parameters.AddWithValue("epoch", lease.State.SourceEpoch);
        command.Parameters.AddWithValue("fingerprint", lease.ConfigurationSha256);
        command.Parameters.AddWithValue("attempt", lease.AttemptId);
        return await command.ExecuteScalarAsync(token) is not null;
    }

    public async Task FailContinuousFeedAsync(ContinuousChangeLease lease, string failureCode, CancellationToken token = default)
    {
        if (!ContinuousChangeFeedValidation.SafeId(failureCode, 64)) throw new ArgumentException("Invalid safe failure code.");
        await using var connection = await OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await LockFeedAsync(connection, transaction, lease.TenantId, lease.OrganizationId, lease.State.Alias, token);
        await using var command = FeedCommand("""
            UPDATE SharedContinuousChangeFeeds SET Health='Error',FailureCode=@failure,
                ConsecutiveFailures=LEAST(ConsecutiveFailures+1,16),
                NextPollAtUtc=now()+make_interval(secs=>LEAST(3600,30*power(2,LEAST(ConsecutiveFailures,7)))::int),
                ActiveAttemptId=NULL,LeaseExpiresAtUtc=NULL
            WHERE TenantId=@tenant AND OrganizationId=@organization AND Alias=@alias AND SourceEpoch=@epoch
                AND ActiveAttemptId=@attempt AND LeaseExpiresAtUtc>now();
            """, connection, transaction, lease.TenantId, lease.OrganizationId, lease.State.Alias);
        command.Parameters.AddWithValue("failure", failureCode);
        command.Parameters.AddWithValue("epoch", lease.State.SourceEpoch);
        command.Parameters.AddWithValue("attempt", lease.AttemptId);
        if (await command.ExecuteNonQueryAsync(token) == 0) return;
        await using var attempt = FeedCommand("""
            UPDATE SharedContinuousChangeAttempts SET CompletedAtUtc=now(),Outcome='Failed',FailureCode=@failure
            WHERE TenantId=@tenant AND OrganizationId=@organization AND Alias=@alias AND SourceEpoch=@epoch
                AND AttemptId=@attempt AND CompletedAtUtc IS NULL;
            """, connection, transaction, lease.TenantId, lease.OrganizationId, lease.State.Alias);
        attempt.Parameters.AddWithValue("failure", failureCode);
        attempt.Parameters.AddWithValue("epoch", lease.State.SourceEpoch);
        attempt.Parameters.AddWithValue("attempt", lease.AttemptId);
        await attempt.ExecuteNonQueryAsync(token);
        var id = OperationalId($"continuous-failed|{lease.TenantId}|{lease.OrganizationId}|{lease.AttemptId}");
        await RecordOperationalEventAsync(connection, transaction, lease.TenantId, lease.OrganizationId,
            new(id, Guid.Empty, Guid.Empty, lease.State.Provider, lease.State.AccountId, "continuous-cloud-change", "Changed",
                failureCode, SharedAlertSeverity.High, null, null, DateTime.UtcNow)
            { EventType = "cloud-change.poll-failed", SubjectId = lease.AttemptId }, token);
        await transaction.CommitAsync(token);
    }

    public async Task<IReadOnlyList<StoredContinuousChange>> GetContinuousChangesAsync(Guid tenant, Guid organization,
        string alias, int limit = 100, CancellationToken token = default)
    {
        if (limit is < 1 or > 500 || !ContinuousChangeFeedValidation.SafeId(alias, 64)) throw new ArgumentException("Invalid change list bounds.");
        await using var connection = await OpenAsync(token);
        await using var command = FeedCommand("""
            SELECT SourceId,SourceEpoch,ReceivedAtUtc,PayloadJson FROM SharedContinuousChangeRevisions
            WHERE TenantId=@tenant AND OrganizationId=@organization AND Alias=@alias
            ORDER BY ReceivedAtUtc DESC,SourceEventId,RevisionId LIMIT @limit;
            """, connection, null, tenant, organization, alias);
        command.Parameters.AddWithValue("limit", limit);
        var result = new List<StoredContinuousChange>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) result.Add(new(alias, reader.GetString(0), reader.GetGuid(1),
            reader.GetFieldValue<DateTimeOffset>(2), JsonSerializer.Deserialize<NormalizedCloudChange>(reader.GetString(3), JsonOptions)!));
        return result;
    }

    private static async Task ReleaseSuccessfulFeedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        ContinuousChangeLease lease, int poll, CancellationToken token)
    {
        await using var command = FeedCommand("""
            UPDATE SharedContinuousChangeFeeds SET ActiveAttemptId=NULL,LeaseExpiresAtUtc=NULL,
                Health=CASE WHEN CoverageJson::jsonb @> '[{"state":"Gap"}]'::jsonb THEN 'Gap' ELSE 'Partial' END,
                ConsecutiveFailures=0,FailureCode=NULL,NextPollAtUtc=now()+make_interval(secs=>@poll)
            WHERE TenantId=@tenant AND OrganizationId=@organization AND Alias=@alias;
            UPDATE SharedContinuousChangeAttempts SET CompletedAtUtc=now(),Outcome='Replayed'
            WHERE TenantId=@tenant AND OrganizationId=@organization AND Alias=@alias AND SourceEpoch=@epoch
                AND AttemptId=@attempt AND CompletedAtUtc IS NULL;
            """, connection, transaction, lease.TenantId, lease.OrganizationId, lease.State.Alias);
        command.Parameters.AddWithValue("poll", poll);
        command.Parameters.AddWithValue("epoch", lease.State.SourceEpoch);
        command.Parameters.AddWithValue("attempt", lease.AttemptId);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task RequireActiveFeedBindingAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid tenant, Guid organization, CloudProvider provider, string account, CancellationToken token)
    {
        await using var directory = new NpgsqlCommand(
            "SELECT PayloadJson FROM SharedOrganizationDirectories WHERE TenantId=@tenant FOR SHARE;", connection, transaction);
        directory.Parameters.AddWithValue("tenant", tenant);
        var payload = await directory.ExecuteScalarAsync(token) as string;
        var state = payload is null ? null : JsonSerializer.Deserialize<OrganizationDirectoryState>(payload, JsonOptions);
        if (state?.Organizations.Any(o => o.Id == organization && o.Status == OrganizationStatus.Active) != true)
            throw new UnauthorizedAccessException("Organization is not active.");
        // TenantOrganizationStore.Save updates the directory row before replacing bindings.
        // Its update conflicts with our directory share lock, so a second binding-row lock
        // is unnecessary and would require UPDATE privileges on the insert/delete-only table.
        await using var bound = new NpgsqlCommand("""
            SELECT 1 FROM SharedAccountBindings WHERE TenantId=@tenant AND OrganizationId=@organization
                AND Provider=@provider AND ExternalAccountId=@account;
            """, connection, transaction);
        bound.Parameters.AddWithValue("tenant", tenant);
        bound.Parameters.AddWithValue("organization", organization);
        bound.Parameters.AddWithValue("provider", provider.ToString().ToUpperInvariant());
        bound.Parameters.AddWithValue("account", account.Trim().ToUpperInvariant());
        if (await bound.ExecuteScalarAsync(token) is null) throw new UnauthorizedAccessException("Source account is not bound.");
    }

    private static async Task LockFeedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid tenant, Guid organization, string alias, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@scope,0));", connection, transaction);
        command.Parameters.AddWithValue("scope", $"continuous-feed:{tenant:D}:{organization:D}:{alias}");
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<(ContinuousChangeFeedStatus State, string Fingerprint)?> ReadFeedAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid tenant, Guid organization, string alias, CancellationToken token)
    {
        await using var command = FeedCommand($"SELECT {FeedColumns} FROM SharedContinuousChangeFeeds WHERE TenantId=@tenant AND OrganizationId=@organization AND Alias=@alias;",
            connection, transaction, tenant, organization, alias);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadFeedRow(reader) : null;
    }

    private static (ContinuousChangeFeedStatus State, string Fingerprint) ReadFeedRow(NpgsqlDataReader reader, int start = 0)
    {
        string? Text(int index) => reader.IsDBNull(start + index) ? null : reader.GetString(start + index);
        DateTimeOffset? Time(int index) => reader.IsDBNull(start + index) ? null : reader.GetFieldValue<DateTimeOffset>(start + index);
        return (new(reader.GetString(start), Enum.Parse<CloudProvider>(reader.GetString(start + 1)), reader.GetString(start + 2),
            reader.GetString(start + 3), reader.GetGuid(start + 4), reader.GetBoolean(start + 5), reader.GetBoolean(start + 6),
            Text(7), reader.GetString(start + 8), reader.GetInt32(start + 9), Time(10), Time(11), Text(12),
            JsonSerializer.Deserialize<ChangeFeedCategoryCoverage[]>(reader.GetString(start + 13), JsonOptions)!, Time(14)), reader.GetString(start + 15));
    }

    private static NpgsqlCommand FeedCommand(string sql, NpgsqlConnection connection, NpgsqlTransaction? transaction,
        Guid tenant, Guid organization, string alias)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("organization", organization);
        command.Parameters.AddWithValue("alias", alias);
        return command;
    }

    private static void AddNullable(NpgsqlCommand command, string name, NpgsqlDbType type, object? value)
        => command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });
}
