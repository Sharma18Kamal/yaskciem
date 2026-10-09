namespace CloudEntitle.Domain;

using System.ComponentModel.DataAnnotations.Schema;

public enum CloudProviderType
{
    Azure,
    Aws,
    Gcp,
    Kubernetes
}

public enum CloudProvider
{
    Azure,
    Aws,
    Gcp,
    Kubernetes
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
    ServicePrincipal,
    ServiceAccount,
    ManagedIdentity,
    Role,
    Application,
    Unknown
}

public enum AccountType
{
    Tenant,
    ManagementGroup,
    Subscription,
    AwsOrganization,
    AwsAccount,
    GcpOrganization,
    Folder,
    Project,
    Cluster
}

public enum AssignmentType
{
    Direct,
    Group,
    Inherited,
    Policy,
    Role,
    Application,
    AssumedRole,
    ServiceIdentity,
    Dynamic,
    Conditional,
    Unknown
}

public enum AccessType
{
    Allow,
    Deny
}

public enum FindingStatus
{
    Open,
    Resolved,
    Accepted,
    Ignored
}

public enum ResourceType
{
    Organization,
    Account,
    Subscription,
    ResourceGroup,
    Project,
    Folder,
    Resource,
    Unknown
}

public enum IdentityStatus
{
    Active,
    Inactive,
    Disabled,
    Unknown
}

public enum MembershipType
{
    Direct,
    Inherited,
    Dynamic,
    Unknown
}

public enum AccessPathType
{
    Direct,
    GroupBased,
    NestedGroup,
    Inherited,
    RoleBased,
    PolicyBased,
    AssumedRole,
    ServiceIdentity,
    Conditional,
    Mixed,
    ApplicationBased,
    Unknown
}

public enum AccessPathStatus
{
    Complete,
    Partial,
    Unknown,
    Unsupported,
    Invalid
}

public enum ScanStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled
}

public enum AccessDecision
{
    Allow,
    Deny,
    Conditional,
    NotApplicable,
    Unknown,
    Unsupported
}

public enum EvaluationStatus
{
    FullyEvaluated,
    PartiallyEvaluated,
    ConditionRequired,
    Unsupported,
    Invalid
}

public enum RiskSeverity
{
    Critical,
    High,
    Medium,
    Low,
    Informational
}

public enum RiskEvaluationState
{
    Confirmed,
    Potential,
    Unknown,
    Unsupported
}

public enum RiskCategory
{
    Privilege,
    ExcessiveAccess,
    Scope,
    Identity,
    Dormancy,
    ExternalAccess,
    ServiceIdentity,
    Policy,
    AccessPathComplexity,
    Configuration
}

public enum RecommendationStatus
{
    New,
    Reviewed,
    Approved,
    Rejected,
    Deferred,
    Implemented,
    NotApplicable
}

public enum RecommendationType
{
    RemoveUnusedPermission,
    NarrowWildcardPermission,
    NarrowResourceScope,
    ReplaceBroadRole,
    RemoveRedundantAccessPath,
    RemoveDormantPrivilege,
    ReduceServiceAccountPermission,
    ReducePrivilegedGroupAccess,
    ReduceNestedPrivilege,
    ReduceLongPrivilegeChain,
    ConsolidateAccess,
    GenerateLeastPrivilegePolicy,
    RemoveUnusedRole
}

public enum RecommendationImpactState
{
    NoKnownImpact,
    LowPotentialImpact,
    RequiresReview,
    HighPotentialImpact,
    UnknownImpact
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
    public AccountType AccountType { get; set; } = AccountType.Subscription;
    public string? ParentAccountId { get; set; }
    public string? Description { get; set; }
    public IdentityStatus Status { get; set; } = IdentityStatus.Unknown;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastScannedAt { get; set; }
    public string? ExternalId
    {
        get => ExternalAccountId ?? SubscriptionId ?? ProjectId;
        set => ExternalAccountId = value;
    }
    public CloudProvider Provider => this.CloudProvider switch
    {
        CloudProviderType.Azure => global::CloudEntitle.Domain.CloudProvider.Azure,
        CloudProviderType.Aws => global::CloudEntitle.Domain.CloudProvider.Aws,
        CloudProviderType.Kubernetes => global::CloudEntitle.Domain.CloudProvider.Kubernetes,
        _ => global::CloudEntitle.Domain.CloudProvider.Gcp
    };

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ExternalId))
        {
            throw new ArgumentException("CloudAccount requires an ExternalId.", nameof(ExternalId));
        }
    }
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
    public Guid CloudAccountId { get => AccountId; set => AccountId = value; }
    public string ExternalId { get => ResourceId ?? string.Empty; set => ResourceId = value; }
    public ResourceType ResourceType { get; set; } = ResourceType.Unknown;
    public string? ParentResourceId { get; set; }
    public string? ResourceUri { get; set; }
    public string? Region { get => CloudRegion; set => CloudRegion = value; }
    public string? Location { get; set; }
    public Dictionary<string, string> Tags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
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
    public Guid CloudAccountId { get => AccountId; set => AccountId = value; }
    public string ExternalId { get => PrincipalId ?? string.Empty; set => PrincipalId = value; }
    public string DisplayName { get => Name; set => Name = value; }
    public IdentityStatus Status { get => IsActive ? IdentityStatus.Active : IdentityStatus.Inactive; set => IsActive = value == IdentityStatus.Active; }
    public bool IsExternal { get; set; }
    public DateTime? LastActivityAt { get; set; }
    public DateTime? LastObservedWorkloadAtUtc { get; set; }
    public bool? ActivityEvidenceComplete { get; set; }
    public int? ActivityObservationDays { get; set; }
    public string? ActivityEvidenceSource { get; set; }
    public List<string> OwnerIds { get; set; } = new();
    public bool? OwnerInventoryComplete { get; set; }
    public string? OwnerEvidenceSource { get; set; }
    public DateTime? CredentialLastRotatedAtUtc { get; set; }
    public DateTime? CredentialExpiresAtUtc { get; set; }
    public bool? CredentialInventoryComplete { get; set; }
    public string? CredentialEvidenceSource { get; set; }
    public string? FederationIssuer { get; set; }
    public string? FederationSubject { get; set; }
    public string? FederationAudience { get; set; }
    public List<WorkloadFederationTrust> FederationTrusts { get; set; } = new();
    public bool? FederationTrustVerified { get; set; }
    public bool? FederationInventoryComplete { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    [NotMapped]
    public string OwnerSummary => OwnerIds.Count > 0 ? string.Join(", ", OwnerIds)
        : OwnerInventoryComplete == true ? "Unowned"
        : OwnerInventoryComplete == false ? "Unknown / incomplete"
        : "Not assessed";

    [NotMapped]
    public string FederationSummary => FederationTrusts.Count > 0
        ? string.Join("; ", FederationTrusts.Select(trust => $"{trust.Issuer} | {trust.Subject}"))
        : FederationInventoryComplete == true ? "No federation configured"
        : FederationInventoryComplete == false ? "Unknown / incomplete"
        : "Not assessed";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            throw new ArgumentException("CloudPrincipal requires a DisplayName.", nameof(DisplayName));
        }
    }
}

public sealed record WorkloadFederationTrust(string Issuer, string Subject, IReadOnlyList<string> Audiences, string Source);

public class CloudGroup
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? GroupId { get; set; }
    public string? ProviderPath { get; set; }
    public string? DisplayName { get => Name; set => Name = value ?? string.Empty; }
    public string? Description { get; set; }
    public IdentityStatus Status { get; set; } = IdentityStatus.Unknown;
    public List<Guid> MemberPrincipalIds { get; set; } = new();
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
    public string ExternalId { get => RoleId; set => RoleId = value; }
    public bool IsPrivileged { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
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
    public string? Provider { get; set; }
    public string? Service { get; set; }
    public string? ActionName { get => Action; set => Action = value; }
    public string? ResourcePattern { get => Resource; set => Resource = value; }
    public AccessType AccessType { get; set; } = AccessType.Allow;
    public bool IsExclusion { get; set; }
    public string? Description { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ActionName))
        {
            throw new ArgumentException("CloudPermission requires an Action.", nameof(ActionName));
        }
    }
}

public class CloudPolicy
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? PolicyType { get; set; }
    public string? Document { get; set; }
    public bool IsManaged { get; set; }
    public string? ExternalId { get; set; }
    public string? DocumentVersion { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
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
    public Guid RuleId { get; set; }
    public Guid CloudAccountId { get; set; }
    public Guid PrincipalId { get; set; }
    public Guid? ResourceId { get; set; }
    public Guid? EffectiveAccessId { get; set; }
    public FindingStatus Status { get; set; } = FindingStatus.Open;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }

    public void Validate()
    {
        if (RuleId == Guid.Empty)
        {
            throw new ArgumentException("RiskFinding requires a RuleId.", nameof(RuleId));
        }

        if (!Enum.IsDefined(Severity))
        {
            throw new ArgumentException("RiskFinding requires a valid Severity.", nameof(Severity));
        }
    }
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
    public string Code { get; set; } = string.Empty;
    public bool Enabled { get => IsEnabled; set => IsEnabled = value; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

public class Recommendation
{
    public Guid Id { get; set; }
    public Guid FindingId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Remediation { get; set; } = string.Empty;
    public Guid RiskFindingId { get => FindingId; set => FindingId = value; }
    public string CurrentState { get; set; } = string.Empty;
    public string SuggestedState { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public int Priority { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public RecommendationType RecommendationType { get; set; }
    public CloudProvider Provider { get; set; }
    public Guid CloudAccountId { get; set; }
    public Guid? PrincipalId { get; set; }
    public Guid? ResourceId { get; set; }
    public Guid? EffectiveAccessId { get; set; }
    public Guid? AccessPathId { get; set; }
    public Guid? EntitlementId { get; set; }
    public string Changes { get; set; } = string.Empty;
    public string Impact { get; set; } = string.Empty;
    public string? Evidence { get; set; }
    public RiskEvaluationState EvaluationState { get; set; } = RiskEvaluationState.Unknown;
    public RecommendationImpactState ImpactState { get; set; } = RecommendationImpactState.UnknownImpact;
    public RecommendationStatus Status { get; set; } = RecommendationStatus.New;
    public string Fingerprint { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
