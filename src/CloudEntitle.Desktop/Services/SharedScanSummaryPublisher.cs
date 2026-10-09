using System.Security.Cryptography;
using System.Text;
using CloudEntitle.Domain;
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
            scan.Coverage.Where(item => providers.Contains(item.Provider)).Select(item => new SharedScanCoverageDto(item.Provider, item.DataSource, item.Status.ToString(), item.Records, item.Details)).ToArray());
        return await sharedOrganizationApi.UploadScanSummaryAsync(organizationId, request, cancellationToken);
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
