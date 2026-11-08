using System.Text.Json;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
using Npgsql;

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
            || !SafeActivityText(snapshot.Source, 128) || !SafeActivityText(snapshot.CoverageDetails, 2048))
            throw new ArgumentException("Invalid scoped 90-day activity snapshot.");
        foreach (var item in snapshot.Events)
        {
            if (item is null || !SafeActivityText(item.EventId, 2048) || !SafeActivityText(item.Principal, 2048)
                || !SafeActivityText(item.Action, 2048)
                || item.PermissionAction is not null && !SafeActivityText(item.PermissionAction, 2048)
                || item.Resource is not null && !SafeActivityText(item.Resource, 4096)
                || item.Timestamp < snapshot.FromUtc || item.Timestamp > snapshot.ThroughUtc)
                throw new ArgumentException("Invalid activity event or timestamp.");
        }
    }

    private static bool SafeActivityText(string? value, int maximum)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);
}
