using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
using Npgsql;

namespace CloudEntitle.LicenseServer.Services;

public sealed partial class SharedWorkspaceStore
{
    public async Task SaveTrustCredentialSnapshotAsync(Guid tenant, Guid organization, Guid user,
        TrustCredentialSnapshot snapshot, CancellationToken token = default)
    {
        ValidateTrustCredentialSnapshot(snapshot);
        var payload = JsonSerializer.Serialize(snapshot, JsonOptions);
        if (Encoding.UTF8.GetByteCount(payload) > 8 * 1024 * 1024)
            throw new ArgumentException("Trust and credential evidence exceeds 8 MiB.");
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            INSERT INTO SharedTrustCredentialSnapshots
              (TenantId,OrganizationId,SnapshotId,Provider,AccountId,StartedAtUtc,CollectedAtUtc,UploadedBy,PayloadJson)
            VALUES (@tenant,@organization,@snapshot,@provider,@account,@started,@collected,@user,@payload)
            ON CONFLICT DO NOTHING;
            SELECT PayloadJson FROM SharedTrustCredentialSnapshots
            WHERE TenantId=@tenant AND OrganizationId=@organization AND SnapshotId=@snapshot;
            """, connection);
        AddActivityScope(command, tenant, organization, snapshot.Provider, snapshot.AccountId);
        command.Parameters.AddWithValue("snapshot", snapshot.SnapshotId);
        command.Parameters.AddWithValue("started", snapshot.StartedAtUtc.ToUniversalTime());
        command.Parameters.AddWithValue("collected", snapshot.CollectedAtUtc.ToUniversalTime());
        command.Parameters.AddWithValue("user", user);
        command.Parameters.AddWithValue("payload", payload);
        if (await command.ExecuteScalarAsync(token) as string != payload)
            throw new InvalidOperationException("Trust and credential evidence identifier conflicts with stored evidence.");
    }

    public async Task<IReadOnlyList<TrustCredentialSnapshotSummary>> GetTrustCredentialHistoryAsync(
        Guid tenant, Guid organization, CloudProvider provider, string account, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT SnapshotId,StartedAtUtc,CollectedAtUtc,jsonb_array_length(PayloadJson::jsonb->'observations'),
                   (PayloadJson::jsonb->>'credentialInventoryComplete')::boolean,
                   (PayloadJson::jsonb->>'trustInventoryComplete')::boolean,UploadedBy
            FROM SharedTrustCredentialSnapshots
            WHERE TenantId=@tenant AND OrganizationId=@organization AND Provider=@provider AND AccountId=@account
            ORDER BY CollectedAtUtc DESC,SnapshotId DESC LIMIT 50;
            """, connection);
        AddActivityScope(command, tenant, organization, provider, account);
        var result = new List<TrustCredentialSnapshotSummary>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result.Add(new(reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetInt32(3), reader.GetBoolean(4), reader.GetBoolean(5), reader.GetGuid(6)));
        return result;
    }

    public async Task<TrustCredentialSnapshot?> GetTrustCredentialSnapshotAsync(Guid tenant, Guid organization,
        CloudProvider provider, string account, Guid snapshotId, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT PayloadJson FROM SharedTrustCredentialSnapshots
            WHERE TenantId=@tenant AND OrganizationId=@organization AND Provider=@provider AND AccountId=@account AND SnapshotId=@snapshot;
            """, connection);
        AddActivityScope(command, tenant, organization, provider, account);
        command.Parameters.AddWithValue("snapshot", snapshotId);
        var payload = await command.ExecuteScalarAsync(token) as string;
        return payload is null ? null : JsonSerializer.Deserialize<TrustCredentialSnapshot>(payload, JsonOptions)
            ?? throw new InvalidOperationException("Stored trust/credential evidence is invalid.");
    }

    public async Task RecordTrustCredentialMonitoringAsync(Guid tenant, Guid organization,
        TrustCredentialMonitoringRequest request, TrustCredentialMonitoringReport report, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        var rules = await ReadRulesAsync(connection, transaction, tenant, organization, token);
        foreach (var change in report.TrustCredentialChanges)
        {
            var id = OperationalId($"trust-credential|{tenant}|{organization}|{request.Provider}|{request.AccountId}|{change.PrincipalId}|{change.Kind}|{change.EvidenceId}|{change.Change}|{change.BeforeSha256}|{change.AfterSha256}");
            var changeId = request.TrustCredentialSnapshotId ?? Guid.Empty;
            await RecordOperationalEventAsync(connection, transaction, tenant, organization,
                new(id, changeId, Guid.Empty, request.Provider, request.AccountId, "trust-credential", "Changed",
                    $"{change.Change}|{change.Kind}|{id:D}", SharedAlertSeverity.Medium, change.BeforeSha256,
                    change.AfterSha256, DateTime.UtcNow)
                {
                    EventType = "trust-credentials.observedchange",
                    SubjectId = request.TrustCredentialSnapshotId
                }, token, rules);
        }
        foreach (var signal in report.PrivilegeEscalations)
        {
            var id = OperationalId($"privilege-escalation|{tenant}|{organization}|{request.Provider}|{request.AccountId}|{request.BaselineScanId}|{request.CurrentScanId}|{signal.Principal}|{signal.Role}|{signal.Scope}|{signal.Source}");
            await RecordOperationalEventAsync(connection, transaction, tenant, organization,
                new(id, request.CurrentScanId ?? Guid.Empty, request.BaselineScanId ?? Guid.Empty, request.Provider,
                    request.AccountId, "effective-access", "Added", $"{signal.Confidence}|{id:D}",
                    SharedAlertSeverity.High, null, null, DateTime.UtcNow)
                {
                    EventType = "privilege-escalation.potential",
                    SubjectId = request.CurrentScanId
                }, token, rules);
        }
        await transaction.CommitAsync(token);
    }

    public static void ValidateTrustCredentialSnapshot(TrustCredentialSnapshot snapshot)
    {
        if (snapshot is null || snapshot.SnapshotId == Guid.Empty
            || snapshot.Provider is not (CloudProvider.Azure or CloudProvider.Aws or CloudProvider.Gcp)
            || string.IsNullOrWhiteSpace(snapshot.AccountId) || !ValidTrustCredentialAccount(snapshot.Provider, snapshot.AccountId)
            || snapshot.StartedAtUtc < DateTimeOffset.UnixEpoch
            || snapshot.CollectedAtUtc < snapshot.StartedAtUtc || snapshot.CollectedAtUtc > DateTimeOffset.UtcNow.AddMinutes(1)
            || snapshot.CollectedAtUtc - snapshot.StartedAtUtc > TimeSpan.FromMinutes(4)
            || snapshot.Source != (snapshot.Provider switch
            {
                CloudProvider.Azure => "Azure CLI / Microsoft Graph Entra application credentials and federation",
                CloudProvider.Aws => "AWS IAM role trust policies and credential report metadata",
                CloudProvider.Gcp => "GCP project service-account keys and IAM impersonation policies",
                _ => ""
            })
            || snapshot.Observations is null || snapshot.Observations.Count > 20_000
            || snapshot.Limitations is null || snapshot.Limitations.Count is < 1 or > 16
            || snapshot.Limitations.Any(item => !SafeActivityText(item, 2048)))
            throw new ArgumentException("Invalid bounded trust/credential observation.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in snapshot.Observations)
            if (item is null || !SafeActivityText(item.PrincipalId, 512) || !SafeActivityText(item.PrincipalName, 512)
                || item.Kind is not ("PasswordCredential" or "KeyCredential" or "FederatedIdentityCredential"
                    or "IamRoleTrustPolicy" or "IamCredentialReport" or "GcpProjectIamBinding"
                    or "GcpServiceAccountIamBinding" or "GcpUserManagedServiceAccountKey")
                || !SafeActivityText(item.EvidenceId, 512) || !SafeActivityText(item.Name, 512)
                || item.Issuer is not null && !SafeActivityText(item.Issuer, 2048)
                || item.Subject is not null && !SafeActivityText(item.Subject, 2048)
                || item.Audiences is null || item.Audiences.Count > 32
                || item.Audiences.Any(value => !SafeActivityText(value, 2048))
                || item.ValidFromUtc < DateTimeOffset.UnixEpoch || item.ValidUntilUtc < DateTimeOffset.UnixEpoch
                || item.MetadataSha256 is not { Length: 64 } || item.MetadataSha256.Any(character => !char.IsAsciiHexDigit(character))
                || !keys.Add($"{item.Kind}\0{item.PrincipalId}\0{item.EvidenceId}"))
                throw new ArgumentException("Invalid or duplicate trust/credential observation.");
    }

    private static bool ValidTrustCredentialAccount(CloudProvider provider, string accountId)
        => provider switch
        {
            CloudProvider.Azure => Guid.TryParseExact(accountId, "D", out _),
            CloudProvider.Aws => Regex.IsMatch(accountId, "^[0-9]{12}$", RegexOptions.CultureInvariant),
            CloudProvider.Gcp => accountId.Length is >= 6 and <= 30
                && Regex.IsMatch(accountId, "^(?:[a-z][a-z0-9-]{4,28}[a-z0-9]|[0-9]{6,30})$", RegexOptions.CultureInvariant),
            _ => false
        };
}
