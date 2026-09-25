namespace CloudEntitle.Domain;

public enum CloudProviderType
{
    Azure,
    Aws,
    Gcp
}

public enum ConnectionStatus
{
    Disconnected,
    Connected,
    Failed,
    Pending
}

public enum IdentityType
{
    User,
    Group,
    ServiceAccount,
    ManagedIdentity,
    Role,
    Unknown
}

public enum RiskSeverity
{
    Critical,
    High,
    Medium,
    Low,
    Informational
}

public enum LicenseStatus
{
    Trial,
    Active,
    Expired,
    Suspended
}

public class CloudAccount
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public CloudProviderType CloudProvider { get; set; }
    public string? SubscriptionId { get; set; }
    public string? ExternalAccountId { get; set; }
    public string? TenantId { get; set; }
    public string? ProjectId { get; set; }
    public ConnectionStatus ConnectionStatus { get; set; }
    public DateTime? LastScanAt { get; set; }
    public bool IsReadOnly { get; set; }
    public bool IsConnected => ConnectionStatus == ConnectionStatus.Connected;
}

public class CloudSubscription
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? SubscriptionId { get; set; }
    public string? TenantId { get; set; }
    public bool IsActive { get; set; }
}

public class CloudProject
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ProjectId { get; set; }
    public string? OrganizationId { get; set; }
}

public class CloudResource
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? ResourceId { get; set; }
    public string? Scope { get; set; }
    public string? CloudRegion { get; set; }
}

public class CloudPrincipal
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public CloudProviderType CloudProvider { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Email { get; set; }
    public IdentityType IdentityType { get; set; }
    public bool IsPrivileged { get; set; }
    public bool IsActive { get; set; }
    public string? LastActivity { get; set; }
    public string? PrincipalId { get; set; }
}

public class CloudGroup
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? GroupId { get; set; }
    public string? ProviderPath { get; set; }
}

public class CloudServiceAccount
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Email { get; set; }
    public bool IsActive { get; set; }
    public bool IsPrivileged { get; set; }
}

public class CloudRole
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string RoleId { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Scope { get; set; }
}

public class CloudPermission
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Action { get; set; }
    public string? Resource { get; set; }
    public string? Scope { get; set; }
    public string? Source { get; set; }
}

public class CloudPolicy
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? PolicyType { get; set; }
    public string? Document { get; set; }
    public bool IsManaged { get; set; }
}

public class RoleAssignment
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public Guid PrincipalId { get; set; }
    public Guid RoleId { get; set; }
    public string? Scope { get; set; }
    public string? Source { get; set; }
}

public class PermissionAssignment
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public Guid PrincipalId { get; set; }
    public Guid PermissionId { get; set; }
    public string? Scope { get; set; }
    public string? Source { get; set; }
}

public class RiskFinding
{
    public Guid Id { get; set; }
    public CloudProviderType CloudProvider { get; set; }
    public RiskSeverity Severity { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Principal { get; set; } = string.Empty;
    public string Resource { get; set; } = string.Empty;
    public string Permission { get; set; } = string.Empty;
    public string Evidence { get; set; } = string.Empty;
    public string Recommendation { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class RiskRule
{
    public Guid Id { get; set; }
    public CloudProviderType CloudProvider { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public RiskSeverity Severity { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string RuleExpression { get; set; } = string.Empty;
}

public class Recommendation
{
    public Guid Id { get; set; }
    public Guid FindingId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Remediation { get; set; } = string.Empty;
}
