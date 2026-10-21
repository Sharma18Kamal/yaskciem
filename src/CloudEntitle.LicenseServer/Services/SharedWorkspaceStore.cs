using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudEntitle.Domain;
using CloudEntitle.IdentityGovernance.Models;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.OrganizationManagement.Abstractions;
using CloudEntitle.OrganizationManagement.Models;
using Npgsql;
using NpgsqlTypes;

namespace CloudEntitle.LicenseServer.Services;

public sealed partial class SharedWorkspaceStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _ownsDataSource;

    public SharedWorkspaceStore(string connectionString)
        : this(new NpgsqlDataSourceBuilder(connectionString).Build(), ownsDataSource: true)
    {
    }

    public SharedWorkspaceStore(NpgsqlDataSource dataSource)
        : this(dataSource, ownsDataSource: false)
    {
    }

    private SharedWorkspaceStore(NpgsqlDataSource dataSource, bool ownsDataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
        _ownsDataSource = ownsDataSource;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS SharedOrganizationDirectories (
                TenantId uuid NOT NULL PRIMARY KEY,
                Revision bigint NOT NULL,
                PayloadJson text NOT NULL
            );
            CREATE TABLE IF NOT EXISTS SharedAccountBindings (
                Provider text NOT NULL,
                ExternalAccountId text NOT NULL,
                TenantId uuid NOT NULL,
                OrganizationId uuid NOT NULL,
                PRIMARY KEY (Provider, ExternalAccountId)
            );
            CREATE TABLE IF NOT EXISTS SharedAccessReviewCampaigns (
                TenantId uuid NOT NULL,
                OrganizationId uuid NOT NULL,
                CampaignId uuid NOT NULL,
                CreatedAtUtc timestamptz NOT NULL,
                PayloadJson text NOT NULL,
                PRIMARY KEY (TenantId, OrganizationId, CampaignId)
            );
            CREATE INDEX IF NOT EXISTS IX_SharedAccessReviewCampaigns_Org_Created
                ON SharedAccessReviewCampaigns (TenantId, OrganizationId, CreatedAtUtc);
            CREATE TABLE IF NOT EXISTS SharedAccessReviewAuditEvents (
                TenantId uuid NOT NULL,
                OrganizationId uuid NOT NULL,
                CampaignId uuid NOT NULL,
                EventId uuid NOT NULL,
                CorrelationId uuid NOT NULL,
                ItemId uuid NULL,
                OccurredAtUtc timestamptz NOT NULL,
                Actor text NOT NULL,
                ActorUserId uuid NULL,
                Action text NOT NULL,
                Decision text NULL,
                Rationale text NOT NULL,
                EvidenceSha256 text NULL,
                EventJson text NOT NULL,
                PRIMARY KEY (TenantId, OrganizationId, CampaignId, EventId),
                FOREIGN KEY (TenantId, OrganizationId, CampaignId)
                    REFERENCES SharedAccessReviewCampaigns (TenantId, OrganizationId, CampaignId) ON DELETE RESTRICT
            );
            ALTER TABLE SharedAccessReviewAuditEvents ADD COLUMN IF NOT EXISTS ActorUserId uuid NULL;
            CREATE INDEX IF NOT EXISTS IX_SharedAccessReviewAuditEvents_Correlation
                ON SharedAccessReviewAuditEvents (TenantId, OrganizationId, CorrelationId);
            CREATE OR REPLACE FUNCTION yask_reject_shared_review_audit_mutation() RETURNS trigger
            LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'Shared access-review audit events are append-only';
            END;
            $$;
            DROP TRIGGER IF EXISTS TR_SharedAccessReviewAuditEvents_AppendOnly ON SharedAccessReviewAuditEvents;
            CREATE TRIGGER TR_SharedAccessReviewAuditEvents_AppendOnly
            BEFORE UPDATE OR DELETE ON SharedAccessReviewAuditEvents
            FOR EACH ROW EXECUTE FUNCTION yask_reject_shared_review_audit_mutation();
            DROP TRIGGER IF EXISTS TR_SharedAccessReviewAuditEvents_NoTruncate ON SharedAccessReviewAuditEvents;
            CREATE TRIGGER TR_SharedAccessReviewAuditEvents_NoTruncate
            BEFORE TRUNCATE ON SharedAccessReviewAuditEvents
            FOR EACH STATEMENT EXECUTE FUNCTION yask_reject_shared_review_audit_mutation();
            CREATE TABLE IF NOT EXISTS SharedScanSummaries (
                TenantId uuid NOT NULL,
                OrganizationId uuid NOT NULL,
                ScanId uuid NOT NULL,
                CompletedAtUtc timestamptz NOT NULL,
                PayloadJson text NOT NULL,
                PRIMARY KEY (TenantId, OrganizationId, ScanId)
            );
            CREATE INDEX IF NOT EXISTS IX_SharedScanSummaries_Org_Completed
                ON SharedScanSummaries (TenantId, OrganizationId, CompletedAtUtc);
            CREATE TABLE IF NOT EXISTS SharedCiemScans (
                TenantId uuid NOT NULL,
                OrganizationId uuid NOT NULL,
                ScanId uuid NOT NULL,
                CompletedAtUtc timestamptz NOT NULL,
                RequestHash text NOT NULL,
                PayloadJson text NOT NULL,
                PRIMARY KEY (TenantId, OrganizationId, ScanId)
            );
            CREATE INDEX IF NOT EXISTS IX_SharedCiemScans_Org_Completed
                ON SharedCiemScans (TenantId, OrganizationId, CompletedAtUtc DESC);
            CREATE TABLE IF NOT EXISTS SharedCiemScanRecords (
                TenantId uuid NOT NULL,
                OrganizationId uuid NOT NULL,
                ScanId uuid NOT NULL,
                Provider text NOT NULL,
                RecordKind text NOT NULL,
                RecordId text NOT NULL,
                ExternalAccountId text NOT NULL,
                PayloadJson text NOT NULL,
                PRIMARY KEY (TenantId, OrganizationId, ScanId, Provider, RecordKind, ExternalAccountId, RecordId),
                FOREIGN KEY (TenantId, OrganizationId, ScanId)
                    REFERENCES SharedCiemScans (TenantId, OrganizationId, ScanId) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_SharedCiemScanRecords_Kind
                ON SharedCiemScanRecords (TenantId, OrganizationId, ScanId, RecordKind, ExternalAccountId);
            CREATE TABLE IF NOT EXISTS SharedScanSchedules (
                TenantId uuid NOT NULL, OrganizationId uuid NOT NULL, ScheduleId uuid NOT NULL,
                Provider text NOT NULL, ExternalAccountId text NOT NULL, DisplayName text NOT NULL,
                IntervalMinutes integer NOT NULL CHECK (IntervalMinutes BETWEEN 5 AND 10080),
                MaxRetries integer NOT NULL CHECK (MaxRetries BETWEEN 0 AND 4), Enabled boolean NOT NULL,
                NextRunAtUtc timestamptz NOT NULL, CreatedAtUtc timestamptz NOT NULL, UpdatedAtUtc timestamptz NOT NULL,
                LastSuccessfulScanAtUtc timestamptz NULL,
                PRIMARY KEY (TenantId, OrganizationId, ScheduleId),
                UNIQUE (TenantId, OrganizationId, Provider, ExternalAccountId)
            );
            CREATE INDEX IF NOT EXISTS IX_SharedScanSchedules_Due ON SharedScanSchedules (NextRunAtUtc) WHERE Enabled;
            CREATE TABLE IF NOT EXISTS SharedScanJobs (
                TenantId uuid NOT NULL, OrganizationId uuid NOT NULL, JobId uuid NOT NULL, ScheduleId uuid NOT NULL,
                Provider text NOT NULL, ExternalAccountId text NOT NULL, DisplayName text NOT NULL,
                Status text NOT NULL CHECK (Status IN ('Queued', 'Running', 'Succeeded', 'Partial', 'Failed', 'Cancelled')),
                AttemptCount integer NOT NULL DEFAULT 0 CHECK (AttemptCount >= 0),
                MaxRetries integer NOT NULL CHECK (MaxRetries BETWEEN 0 AND 4), QueuedAtUtc timestamptz NOT NULL,
                StartedAtUtc timestamptz NULL, CompletedAtUtc timestamptz NULL, NextAttemptAtUtc timestamptz NULL,
                LeaseExpiresAtUtc timestamptz NULL, ActiveAttemptId uuid NULL,
                DurationMilliseconds bigint NULL, ScanId uuid NULL, CancellationRequested boolean NOT NULL DEFAULT false,
                LastError text NULL, PRIMARY KEY (TenantId, OrganizationId, JobId),
                FOREIGN KEY (TenantId, OrganizationId, ScheduleId)
                    REFERENCES SharedScanSchedules (TenantId, OrganizationId, ScheduleId) ON DELETE RESTRICT
            );
            ALTER TABLE SharedScanJobs ADD COLUMN IF NOT EXISTS LeaseExpiresAtUtc timestamptz NULL;
            ALTER TABLE SharedScanJobs ADD COLUMN IF NOT EXISTS ActiveAttemptId uuid NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS UX_SharedScanJobs_ActiveAccount
                ON SharedScanJobs (TenantId, OrganizationId, Provider, ExternalAccountId) WHERE Status IN ('Queued', 'Running');
            CREATE INDEX IF NOT EXISTS IX_SharedScanJobs_Queue ON SharedScanJobs (Status, NextAttemptAtUtc, QueuedAtUtc);
            CREATE INDEX IF NOT EXISTS IX_SharedScanJobs_Org_Queued ON SharedScanJobs (TenantId, OrganizationId, QueuedAtUtc DESC);
            CREATE TABLE IF NOT EXISTS SharedScanJobAttempts (
                TenantId uuid NOT NULL, OrganizationId uuid NOT NULL, JobId uuid NOT NULL, AttemptId uuid NOT NULL,
                AttemptNumber integer NOT NULL CHECK (AttemptNumber > 0), StartedAtUtc timestamptz NOT NULL,
                CompletedAtUtc timestamptz NULL, DurationMilliseconds bigint NULL, Outcome text NOT NULL, Error text NULL,
                PRIMARY KEY (TenantId, OrganizationId, JobId, AttemptId),
                UNIQUE (TenantId, OrganizationId, JobId, AttemptNumber),
                FOREIGN KEY (TenantId, OrganizationId, JobId)
                    REFERENCES SharedScanJobs (TenantId, OrganizationId, JobId) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS SharedScanJobCheckpoints (
                TenantId uuid NOT NULL, OrganizationId uuid NOT NULL, JobId uuid NOT NULL, DataSource text NOT NULL,
                Status text NOT NULL, Records integer NOT NULL CHECK (Records >= 0), UpdatedAtUtc timestamptz NOT NULL,
                Details text NULL, PRIMARY KEY (TenantId, OrganizationId, JobId, DataSource),
                FOREIGN KEY (TenantId, OrganizationId, JobId)
                    REFERENCES SharedScanJobs (TenantId, OrganizationId, JobId) ON DELETE CASCADE
            );
            """, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
        using var monitoringSchema = typeof(SharedWorkspaceStore).Assembly.GetManifestResourceStream("CloudEntitle.LicenseServer.MonitoringSchema.sql")
            ?? throw new InvalidOperationException("The central monitoring schema resource is missing.");
        using var schemaReader = new StreamReader(monitoringSchema);
        await using var monitoringCommand = new NpgsqlCommand(await schemaReader.ReadToEndAsync(cancellationToken), connection);
        await monitoringCommand.ExecuteNonQueryAsync(cancellationToken);
        using var governanceSchema = typeof(SharedWorkspaceStore).Assembly.GetManifestResourceStream("CloudEntitle.LicenseServer.GovernanceSchema.sql")
            ?? throw new InvalidOperationException("The governance schema resource is missing.");
        using var governanceReader = new StreamReader(governanceSchema);
        await using var governanceCommand = new NpgsqlCommand(await governanceReader.ReadToEndAsync(cancellationToken), connection);
        await governanceCommand.ExecuteNonQueryAsync(cancellationToken);
        using var operationsSchema = typeof(SharedWorkspaceStore).Assembly.GetManifestResourceStream("CloudEntitle.LicenseServer.OperationsSchema.sql")
            ?? throw new InvalidOperationException("The operations schema resource is missing.");
        using var operationsReader = new StreamReader(operationsSchema);
        await using var operationsCommand = new NpgsqlCommand(await operationsReader.ReadToEndAsync(cancellationToken), connection);
        await operationsCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("""
                     SELECT to_regclass('sharedorganizationdirectories') IS NOT NULL
                         AND to_regclass('sharedaccountbindings') IS NOT NULL
                         AND to_regclass('sharedaccessreviewcampaigns') IS NOT NULL
                         AND EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid=to_regclass('sharedaccessreviewcampaigns')
                             AND attname='nextgovernancecheckatutc' AND NOT attisdropped)
                         AND to_regclass('sharedaccessreviewauditevents') IS NOT NULL
                         AND EXISTS (
                             SELECT 1 FROM pg_attribute
                             WHERE attrelid=to_regclass('sharedaccessreviewauditevents')
                               AND attname='actoruserid' AND NOT attisdropped
                         )
                         AND EXISTS (
                             SELECT 1 FROM pg_trigger
                             WHERE tgrelid=to_regclass('sharedaccessreviewauditevents')
                               AND tgname='tr_sharedaccessreviewauditevents_appendonly'
                               AND NOT tgisinternal AND tgenabled='O'
                         )
                         AND EXISTS (
                             SELECT 1 FROM pg_trigger
                             WHERE tgrelid=to_regclass('sharedaccessreviewauditevents')
                               AND tgname='tr_sharedaccessreviewauditevents_notruncate'
                               AND NOT tgisinternal AND tgenabled='O'
                         )
                         AND to_regclass('sharedscansummaries') IS NOT NULL
                         AND to_regclass('sharedciemscans') IS NOT NULL
                         AND to_regclass('sharedciemscanrecords') IS NOT NULL
                         AND to_regclass('sharedscanschedules') IS NOT NULL
                         AND to_regclass('sharedscanjobs') IS NOT NULL
                         AND to_regclass('sharedscanjobattempts') IS NOT NULL
                         AND to_regclass('sharedscanjobcheckpoints') IS NOT NULL
                         AND to_regclass('sharedmonitoringbaselines') IS NOT NULL
                         AND to_regclass('sharednotificationrules') IS NOT NULL
                         AND to_regclass('shareddriftevents') IS NOT NULL
                         AND to_regclass('sharednotificationdeliveries') IS NOT NULL
                         AND to_regclass('sharednotificationattempts') IS NOT NULL
                         AND EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid=to_regclass('sharednotificationdeliveries')
                             AND attname='connectorkind' AND NOT attisdropped)
                         AND EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid=to_regclass('sharednotificationdeliveries')
                             AND attname='requestedby' AND NOT attisdropped)
                """, connection);
            return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    public IOrganizationStore CreateOrganizationStore(Guid tenantId)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("An Entra tenant ID is required.", nameof(tenantId));
        return new TenantOrganizationStore(_dataSource, tenantId);
    }

    public async Task<bool> IsAccountBoundAsync(CloudProvider provider, string externalAccountId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM SharedAccountBindings WHERE Provider=@provider AND ExternalAccountId=@external)", connection);
        command.Parameters.AddWithValue("provider", provider.ToString().ToUpperInvariant());
        command.Parameters.AddWithValue("external", externalAccountId.Trim().ToUpperInvariant());
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    public async Task<IReadOnlyList<AccessReviewCampaign>> GetAccessReviewsAsync(Guid tenantId, Guid organizationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT PayloadJson FROM SharedAccessReviewCampaigns WHERE TenantId=@tenant AND OrganizationId=@organization ORDER BY CreatedAtUtc DESC", connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("organization", organizationId);
        var campaigns = new List<AccessReviewCampaign>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var campaign = JsonSerializer.Deserialize<AccessReviewCampaign>(reader.GetString(0), JsonOptions);
            if (campaign is not null) campaigns.Add(campaign);
        }
        return campaigns;
    }

    public async Task<AccessReviewCampaign> UpdateAccessReviewAsync(
        Guid tenantId,
        Guid organizationId,
        Guid campaignId,
        Func<AccessReviewCampaign?, AccessReviewCampaign> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@lockKey, 0));", connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("lockKey", $"{tenantId:D}:{organizationId:D}:{campaignId:D}");
            await lockCommand.ExecuteScalarAsync(cancellationToken);
        }
        AccessReviewCampaign? existing;
        await using (var read = new NpgsqlCommand("""
            SELECT PayloadJson FROM SharedAccessReviewCampaigns
            WHERE TenantId=@tenant AND OrganizationId=@organization AND CampaignId=@campaign
            FOR UPDATE;
            """, connection, transaction))
        {
            read.Parameters.AddWithValue("tenant", tenantId);
            read.Parameters.AddWithValue("organization", organizationId);
            read.Parameters.AddWithValue("campaign", campaignId);
            var payload = await read.ExecuteScalarAsync(cancellationToken) as string;
            existing = payload is null ? null : JsonSerializer.Deserialize<AccessReviewCampaign>(payload, JsonOptions)
                ?? throw new InvalidOperationException("The stored shared access review is invalid.");
        }

        var campaign = update(existing);
        if (campaign.Id != campaignId || campaign.Items.Count == 0)
            throw new InvalidOperationException("The shared access review update returned invalid campaign data.");
        await PersistAccessReviewAsync(connection, transaction, tenantId, organizationId, campaign, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return campaign;
    }

    public async Task SaveAccessReviewAsync(Guid tenantId, Guid organizationId, AccessReviewCampaign campaign, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@lockKey, 0));", connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("lockKey", $"{tenantId:D}:{organizationId:D}:{campaign.Id:D}");
            await lockCommand.ExecuteScalarAsync(cancellationToken);
        }
        await PersistAccessReviewAsync(connection, transaction, tenantId, organizationId, campaign, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task PersistAccessReviewAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid tenantId,
        Guid organizationId,
        AccessReviewCampaign campaign,
        CancellationToken cancellationToken)
    {
        AccessReviewCampaign? previous;
        await using (var read = new NpgsqlCommand("""
            SELECT PayloadJson FROM SharedAccessReviewCampaigns
            WHERE TenantId=@tenant AND OrganizationId=@organization AND CampaignId=@campaign;
            """, connection, transaction))
        {
            read.Parameters.AddWithValue("tenant", tenantId);
            read.Parameters.AddWithValue("organization", organizationId);
            read.Parameters.AddWithValue("campaign", campaign.Id);
            var payload = await read.ExecuteScalarAsync(cancellationToken) as string;
            previous = payload is null ? null : JsonSerializer.Deserialize<AccessReviewCampaign>(payload, JsonOptions)
                ?? throw new InvalidOperationException("The stored governance campaign is invalid.");
        }
        await using var command = new NpgsqlCommand("""
            INSERT INTO SharedAccessReviewCampaigns (TenantId, OrganizationId, CampaignId, CreatedAtUtc, PayloadJson, NextGovernanceCheckAtUtc)
            VALUES (@tenant, @organization, @campaign, @created, @payload, @nextCheck)
            ON CONFLICT (TenantId, OrganizationId, CampaignId) DO UPDATE SET
                CreatedAtUtc=excluded.CreatedAtUtc, PayloadJson=excluded.PayloadJson, NextGovernanceCheckAtUtc=excluded.NextGovernanceCheckAtUtc;
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("organization", organizationId);
        command.Parameters.AddWithValue("campaign", campaign.Id);
        command.Parameters.AddWithValue("created", campaign.CreatedAt.ToUniversalTime());
        command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(campaign, JsonOptions));
        command.Parameters.Add("nextCheck", NpgsqlDbType.TimestampTz).Value =
            campaign.Status is not (CampaignStatus.Cancelled or CampaignStatus.Draft)
                && campaign.Items.Any(item => item.Decision is ReviewDecision.Pending or ReviewDecision.NeedsInvestigation or ReviewDecision.ExceptionGranted)
                    ? DateTime.UtcNow.AddMinutes(1) : DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);

        await RecordReviewOverdueEventsAsync(connection, transaction, tenantId, organizationId, previous, campaign, cancellationToken);
        foreach (var entry in campaign.AuditTrail.Concat(campaign.Items.SelectMany(item => item.AuditTrail))
                     .Where(entry => entry.EventId != Guid.Empty && entry.CorrelationId != Guid.Empty))
        {
            await using var audit = new NpgsqlCommand("""
                INSERT INTO SharedAccessReviewAuditEvents
                    (TenantId, OrganizationId, CampaignId, EventId, CorrelationId, ItemId, OccurredAtUtc,
                     Actor, ActorUserId, Action, Decision, Rationale, EvidenceSha256, EventJson)
                VALUES
                    (@tenant, @organization, @campaign, @event, @correlation, @item, @occurred,
                     @actor, @actorUser, @action, @decision, @rationale, @evidenceHash, @eventJson)
                ON CONFLICT (TenantId, OrganizationId, CampaignId, EventId) DO NOTHING;
                """, connection, transaction);
            audit.Parameters.AddWithValue("tenant", tenantId);
            audit.Parameters.AddWithValue("organization", organizationId);
            audit.Parameters.AddWithValue("campaign", campaign.Id);
            audit.Parameters.AddWithValue("event", entry.EventId);
            audit.Parameters.AddWithValue("correlation", entry.CorrelationId);
            audit.Parameters.AddWithValue("item", entry.ItemId is Guid itemId ? itemId : DBNull.Value);
            audit.Parameters.AddWithValue("occurred", entry.Timestamp.ToUniversalTime());
            audit.Parameters.AddWithValue("actor", entry.Actor);
            audit.Parameters.AddWithValue("actorUser", entry.ActorUserId == Guid.Empty ? DBNull.Value : entry.ActorUserId);
            audit.Parameters.AddWithValue("action", entry.Action);
            audit.Parameters.AddWithValue("decision", entry.Decision is ReviewDecision decision ? decision.ToString() : DBNull.Value);
            audit.Parameters.AddWithValue("rationale", entry.Details);
            audit.Parameters.AddWithValue("evidenceHash", entry.EvidenceSha256 is null ? DBNull.Value : entry.EvidenceSha256);
            audit.Parameters.AddWithValue("eventJson", JsonSerializer.Serialize(entry, JsonOptions));
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task SaveScanSummaryAsync(SharedScanSummaryDto summary, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO SharedScanSummaries (TenantId, OrganizationId, ScanId, CompletedAtUtc, PayloadJson)
            VALUES (@tenant, @organization, @scan, @completed, @payload)
            ON CONFLICT (TenantId, OrganizationId, ScanId) DO UPDATE SET
                CompletedAtUtc=excluded.CompletedAtUtc, PayloadJson=excluded.PayloadJson;
            """, connection);
        command.Parameters.AddWithValue("tenant", summary.TenantId);
        command.Parameters.AddWithValue("organization", summary.OrganizationId);
        command.Parameters.AddWithValue("scan", summary.ScanId);
        command.Parameters.AddWithValue("completed", summary.CompletedAtUtc.ToUniversalTime());
        command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(summary, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<(SharedScanSummaryDto Summary, bool Created)> SaveCiemScanAsync(
        SharedScanSummaryDto summary,
        IReadOnlyList<SharedCiemRecordDto> records,
        string requestHash,
        CancellationToken cancellationToken = default,
        ScanJobLease? lease = null)
    {
        ValidateMonitoringScopes(summary, records);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await LockMonitoringOrganizationAsync(connection, transaction, summary.TenantId, summary.OrganizationId, cancellationToken);
        if (lease is not null)
        {
            await using var ownership = new NpgsqlCommand("""
                SELECT 1 FROM SharedScanJobs
                WHERE TenantId=@tenant AND OrganizationId=@organization AND JobId=@job
                  AND Status='Running' AND NOT CancellationRequested
                  AND ActiveAttemptId=@attempt AND LeaseExpiresAtUtc > @now
                FOR UPDATE;
                """, connection, transaction);
            ownership.Parameters.AddWithValue("tenant", lease.Job.TenantId);
            ownership.Parameters.AddWithValue("organization", lease.Job.OrganizationId);
            ownership.Parameters.AddWithValue("job", lease.Job.JobId);
            ownership.Parameters.AddWithValue("attempt", lease.AttemptId);
            ownership.Parameters.AddWithValue("now", DateTime.UtcNow);
            if (await ownership.ExecuteScalarAsync(cancellationToken) is null)
                throw new InvalidOperationException("The scan worker no longer owns this job lease.");
        }
        foreach (var account in summary.Accounts)
        {
            await using var binding = new NpgsqlCommand("""
                SELECT 1 FROM SharedAccountBindings
                WHERE Provider=@provider AND ExternalAccountId=@external
                  AND TenantId=@tenant AND OrganizationId=@organization
                FOR KEY SHARE;
                """, connection, transaction);
            binding.Parameters.AddWithValue("provider", account.Provider.ToString().ToUpperInvariant());
            binding.Parameters.AddWithValue("external", account.ExternalAccountId.Trim().ToUpperInvariant());
            binding.Parameters.AddWithValue("tenant", summary.TenantId);
            binding.Parameters.AddWithValue("organization", summary.OrganizationId);
            if (await binding.ExecuteScalarAsync(cancellationToken) is null)
                throw new UnauthorizedAccessException("The scan account is no longer bound to this organization.");
        }
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO SharedCiemScans (TenantId, OrganizationId, ScanId, CompletedAtUtc, RequestHash, PayloadJson)
            VALUES (@tenant, @organization, @scan, @completed, @requestHash, @payload)
            ON CONFLICT (TenantId, OrganizationId, ScanId) DO NOTHING;
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("tenant", summary.TenantId);
            insert.Parameters.AddWithValue("organization", summary.OrganizationId);
            insert.Parameters.AddWithValue("scan", summary.ScanId);
            insert.Parameters.AddWithValue("completed", summary.CompletedAtUtc.ToUniversalTime());
            insert.Parameters.AddWithValue("requestHash", requestHash);
            insert.Parameters.AddWithValue("payload", JsonSerializer.Serialize(summary, JsonOptions));
            var inserted = await insert.ExecuteNonQueryAsync(cancellationToken);
            await using var read = new NpgsqlCommand("""
                SELECT RequestHash, PayloadJson
                FROM SharedCiemScans
                WHERE TenantId=@tenant AND OrganizationId=@organization AND ScanId=@scan
                FOR UPDATE;
                """, connection, transaction);
            read.Parameters.AddWithValue("tenant", summary.TenantId);
            read.Parameters.AddWithValue("organization", summary.OrganizationId);
            read.Parameters.AddWithValue("scan", summary.ScanId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("The shared scan could not be read after saving.");
            var existingHash = reader.GetString(0);
            var existingSummary = JsonSerializer.Deserialize<SharedScanSummaryDto>(reader.GetString(1), JsonOptions)
                ?? throw new InvalidOperationException("The stored shared scan summary is invalid.");
            await reader.CloseAsync();
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(existingHash),
                    Encoding.ASCII.GetBytes(requestHash)))
                throw new InvalidOperationException("A scan with this ID already exists with different content.");

            if (inserted == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return (existingSummary, false);
            }

            await using (var insertSummary = new NpgsqlCommand("""
                INSERT INTO SharedScanSummaries (TenantId, OrganizationId, ScanId, CompletedAtUtc, PayloadJson)
                VALUES (@tenant, @organization, @scan, @completed, @payload)
                ON CONFLICT (TenantId, OrganizationId, ScanId) DO UPDATE SET
                    CompletedAtUtc=excluded.CompletedAtUtc, PayloadJson=excluded.PayloadJson;
                """, connection, transaction))
            {
                insertSummary.Parameters.AddWithValue("tenant", summary.TenantId);
                insertSummary.Parameters.AddWithValue("organization", summary.OrganizationId);
                insertSummary.Parameters.AddWithValue("scan", summary.ScanId);
                insertSummary.Parameters.AddWithValue("completed", summary.CompletedAtUtc.ToUniversalTime());
                insertSummary.Parameters.AddWithValue("payload", JsonSerializer.Serialize(summary, JsonOptions));
                await insertSummary.ExecuteNonQueryAsync(cancellationToken);
            }

            if (records.Count > 0)
            {
                var recordsJson = JsonSerializer.Serialize(records.Select(record => new
                {
                    provider = record.Provider.ToString(),
                    record_kind = record.Kind,
                    record_id = record.RecordId,
                    external_account_id = record.ExternalAccountId,
                    payload_json = record.PayloadJson
                }));
                await using var insertRecords = new NpgsqlCommand("""
                    INSERT INTO SharedCiemScanRecords
                        (TenantId, OrganizationId, ScanId, Provider, RecordKind, RecordId, ExternalAccountId, PayloadJson)
                    SELECT @tenant, @organization, @scan, record.provider, record.record_kind,
                        record.record_id, record.external_account_id, record.payload_json
                    FROM jsonb_to_recordset(@records::jsonb) AS record(
                        provider text, record_kind text, record_id text, external_account_id text, payload_json text);
                    """, connection, transaction);
                insertRecords.Parameters.AddWithValue("tenant", summary.TenantId);
                insertRecords.Parameters.AddWithValue("organization", summary.OrganizationId);
                insertRecords.Parameters.AddWithValue("scan", summary.ScanId);
                insertRecords.Parameters.AddWithValue("records", recordsJson);
                await insertRecords.ExecuteNonQueryAsync(cancellationToken);
            }
            await RecordCentralDriftAsync(connection, transaction, summary, records, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (summary, true);
        }
    }

    public async Task<IReadOnlyList<SharedCiemRecordDto>> GetCiemScanRecordsAsync(
        Guid tenantId,
        Guid organizationId,
        Guid scanId,
        string? kind = null,
        string? externalAccountId = null,
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 5000) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT Provider, RecordKind, RecordId, ExternalAccountId, PayloadJson
            FROM SharedCiemScanRecords
            WHERE TenantId=@tenant AND OrganizationId=@organization AND ScanId=@scan
                AND (@kind IS NULL OR RecordKind=@kind)
                AND (@account IS NULL OR ExternalAccountId=@account)
            ORDER BY Provider, RecordKind, ExternalAccountId, RecordId
            LIMIT @limit;
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("organization", organizationId);
        command.Parameters.AddWithValue("scan", scanId);
        command.Parameters.Add("kind", NpgsqlDbType.Text).Value = (object?)kind ?? DBNull.Value;
        command.Parameters.Add("account", NpgsqlDbType.Text).Value = (object?)externalAccountId ?? DBNull.Value;
        command.Parameters.AddWithValue("limit", limit);
        var records = new List<SharedCiemRecordDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            records.Add(new SharedCiemRecordDto(Enum.Parse<CloudProvider>(reader.GetString(0), true), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        return records;
    }

    public async Task<IReadOnlyList<SharedCiemRecordDto>> GetCiemFindingsAsync(
        Guid tenantId,
        Guid organizationId,
        Guid? scanId = null,
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 5000) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH selected_scan AS (
                SELECT ScanId
                FROM SharedCiemScans
                WHERE TenantId=@tenant AND OrganizationId=@organization
                    AND (@scan IS NULL OR ScanId=@scan)
                ORDER BY CompletedAtUtc DESC
                LIMIT 1
            )
            SELECT Provider, RecordKind, RecordId, ExternalAccountId, PayloadJson
            FROM SharedCiemScanRecords
            WHERE TenantId=@tenant AND OrganizationId=@organization
                AND ScanId IN (SELECT ScanId FROM selected_scan)
                AND RecordKind IN ('finding', 'resource-finding')
            ORDER BY Provider, RecordKind, ExternalAccountId, RecordId
            LIMIT @limit;
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("organization", organizationId);
        command.Parameters.Add("scan", NpgsqlDbType.Uuid).Value = (object?)scanId ?? DBNull.Value;
        command.Parameters.AddWithValue("limit", limit);
        var records = new List<SharedCiemRecordDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            records.Add(new SharedCiemRecordDto(Enum.Parse<CloudProvider>(reader.GetString(0), true), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        return records;
    }

    public async Task<IReadOnlyList<SharedScanSummaryDto>> GetScanSummariesAsync(Guid tenantId, Guid organizationId, int limit = 30, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT PayloadJson FROM SharedScanSummaries WHERE TenantId=@tenant AND OrganizationId=@organization ORDER BY CompletedAtUtc DESC LIMIT @limit", connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("organization", organizationId);
        command.Parameters.AddWithValue("limit", limit);
        var summaries = new List<SharedScanSummaryDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var summary = JsonSerializer.Deserialize<SharedScanSummaryDto>(reader.GetString(0), JsonOptions);
            if (summary is not null) summaries.Add(summary);
        }
        return summaries;
    }

    private Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
        => _dataSource.OpenConnectionAsync(cancellationToken).AsTask();

    private sealed class TenantOrganizationStore(NpgsqlDataSource dataSource, Guid tenantId) : IOrganizationStore
    {
        private static readonly JsonSerializerOptions StoreJsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
        private long _revision;
        private bool _loaded;

        public OrganizationDirectoryState Load()
        {
            using var connection = dataSource.OpenConnection();
            using var command = new NpgsqlCommand("SELECT Revision, PayloadJson FROM SharedOrganizationDirectories WHERE TenantId=@tenant", connection);
            command.Parameters.AddWithValue("tenant", tenantId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                _revision = 0;
                _loaded = true;
                return EmptyState();
            }
            _revision = reader.GetInt64(0);
            _loaded = true;
            return JsonSerializer.Deserialize<OrganizationDirectoryState>(reader.GetString(1), StoreJsonOptions) ?? EmptyState();
        }

        public void Save(OrganizationDirectoryState state)
        {
            ArgumentNullException.ThrowIfNull(state);
            if (!_loaded) throw new InvalidOperationException("Load the tenant directory before saving it.");
            using var connection = dataSource.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
            using (var command = new NpgsqlCommand(_revision == 0
                ? "INSERT INTO SharedOrganizationDirectories (TenantId, Revision, PayloadJson) VALUES (@tenant, 1, @payload) ON CONFLICT (TenantId) DO NOTHING;"
                : "UPDATE SharedOrganizationDirectories SET Revision=Revision+1, PayloadJson=@payload WHERE TenantId=@tenant AND Revision=@revision;", connection, transaction))
            {
                command.Parameters.AddWithValue("tenant", tenantId);
                command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(state, StoreJsonOptions));
                if (_revision != 0) command.Parameters.AddWithValue("revision", _revision);
                if (command.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("The shared organization directory changed concurrently. Reload it and retry the operation.");
            }

            using (var clearBindings = new NpgsqlCommand("DELETE FROM SharedAccountBindings WHERE TenantId=@tenant", connection, transaction))
            {
                clearBindings.Parameters.AddWithValue("tenant", tenantId);
                clearBindings.ExecuteNonQuery();
            }

            foreach (var binding in state.Bindings)
            {
                using var addBinding = new NpgsqlCommand("INSERT INTO SharedAccountBindings (Provider, ExternalAccountId, TenantId, OrganizationId) VALUES (@provider, @external, @tenant, @organization)", connection, transaction);
                addBinding.Parameters.AddWithValue("provider", binding.Provider.ToString().ToUpperInvariant());
                addBinding.Parameters.AddWithValue("external", binding.ExternalTenantId.Trim().ToUpperInvariant());
                addBinding.Parameters.AddWithValue("tenant", tenantId);
                addBinding.Parameters.AddWithValue("organization", binding.OrganizationId);
                try { addBinding.ExecuteNonQuery(); }
                catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
                {
                    throw new InvalidOperationException("This provider account is already bound to an organization.", exception);
                }
            }

            transaction.Commit();
            _revision++;
        }

        private static OrganizationDirectoryState EmptyState()
            => new(Array.Empty<OrganizationProfile>(), Array.Empty<OrganizationMember>(), Array.Empty<TenantBinding>());
    }

    public void Dispose()
    {
        if (_ownsDataSource) _dataSource.Dispose();
    }
}
