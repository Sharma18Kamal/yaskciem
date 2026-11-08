using System.Text;
using System.Text.Json;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
using Npgsql;

namespace CloudEntitle.LicenseServer.Services;

public sealed partial class SharedWorkspaceStore
{
    public async Task SaveEntitlementEvidenceAsync(Guid tenant, Guid organization, Guid user,
        EntitlementEvidenceSnapshot snapshot, CancellationToken token = default)
    {
        ValidateEntitlementEvidence(snapshot);
        var payload = JsonSerializer.Serialize(snapshot, JsonOptions);
        if (Encoding.UTF8.GetByteCount(payload) > 8 * 1024 * 1024) throw new ArgumentException("Entitlement evidence exceeds 8 MiB.");
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            INSERT INTO SharedEntitlementEvidenceSnapshots
              (TenantId,OrganizationId,SnapshotId,Provider,AccountId,StartedAtUtc,CollectedAtUtc,UploadedBy,PayloadJson)
            VALUES (@tenant,@organization,@snapshot,@provider,@account,@started,@collected,@user,@payload)
            ON CONFLICT DO NOTHING;
            SELECT PayloadJson FROM SharedEntitlementEvidenceSnapshots
            WHERE TenantId=@tenant AND OrganizationId=@organization AND SnapshotId=@snapshot;
            """, connection);
        AddActivityScope(command, tenant, organization, snapshot.Provider, snapshot.AccountId);
        command.Parameters.AddWithValue("snapshot", snapshot.SnapshotId);
        command.Parameters.AddWithValue("started", snapshot.StartedAtUtc.ToUniversalTime());
        command.Parameters.AddWithValue("collected", snapshot.CollectedAtUtc.ToUniversalTime());
        command.Parameters.AddWithValue("user", user);
        command.Parameters.AddWithValue("payload", payload);
        if (await command.ExecuteScalarAsync(token) as string != payload)
            throw new InvalidOperationException("Entitlement evidence identifier conflicts with stored evidence.");
    }

    public async Task<IReadOnlyList<EntitlementEvidenceHistoryEntry>> GetEntitlementEvidenceHistoryAsync(
        Guid tenant, Guid organization, CloudProvider provider, string account, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT SnapshotId,StartedAtUtc,CollectedAtUtc,UploadedBy,jsonb_array_length(PayloadJson::jsonb->'grants')
            FROM SharedEntitlementEvidenceSnapshots
            WHERE TenantId=@tenant AND OrganizationId=@organization AND Provider=@provider AND AccountId=@account
            ORDER BY CollectedAtUtc DESC,SnapshotId DESC LIMIT 50;
            """, connection);
        AddActivityScope(command, tenant, organization, provider, account);
        var result = new List<EntitlementEvidenceHistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result.Add(new(reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetInt32(4), reader.GetGuid(3)));
        return result;
    }

    public async Task<EntitlementEvidenceSnapshot?> GetEntitlementEvidenceAsync(Guid tenant, Guid organization,
        CloudProvider provider, string account, Guid snapshotId, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT PayloadJson FROM SharedEntitlementEvidenceSnapshots
            WHERE TenantId=@tenant AND OrganizationId=@organization AND Provider=@provider AND AccountId=@account AND SnapshotId=@snapshot;
            """, connection);
        AddActivityScope(command, tenant, organization, provider, account);
        command.Parameters.AddWithValue("snapshot", snapshotId);
        var payload = await command.ExecuteScalarAsync(token) as string;
        return payload is null ? null : JsonSerializer.Deserialize<EntitlementEvidenceSnapshot>(payload, JsonOptions)
            ?? throw new InvalidOperationException("Stored entitlement evidence is invalid.");
    }

    public static void ValidateEntitlementEvidence(EntitlementEvidenceSnapshot snapshot)
    {
        if (snapshot is null || snapshot.SnapshotId == Guid.Empty || snapshot.Provider is not (CloudProvider.Azure or CloudProvider.Aws or CloudProvider.Gcp)
            || !SafeActivityText(snapshot.AccountId, 128) || snapshot.StartedAtUtc < DateTimeOffset.UnixEpoch
            || snapshot.CollectedAtUtc < snapshot.StartedAtUtc || snapshot.CollectedAtUtc > DateTimeOffset.UtcNow.AddMinutes(1)
            || snapshot.CollectedAtUtc - snapshot.StartedAtUtc > TimeSpan.FromMinutes(5)
            || snapshot.Grants is null || snapshot.Grants.Count > 20_000 || snapshot.Limitations is null
            || snapshot.Limitations.Count is < 1 or > 32 || snapshot.Limitations.Any(item => !SafeActivityText(item, 2048)))
            throw new ArgumentException("Invalid bounded entitlement observation.");
        foreach (var grant in snapshot.Grants)
            if (grant is null || !SafeActivityText(grant.Principal, 2048) || !SafeActivityText(grant.SourcePrincipal, 2048)
                || !SafeActivityText(grant.Role, 2048) || !SafeActivityText(grant.SourceId, 4096)
                || !SafeActivityText(grant.SourceVersion, 256) || grant.SourceSha256 is not { Length: 64 }
                || grant.SourceSha256.Any(c => !char.IsAsciiHexDigit(c))
                || !SafeActivityText(grant.StatementId, 256) || !SafeActivityText(grant.Scope, 4096)
                || !SafeActivityText(grant.Action, 2048) || !SafeActivityText(grant.ResourcePattern, 4096)
                || grant.Effect is not ("Allow" or "Deny")
                || grant.IsExclusion && (snapshot.Provider != CloudProvider.Azure || grant.Effect != "Allow"))
                throw new ArgumentException("Invalid observed permission statement.");
    }
}
