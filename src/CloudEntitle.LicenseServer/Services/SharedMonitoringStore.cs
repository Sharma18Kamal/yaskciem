using System.Text.Json;
using CloudEntitle.Integration.Contracts;
using Npgsql;

namespace CloudEntitle.LicenseServer.Services;

public sealed partial class SharedWorkspaceStore
{
    public static void ValidateMonitoringScopes(SharedScanSummaryDto scan, IReadOnlyList<SharedCiemRecordDto> records)
    {
        if (scan.MonitoringScopes is null || scan.MonitoringScopes.Count > 600)
            throw new ArgumentException("Monitoring scopes must be a bounded list.");
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scope in scan.MonitoringScopes)
        {
            if (scope is null || !scan.Accounts.Any(account => account.Provider == scope.Provider
                    && account.ExternalAccountId.Equals(scope.ExternalAccountId, StringComparison.OrdinalIgnoreCase))
                || !(scope.Kind == "effective-access" && scope.Profile == "effective-access-v1"
                    || scope.Kind == "finding" && scope.Profile == "desktop-risk-v1"
                    || scope.Kind == "resource-finding" && scope.Profile == "desktop-resource-risk-v1")
                || !keys.Add($"{scope.Provider}|{scope.ExternalAccountId}|{scope.Kind}"))
                throw new ArgumentException("Monitoring scopes must identify unique scanned accounts and supported comparison profiles.");
            var source = scope.Kind == "effective-access" ? "Effective access" : "Risk findings";
            var coverage = scan.Coverage.Where(item => item.Provider.Equals(scope.Provider.ToString(), StringComparison.OrdinalIgnoreCase)
                && item.DataSource.EndsWith(source, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (coverage.Length == 0 || coverage.Any(item => item.Status is not ("Collected" or "NoData")))
                throw new ArgumentException("Monitoring scopes require complete source coverage; partial or unavailable sources cannot establish a baseline.");
            _ = SharedMonitoringProjection.Project(records.Where(record => record.Provider == scope.Provider
                && record.ExternalAccountId.Equals(scope.ExternalAccountId, StringComparison.OrdinalIgnoreCase)), scope.Kind);
        }
    }

    private static async Task LockMonitoringOrganizationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid tenantId, Guid organizationId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@scope, 0));", connection, transaction);
        command.Parameters.AddWithValue("scope", $"monitoring:{tenantId:D}:{organizationId:D}");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RecordCentralDriftAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        SharedScanSummaryDto scan, IReadOnlyList<SharedCiemRecordDto> records, CancellationToken cancellationToken)
    {
        var rules = await ReadRulesAsync(connection, transaction, scan.TenantId, scan.OrganizationId, cancellationToken);
        foreach (var scope in scan.MonitoringScopes)
        {
            var after = SharedMonitoringProjection.Project(records.Where(record => record.Provider == scope.Provider
                && record.ExternalAccountId.Equals(scope.ExternalAccountId, StringComparison.OrdinalIgnoreCase)), scope.Kind);
            Guid? previousScanId = null;
            IReadOnlyList<SharedMonitoringEntity> before = Array.Empty<SharedMonitoringEntity>();
            await using (var read = new NpgsqlCommand("""
                SELECT ScanId, CompletedAtUtc, EntitiesJson FROM SharedMonitoringBaselines
                WHERE TenantId=@tenant AND OrganizationId=@organization AND Provider=@provider
                  AND ExternalAccountId=@account AND Kind=@kind AND Profile=@profile FOR UPDATE;
                """, connection, transaction))
            {
                AddScope(read, scan, scope);
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    if (reader.GetDateTime(1) >= scan.CompletedAtUtc) continue;
                    previousScanId = reader.GetGuid(0);
                    before = JsonSerializer.Deserialize<SharedMonitoringEntity[]>(reader.GetString(2), JsonOptions)
                        ?? throw new InvalidOperationException("The monitoring baseline is invalid.");
                }
            }
            if (previousScanId.HasValue)
            {
                var old = before.ToDictionary(entity => entity.Key, StringComparer.Ordinal);
                var current = after.ToDictionary(entity => entity.Key, StringComparer.Ordinal);
                foreach (var key in old.Keys.Union(current.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
                {
                    old.TryGetValue(key, out var previous);
                    current.TryGetValue(key, out var next);
                    if (previous?.StateHash == next?.StateHash) continue;
                    var change = previous is null ? "Added" : next is null ? "Removed" : "Changed";
                    var material = JsonSerializer.Serialize(new object[] { scan.TenantId, scan.OrganizationId, previousScanId.Value,
                        scan.ScanId, scope.Provider, scope.ExternalAccountId.ToUpperInvariant(), scope.Kind, key, change });
                    var eventId = new Guid(Convert.FromHexString(SharedMonitoringProjection.Hash(material)).AsSpan(0, 16));
                    var drift = new SharedDriftEventDto(eventId, scan.ScanId, previousScanId.Value, scope.Provider,
                        scope.ExternalAccountId, scope.Kind, change, key,
                        (SharedAlertSeverity)Math.Max((int)(next?.Severity ?? SharedAlertSeverity.Informational),
                            (int)(previous?.Severity ?? SharedAlertSeverity.Informational)),
                        previous?.StateHash, next?.StateHash, DateTime.UtcNow);
                    await using var insert = new NpgsqlCommand("""
                        INSERT INTO SharedDriftEvents (TenantId, OrganizationId, EventId, DetectedAtUtc, PayloadJson)
                        VALUES (@tenant, @organization, @event, @detected, @payload) ON CONFLICT DO NOTHING;
                        """, connection, transaction);
                    insert.Parameters.AddWithValue("tenant", scan.TenantId);
                    insert.Parameters.AddWithValue("organization", scan.OrganizationId);
                    insert.Parameters.AddWithValue("event", drift.EventId);
                    insert.Parameters.AddWithValue("detected", drift.DetectedAtUtc);
                    insert.Parameters.AddWithValue("payload", JsonSerializer.Serialize(drift, JsonOptions));
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                    foreach (var rule in rules.Where(rule => Matches(rule, drift)).OrderBy(rule => rule.RuleId))
                    {
                        var deliveryId = new Guid(Convert.FromHexString(SharedMonitoringProjection.Hash($"{eventId:D}|{rule.Channel}")).AsSpan(0, 16));
                        await using var delivery = new NpgsqlCommand("""
                            INSERT INTO SharedNotificationDeliveries
                                (TenantId, OrganizationId, DeliveryId, EventId, RuleId, Channel, Status, CreatedAtUtc, NextAttemptAtUtc)
                            VALUES (@tenant, @organization, @delivery, @event, @rule, @channel, 'Pending', @now, @now)
                            ON CONFLICT (TenantId, OrganizationId, EventId, Channel) DO NOTHING;
                            """, connection, transaction);
                        delivery.Parameters.AddWithValue("tenant", scan.TenantId);
                        delivery.Parameters.AddWithValue("organization", scan.OrganizationId);
                        delivery.Parameters.AddWithValue("delivery", deliveryId);
                        delivery.Parameters.AddWithValue("event", eventId);
                        delivery.Parameters.AddWithValue("rule", rule.RuleId);
                        delivery.Parameters.AddWithValue("channel", rule.Channel);
                        delivery.Parameters.AddWithValue("now", drift.DetectedAtUtc);
                        await delivery.ExecuteNonQueryAsync(cancellationToken);
                    }
                }
            }
            await using var baseline = new NpgsqlCommand("""
                INSERT INTO SharedMonitoringBaselines
                    (TenantId, OrganizationId, Provider, ExternalAccountId, Kind, Profile, ScanId, CompletedAtUtc, EntitiesJson)
                VALUES (@tenant, @organization, @provider, @account, @kind, @profile, @scan, @completed, @entities)
                ON CONFLICT (TenantId, OrganizationId, Provider, ExternalAccountId, Kind, Profile)
                DO UPDATE SET ScanId=excluded.ScanId, CompletedAtUtc=excluded.CompletedAtUtc, EntitiesJson=excluded.EntitiesJson;
                """, connection, transaction);
            AddScope(baseline, scan, scope);
            baseline.Parameters.AddWithValue("scan", scan.ScanId);
            baseline.Parameters.AddWithValue("completed", scan.CompletedAtUtc);
            baseline.Parameters.AddWithValue("entities", JsonSerializer.Serialize(after, JsonOptions));
            await baseline.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static bool Matches(SharedNotificationRuleDto rule, SharedDriftEventDto drift)
        => rule.Enabled && drift.Severity >= rule.MinimumSeverity
            && (drift.Kind == "effective-access" ? rule.EffectiveAccess : rule.Findings)
            && (drift.Change == "Added" ? rule.Added : drift.Change == "Removed" ? rule.Removed : rule.Changed);

    private static void AddScope(NpgsqlCommand command, SharedScanSummaryDto scan, SharedMonitoringScopeDto scope)
    {
        command.Parameters.AddWithValue("tenant", scan.TenantId);
        command.Parameters.AddWithValue("organization", scan.OrganizationId);
        command.Parameters.AddWithValue("provider", scope.Provider.ToString());
        command.Parameters.AddWithValue("account", scope.ExternalAccountId.ToUpperInvariant());
        command.Parameters.AddWithValue("kind", scope.Kind);
        command.Parameters.AddWithValue("profile", scope.Profile);
    }

    public static void ValidateNotificationRule(SharedNotificationRuleDto rule)
    {
        if (rule is null || rule.RuleId == Guid.Empty || string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Length > 128
            || !Enum.IsDefined(rule.MinimumSeverity) || !(rule.Findings || rule.EffectiveAccess)
            || !(rule.Added || rule.Removed || rule.Changed) || string.IsNullOrEmpty(rule.Channel) || rule.Channel.Length > 64
            || rule.Channel.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw new ArgumentException("Rules require an ID, name, severity, source/change selection, and a channel alias (letters, digits, - or _).");
    }

    public async Task SaveNotificationRuleAsync(Guid tenantId, Guid organizationId, SharedNotificationRuleDto rule,
        CancellationToken cancellationToken = default)
    {
        ValidateNotificationRule(rule);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockMonitoringOrganizationAsync(connection, transaction, tenantId, organizationId, cancellationToken);
        await using (var count = new NpgsqlCommand("""
            SELECT count(*) FROM SharedNotificationRules
            WHERE TenantId=@tenant AND OrganizationId=@organization AND RuleId<>@rule;
            """, connection, transaction))
        {
            count.Parameters.AddWithValue("tenant", tenantId);
            count.Parameters.AddWithValue("organization", organizationId);
            count.Parameters.AddWithValue("rule", rule.RuleId);
            if ((long)(await count.ExecuteScalarAsync(cancellationToken))! >= 50)
                throw new ArgumentException("An organization can have at most 50 notification rules.");
        }
        await using var command = new NpgsqlCommand("""
            INSERT INTO SharedNotificationRules (TenantId, OrganizationId, RuleId, PayloadJson)
            VALUES (@tenant, @organization, @rule, @payload)
            ON CONFLICT (TenantId, OrganizationId, RuleId) DO UPDATE SET PayloadJson=excluded.PayloadJson;
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("organization", organizationId);
        command.Parameters.AddWithValue("rule", rule.RuleId);
        command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(rule, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SharedNotificationRuleDto>> GetNotificationRulesAsync(Guid tenantId, Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await ReadRulesAsync(connection, null, tenantId, organizationId, cancellationToken);
    }

    private static async Task<IReadOnlyList<SharedNotificationRuleDto>> ReadRulesAsync(NpgsqlConnection connection,
        NpgsqlTransaction? transaction, Guid tenantId, Guid organizationId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT PayloadJson FROM SharedNotificationRules WHERE TenantId=@tenant AND OrganizationId=@organization ORDER BY RuleId;
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("organization", organizationId);
        var result = new List<SharedNotificationRuleDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(JsonSerializer.Deserialize<SharedNotificationRuleDto>(reader.GetString(0), JsonOptions)
                ?? throw new InvalidOperationException("The notification rule is invalid."));
        return result;
    }

    public async Task<IReadOnlyList<SharedDriftEventDto>> GetDriftEventsAsync(Guid tenantId, Guid organizationId,
        int limit = 100, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT PayloadJson FROM SharedDriftEvents WHERE TenantId=@tenant AND OrganizationId=@organization
            ORDER BY DetectedAtUtc DESC, EventId LIMIT @limit;
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("organization", organizationId);
        command.Parameters.AddWithValue("limit", limit);
        var events = new List<SharedDriftEventDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            events.Add(JsonSerializer.Deserialize<SharedDriftEventDto>(reader.GetString(0), JsonOptions)
                ?? throw new InvalidOperationException("The drift event is invalid."));
        return events;
    }
}
