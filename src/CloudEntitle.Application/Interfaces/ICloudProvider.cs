namespace CloudEntitle.Application.Interfaces;

using CloudEntitle.Domain;

public interface ICloudProvider
{
    CloudProviderType ProviderType { get; }
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CloudAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CloudPrincipal>> DiscoverIdentitiesAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CloudRole>> DiscoverRolesAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CloudPermission>> DiscoverPermissionsAsync(Guid accountId, CancellationToken cancellationToken = default);
}

public interface ICloudProviderDiagnostics
{
    IReadOnlyList<string> DrainProviderMessages();
}

public interface ICloudCommandRunner
{
    Task<CloudCommandResult> RunAsync(
        string command,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);
}

public sealed record CloudCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool IsSuccess => ExitCode == 0;
}

public interface IIdentityProvider
{
    Task<IReadOnlyList<CloudPrincipal>> GetPrincipalsAsync(Guid accountId, CancellationToken cancellationToken = default);
}

public interface IPermissionProvider
{
    Task<IReadOnlyList<CloudPermission>> GetPermissionsAsync(Guid accountId, CancellationToken cancellationToken = default);
}

public interface IResourceProvider
{
    Task<IReadOnlyList<CloudResource>> GetResourcesAsync(Guid accountId, CancellationToken cancellationToken = default);
}

public interface ICloudEffectiveAccessProvider
{
    Task<IReadOnlyList<CloudEffectiveAccessAssignment>> DiscoverEffectiveAccessAsync(Guid accountId, CancellationToken cancellationToken = default);
}

public sealed record CloudEffectiveAccessAssignment(string Principal, string Role, string Scope, string Source, bool IsPrivileged, string AssignmentType = "Assigned", DateTime? LastUsedAt = null, DateTime? LastChangedAt = null);

public interface ICloudOrganizationProvider
{
    Task<CloudOrganizationScanResult> DiscoverOrganizationAccountsAsync(CancellationToken cancellationToken = default);
}

public sealed record CloudOrganizationScanResult(IReadOnlyList<CloudAccount> Accounts, IReadOnlyList<string> ProviderMessages);

public interface IKubernetesRbacProvider
{
    Task<KubernetesRbacScanResult> ScanKubernetesRbacAsync(CancellationToken cancellationToken = default);
}

public sealed record KubernetesRbacScanResult(IReadOnlyList<KubernetesClusterScan> Clusters, IReadOnlyList<string> ProviderMessages);

public sealed record KubernetesClusterScan(
    CloudAccount Cluster,
    IReadOnlyList<CloudPrincipal> Principals,
    IReadOnlyList<CloudRole> Roles,
    IReadOnlyList<CloudPermission> Permissions,
    IReadOnlyList<CloudEffectiveAccessAssignment> AccessAssignments,
    IReadOnlyList<CloudResource> Resources);

public interface IEntraDirectoryProvider
{
    Task<EntraDirectoryScanResult> DiscoverEntraDirectoryAsync(Guid accountId, CancellationToken cancellationToken = default);
}

public interface IAccessAnalyzerFindingsProvider
{
    Task<IReadOnlyList<RiskFinding>> DiscoverAccessAnalyzerFindingsAsync(Guid accountId, CancellationToken cancellationToken = default);
}

public sealed record EntraDirectoryScanResult(
    IReadOnlyList<CloudPrincipal> Principals,
    IReadOnlyList<CloudEffectiveAccessAssignment> AccessAssignments,
    IReadOnlyList<string> ProviderMessages);

public interface IRoleProvider
{
    Task<IReadOnlyList<CloudRole>> GetRolesAsync(Guid accountId, CancellationToken cancellationToken = default);
}

public interface IAccessAnalyzer
{
    Task<IReadOnlyList<RiskFinding>> AnalyzeAsync(Guid accountId, CancellationToken cancellationToken = default);
}

public interface ILicenseService
{
    Task<LicenseValidationResult> ValidateAsync(string licenseKey, CancellationToken cancellationToken = default);
    Task<bool> ActivateAsync(string licenseKey, CancellationToken cancellationToken = default);
    Task<bool> DeactivateAsync(string licenseKey, CancellationToken cancellationToken = default);
}

public interface ITrialService
{
    Task<TrialInfo> StartTrialAsync(string userId, string deviceIdentifierHash, CancellationToken cancellationToken = default);
    Task<TrialInfo?> GetTrialStatusAsync(string userId, string deviceIdentifierHash, CancellationToken cancellationToken = default);
}

public interface IReportService
{
    Task<string> GenerateReportAsync(string reportType, CancellationToken cancellationToken = default);
    Task<string> GenerateReportAsync(string reportType, string outputPath, CancellationToken cancellationToken = default);
    Task<string> GenerateReportAsync(string reportType, string outputPath, IReadOnlyList<RiskFinding>? currentScanFindings, CancellationToken cancellationToken = default);
}

public interface IScanResultStore
{
    Task ReplaceFindingsAsync(IEnumerable<RiskFinding> findings, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RiskFinding>> GetRecentFindingsAsync(int limit = 100, CancellationToken cancellationToken = default);
}

public interface ICloudInventoryStore
{
    Task ReplaceInventoryAsync(
        IEnumerable<CloudAccount> accounts,
        IEnumerable<CloudPrincipal> principals,
        IEnumerable<CloudRole> roles,
        IEnumerable<CloudPermission> permissions,
        CancellationToken cancellationToken = default);

    Task<InventorySummary> GetSummaryAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CloudPrincipal>> GetPrincipalsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CloudRole>> GetRolesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CloudPermission>> GetPermissionsAsync(CancellationToken cancellationToken = default);
}

public sealed class InventorySummary
{
    public int AccountCount { get; init; }
    public int PrincipalCount { get; init; }
    public int RoleCount { get; init; }
    public int PermissionCount { get; init; }
}

public class LicenseValidationResult
{
    public bool IsValid { get; set; }
    public LicenseStatus Status { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime? ExpiresAt { get; set; }
}

public class TrialInfo
{
    public DateTime TrialStart { get; set; }
    public DateTime TrialEnd { get; set; }
    public LicenseStatus Status { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string DeviceIdentifierHash { get; set; } = string.Empty;
}
