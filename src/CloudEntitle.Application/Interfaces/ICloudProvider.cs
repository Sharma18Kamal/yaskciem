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
    Task<TrialInfo?> GetTrialStatusAsync(string userId, CancellationToken cancellationToken = default);
}

public interface IReportService
{
    Task<string> GenerateReportAsync(string reportType, CancellationToken cancellationToken = default);
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
