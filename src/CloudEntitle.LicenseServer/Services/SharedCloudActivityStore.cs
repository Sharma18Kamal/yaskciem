using System.Text.Json;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace CloudEntitle.LicenseServer.Services;

public sealed partial class SharedWorkspaceStore
{
    public async Task SaveCloudActivityAsync(Guid tenant, Guid organization, Guid user, CloudActivitySnapshot snapshot,
        CancellationToken token = default)
    {
        ValidateCloudActivity(snapshot);
        var payload = JsonSerializer.Serialize(snapshot, JsonOptions);
        if (System.Text.Encoding.UTF8.GetByteCount(payload) > 10 * 1024 * 1024)
            throw new ArgumentException("Activity evidence exceeds the 10 MB snapshot limit.");
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            INSERT INTO SharedCloudActivitySnapshots (TenantId,OrganizationId,SnapshotId,Provider,AccountId,ThroughUtc,UploadedBy,PayloadJson)
            VALUES (@tenant,@organization,@snapshot,@provider,@account,@through,@user,@payload)
            ON CONFLICT (TenantId,OrganizationId,SnapshotId) DO NOTHING;
            SELECT PayloadJson FROM SharedCloudActivitySnapshots
            WHERE TenantId=@tenant AND OrganizationId=@organization AND SnapshotId=@snapshot;
            """, connection);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("organization", organization);
        command.Parameters.AddWithValue("snapshot", snapshot.SnapshotId);
        command.Parameters.AddWithValue("provider", (int)snapshot.Provider);
        command.Parameters.AddWithValue("account", snapshot.AccountId);
        command.Parameters.AddWithValue("through", snapshot.ThroughUtc.ToUniversalTime());
        command.Parameters.AddWithValue("user", user);
        command.Parameters.AddWithValue("payload", payload);
        var saved = await command.ExecuteScalarAsync(token) as string;
        if (saved != payload) throw new InvalidOperationException("Activity snapshot ID already has different evidence.");
    }

    public async Task<CloudActivitySnapshot?> GetCloudActivityAsync(Guid tenant, Guid organization,
        CloudProvider provider, string account, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT PayloadJson FROM SharedCloudActivitySnapshots
            WHERE TenantId=@tenant AND OrganizationId=@organization AND Provider=@provider AND AccountId=@account
            ORDER BY ThroughUtc DESC, SnapshotId DESC LIMIT 1;
            """, connection);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("organization", organization);
        command.Parameters.AddWithValue("provider", (int)provider);
        command.Parameters.AddWithValue("account", account);
        var payload = await command.ExecuteScalarAsync(token) as string;
        return payload is null ? null : JsonSerializer.Deserialize<CloudActivitySnapshot>(payload, JsonOptions)
            ?? throw new InvalidOperationException("Stored activity evidence is invalid.");
    }

    public static void ValidateCloudActivity(CloudActivitySnapshot snapshot)
    {
        if (snapshot is null || snapshot.SnapshotId == Guid.Empty || snapshot.Provider is not (CloudProvider.Azure or CloudProvider.Aws or CloudProvider.Gcp)
            || string.IsNullOrWhiteSpace(snapshot.AccountId) || snapshot.AccountId.Length > 128
            || snapshot.ThroughUtc > DateTimeOffset.UtcNow.AddMinutes(1)
            || snapshot.FromUtc != snapshot.ThroughUtc.AddDays(-90)
            || snapshot.Events is null || snapshot.Events.Count > 20_000
            || snapshot.Provenance is null || snapshot.Provenance.Count > 128
            || snapshot.SourceScope is not null && !SafeActivityText(snapshot.SourceScope, 4096)
            || !SafeActivityText(snapshot.Source, 128) || !SafeActivityText(snapshot.CoverageDetails, 2048))
            throw new ArgumentException("Invalid scoped 90-day activity snapshot.");
        foreach (var item in snapshot.Events)
        {
            if (item is null || !SafeActivityText(item.EventId, 2048) || !SafeActivityText(item.Principal, 2048)
                || !SafeActivityText(item.Action, 2048)
                || item.PermissionAction is not null && !SafeActivityText(item.PermissionAction, 2048)
                || item.Resource is not null && !SafeActivityText(item.Resource, 4096)
                || item.PermissionEvidenceSource is not null && !SafeActivityText(item.PermissionEvidenceSource, 128)
                || item.SourceIp is not null && !SafeActivityText(item.SourceIp, 128)
                || item.CountryCode is not null && (item.CountryCode.Length != 2 || !item.CountryCode.All(char.IsAsciiLetter))
                || item.Success && !item.OutcomeKnown
                || item.Timestamp < snapshot.FromUtc || item.Timestamp > snapshot.ThroughUtc)
                throw new ArgumentException("Invalid activity event or timestamp.");
        }
        foreach (var source in snapshot.Provenance)
            if (source is null || !SafeActivityText(source.SourceId, 4096) || source.Sha256 is not { Length: 64 }
                || source.Sha256.Any(c => !char.IsAsciiHexDigit(c)) || source.SourceRecords < 0)
                throw new ArgumentException("Invalid activity provenance digest.");
    }

    public async Task<CloudActivitySnapshot?> GetCloudActivitySnapshotAsync(Guid tenant, Guid organization,
        CloudProvider provider, string account, Guid snapshotId, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand("""
                SELECT PayloadJson FROM SharedCloudActivitySnapshots
                WHERE TenantId=@tenant AND OrganizationId=@organization AND Provider=@provider AND AccountId=@account AND SnapshotId=@snapshot;
                """, connection);
        AddActivityScope(command, tenant, organization, provider, account);
        command.Parameters.AddWithValue("snapshot", snapshotId);
        var payload = await command.ExecuteScalarAsync(token) as string;
        return payload is null ? null : JsonSerializer.Deserialize<CloudActivitySnapshot>(payload, JsonOptions)
            ?? throw new InvalidOperationException("Stored activity evidence is invalid.");
    }

    public async Task<CloudActivityHistoryPage> GetCloudActivityHistoryAsync(Guid tenant, Guid organization,
        CloudProvider provider, string account, int limit = 20, Guid? beforeSnapshotId = null, CancellationToken token = default)
    {
        if (limit is < 1 or > 50 || beforeSnapshotId == Guid.Empty)
            throw new ArgumentException("Activity history limit must be 1 through 50 and the cursor must identify a saved snapshot.");
        await using var connection = await OpenAsync(token);
        if (beforeSnapshotId is not null)
        {
            await using var cursor = new NpgsqlCommand("""
                    SELECT 1 FROM SharedCloudActivitySnapshots
                    WHERE TenantId=@tenant AND OrganizationId=@organization AND Provider=@provider AND AccountId=@account AND SnapshotId=@before;
                    """, connection);
            AddActivityScope(cursor, tenant, organization, provider, account);
            cursor.Parameters.AddWithValue("before", beforeSnapshotId.Value);
            if (await cursor.ExecuteScalarAsync(token) is null)
                throw new ArgumentException("Unknown activity history cursor for the selected account.");
        }
        await using var command = new NpgsqlCommand("""
                SELECT SnapshotId,ThroughUtc,UploadedBy,PayloadJson::jsonb->>'source',PayloadJson::jsonb->>'sourceScope',
                       (PayloadJson::jsonb->>'truncated')::boolean,jsonb_array_length(PayloadJson::jsonb->'events')
                FROM SharedCloudActivitySnapshots
                WHERE TenantId=@tenant AND OrganizationId=@organization AND Provider=@provider AND AccountId=@account
                  AND (@before IS NULL OR (ThroughUtc,SnapshotId) < (
                    SELECT ThroughUtc,SnapshotId FROM SharedCloudActivitySnapshots
                    WHERE TenantId=@tenant AND OrganizationId=@organization AND Provider=@provider AND AccountId=@account AND SnapshotId=@before))
                ORDER BY ThroughUtc DESC, SnapshotId DESC LIMIT @limit;
                """, connection);
        AddActivityScope(command, tenant, organization, provider, account);
        command.Parameters.AddWithValue("before", NpgsqlDbType.Uuid, (object?)beforeSnapshotId ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", limit + 1);
        var rows = new List<CloudActivityHistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            rows.Add(new(reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetBoolean(5), reader.GetInt32(6), reader.GetGuid(2)));
        return new(rows.Take(limit).ToArray(), rows.Count > limit ? rows[limit - 1].SnapshotId : null);
    }

    private static void AddActivityScope(NpgsqlCommand command, Guid tenant, Guid organization, CloudProvider provider, string account)
    {
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("organization", organization);
        command.Parameters.AddWithValue("provider", (int)provider);
        command.Parameters.AddWithValue("account", account);
    }
    private static bool SafeActivityText(string? value, int maximum)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);
}
