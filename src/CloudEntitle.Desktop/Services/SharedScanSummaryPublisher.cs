using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.OrganizationManagement.Models;

namespace CloudEntitle.Desktop.Services;

public interface ISharedScanSummaryPublisher
{
    Task<SharedScanSummaryDto?> PublishCurrentAsync(Guid organizationId, CancellationToken cancellationToken = default);
}

public sealed class SharedScanSummaryPublisher(
    ILiveCloudScanService liveScanService,
    ISharedOrganizationApi sharedOrganizationApi) : ISharedScanSummaryPublisher
{
    public async Task<SharedScanSummaryDto?> PublishCurrentAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        if (organizationId == Guid.Empty) throw new ArgumentException("An organization ID is required.", nameof(organizationId));
        var scan = liveScanService.CurrentResult ?? throw new InvalidOperationException("Run a live scan before publishing a shared scan summary.");
        var workspace = await sharedOrganizationApi.GetWorkspaceAsync(organizationId, cancellationToken);
        var bound = workspace.Bindings.ToArray();
        var scannedAccounts = scan.AccountRecords.Where(account => account.ScanAccess.StartsWith("Scanned", StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse<CloudProvider>(account.Provider, true, out var provider)
                && bound.Any(binding => binding.Provider == provider && binding.ExternalTenantId.Equals(account.AccountId, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(account => (account.Provider, account.AccountId), StringTupleComparer.Instance)
            .Select(group => group.First()).ToArray();
        if (scannedAccounts.Length == 0) return null;

        var accountIds = scannedAccounts.Select(account => account.CloudAccountId).Where(id => id != Guid.Empty).ToHashSet();
        var externalIds = scannedAccounts.Select(account => account.AccountId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var providers = scannedAccounts.Select(account => account.Provider).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var providerName = string.Join(", ", providers.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        var scanId = StableScanId(scan.StartedAt, scannedAccounts);
        var externalAccountById = scannedAccounts
            .Where(account => account.CloudAccountId != Guid.Empty)
            .GroupBy(account => account.CloudAccountId)
            .ToDictionary(group => group.Key, group => group.First().AccountId);
        var providerByAccountId = scannedAccounts
            .Where(account => account.CloudAccountId != Guid.Empty)
            .GroupBy(account => account.CloudAccountId)
            .ToDictionary(group => group.Key, group => Enum.Parse<CloudProvider>(group.First().Provider, true));
        var externalAccountsByProvider = scannedAccounts
            .GroupBy(account => account.Provider, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(account => account.AccountId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
        var records = new List<SharedCiemRecordDto>();
        records.AddRange(CreateRecords("identity", scan.IdentityRecords, item => item.AccountId, item => item.Id, externalAccountById, providerByAccountId));
        records.AddRange(CreateRecords("resource", scan.ResourceRecords, item => item.AccountId, item => item.Id, externalAccountById, providerByAccountId));
        records.AddRange(CreateRecords("role", scan.RoleRecords, item => item.Role.AccountId, item => item.Role.Id, externalAccountById, providerByAccountId, item => Enum.Parse<CloudProvider>(item.Provider.ToString(), true)));
        records.AddRange(CreateRecords("permission", scan.PermissionRecords, item => item.Permission.AccountId, item => item.Permission.Id, externalAccountById, providerByAccountId, item => Enum.Parse<CloudProvider>(item.Provider.ToString(), true)));
        records.AddRange(CreateRecords("finding", scan.Findings, item => item.CloudAccountId, item => item.Id, externalAccountById, providerByAccountId, item => Enum.Parse<CloudProvider>(item.CloudProvider.ToString(), true)));
        records.AddRange(CreateRecords("resource-finding", scan.ResourceRiskFindings, item => item.CloudAccountId, item => item.Id, externalAccountById, providerByAccountId, item => item.CloudProvider));
        records.AddRange(CreateRecords("access-path", scan.ModeledAccessPaths, item => item.CloudAccountId, item => item.Id, externalAccountById, providerByAccountId, item => item.Provider));
        foreach (var access in scan.EffectiveAccessRecords)
        {
            var candidates = scannedAccounts.Where(account => account.Provider.Equals(access.Provider, StringComparison.OrdinalIgnoreCase)).ToArray();
            var accountId = candidates.Length == 1 ? candidates[0].AccountId : access.AccountId;
            if (string.IsNullOrWhiteSpace(accountId)
                || !candidates.Any(account => account.AccountId.Equals(accountId, StringComparison.OrdinalIgnoreCase)))
            {
                var providerAccounts = externalAccountsByProvider.TryGetValue(access.Provider, out var values) ? values : Array.Empty<string>();
                if (providerAccounts.Length != 1) continue;
                accountId = providerAccounts[0];
            }
            records.Add(new SharedCiemRecordDto(Enum.Parse<CloudProvider>(access.Provider, true), "effective-access", StableRecordId(access), accountId,
                JsonSerializer.Serialize(access)));
        }
        var request = new UploadSharedScanSummaryRequest(scanId, scan.StartedAt.ToUniversalTime(), scan.CompletedAt.ToUniversalTime(), providerName,
            scannedAccounts.Select(account => new SharedScanAccountDto(Enum.Parse<CloudProvider>(account.Provider, true), account.AccountId, account.AccountName)).ToArray(),
            scan.IdentityRecords.Count(identity => accountIds.Contains(identity.AccountId)),
            scan.ResourceRecords.Count(resource => accountIds.Contains(resource.AccountId)),
            scan.RoleRecords.Count(role => accountIds.Contains(role.Role.AccountId)),
            scan.PermissionRecords.Count(permission => accountIds.Contains(permission.Permission.AccountId)),
            scan.EffectiveAccessRecords.Count(access => access.AccountId is not null && externalIds.Contains(access.AccountId)),
            scan.Findings.Count(finding => accountIds.Contains(finding.CloudAccountId)),
            scan.Findings.Count(finding => accountIds.Contains(finding.CloudAccountId) && finding.Severity == RiskSeverity.Critical),
            scan.Findings.Count(finding => accountIds.Contains(finding.CloudAccountId) && finding.Severity == RiskSeverity.High),
            scan.Coverage.Where(item => providers.Contains(item.Provider)).Select(item => new SharedScanCoverageDto(item.Provider, item.DataSource, item.Status.ToString(), item.Records, item.Details)).ToArray())
        {
            Records = records.OrderBy(record => record.Kind, StringComparer.Ordinal)
                .ThenBy(record => record.ExternalAccountId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(record => record.RecordId, StringComparer.Ordinal).ToArray()
        };
        return await sharedOrganizationApi.UploadScanSummaryAsync(organizationId, request, cancellationToken);
    }

    private static IEnumerable<SharedCiemRecordDto> CreateRecords<T>(
        string kind,
        IEnumerable<T> records,
        Func<T, Guid> accountIdSelector,
        Func<T, Guid> recordIdSelector,
        IReadOnlyDictionary<Guid, string> externalAccountById,
        IReadOnlyDictionary<Guid, CloudProvider> providerByAccountId,
        Func<T, CloudProvider>? providerSelector = null)
    {
        foreach (var record in records)
        {
            var accountId = accountIdSelector(record);
            if (!externalAccountById.TryGetValue(accountId, out var externalAccountId)
                || !providerByAccountId.TryGetValue(accountId, out var provider)) continue;
            var recordId = recordIdSelector(record);
            if (recordId == Guid.Empty) continue;
            if (providerSelector is not null) provider = providerSelector(record);
            yield return new SharedCiemRecordDto(provider, kind, recordId.ToString("D"), externalAccountId, JsonSerializer.Serialize(record));
        }
    }

    private static string StableRecordId(LiveEffectiveAccessRecord access)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(access)))).ToLowerInvariant();
    }

    private static Guid StableScanId(DateTime startedAt, IReadOnlyList<LiveScanAccountRecord> accounts)
    {
        var material = $"{startedAt.ToUniversalTime():O}|{string.Join("|", accounts.Select(account => account.SelectionKey).OrderBy(key => key, StringComparer.OrdinalIgnoreCase))}";
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(material)).AsSpan(0, 16));
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string Provider, string AccountId)>
    {
        public static StringTupleComparer Instance { get; } = new();
        public bool Equals((string Provider, string AccountId) x, (string Provider, string AccountId) y)
            => x.Provider.Equals(y.Provider, StringComparison.OrdinalIgnoreCase) && x.AccountId.Equals(y.AccountId, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Provider, string AccountId) value)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Provider), StringComparer.OrdinalIgnoreCase.GetHashCode(value.AccountId));
    }
}
