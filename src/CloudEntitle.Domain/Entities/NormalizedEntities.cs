using CloudEntitle.Domain.ValueObjects;

namespace CloudEntitle.Domain.Entities;

public class GroupMembership
{
    public Guid Id { get; set; }
    public Guid GroupId { get; set; }
    public Guid PrincipalId { get; set; }
    public Guid CloudAccountId { get; set; }
    public MembershipType MembershipType { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

public class Entitlement
{
    public Guid Id { get; set; }
    public Guid PrincipalId { get; set; }
    public Guid? RoleId { get; set; }
    public Guid? PolicyId { get; set; }
    public Guid PermissionId { get; set; }
    public Guid CloudAccountId { get; set; }
    public Guid? ResourceId { get; set; }
    public string? Scope { get; set; }
    public AssignmentType AssignmentType { get; set; }
    public DateTime GrantedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? Source { get; set; }
}

public class PolicyAssignment
{
    public Guid Id { get; set; }
    public Guid PrincipalId { get; set; }
    public Guid PolicyId { get; set; }
    public Guid CloudAccountId { get; set; }
    public Guid? ResourceId { get; set; }
    public string? Scope { get; set; }
    public AssignmentType AssignmentType { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

public class EffectiveAccess
{
    public Guid Id { get; set; }
    public Guid PrincipalId { get; set; }
    public Guid CloudAccountId { get; set; }
    public Guid? ResourceId { get; set; }
    public Guid PermissionId { get; set; }
    public Guid? RoleId { get; set; }
    public Guid? PolicyId { get; set; }
    public string? Scope { get; set; }
    public AccessType AccessType { get; set; }
    public AssignmentType AssignmentType { get; set; }
    public Guid? AccessPathId { get; set; }
    public bool IsDirect { get; set; }
    public bool IsInherited { get; set; }
    public bool IsConditional { get; set; }
    public bool IsPrivileged { get; set; }
    public DateTime CalculatedAt { get; set; }
    public CloudProvider Provider { get; set; }
    public AccessDecision Decision { get; set; } = AccessDecision.Unknown;
    public EvaluationStatus EvaluationStatus { get; set; } = EvaluationStatus.Invalid;
    public string? AccessSource { get; set; }
    public string? Evidence { get; set; }
    public string? EvaluationDetails { get; set; }
}

public class AccessPath
{
    public Guid Id { get; set; }
    public Guid PrincipalId { get; set; }
    public Guid CloudAccountId { get; set; }
    public Guid? TargetResourceId { get; set; }
    public Guid? ResourceId { get; set; }
    public Guid? PermissionId { get; set; }
    public AccessPathType PathType { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<AccessPathNode> Nodes { get; set; } = new();
    public CloudProvider Provider { get; set; }
    public Guid? EffectiveAccessId { get; set; }
    public AccessPathStatus PathStatus { get; set; } = AccessPathStatus.Unknown;
    public string? Evidence { get; set; }
    public string? EngineVersion { get; set; }
    public List<AccessPathEdge> Edges { get; set; } = new();
}

public class AccessPathNode
{
    public Guid Id { get; set; }
    public Guid AccessPathId { get; set; }
    public string NodeType { get; set; } = string.Empty;
    public Guid NodeId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Sequence { get; set; }
    public CloudProvider Provider { get; set; }
    public Guid? EntityId { get; set; }
    public string? EvidenceReference { get; set; }
    public string? Metadata { get; set; }
}

public class AccessPathEdge
{
    public Guid Id { get; set; }
    public Guid AccessPathId { get; set; }
    public Guid FromNodeId { get; set; }
    public Guid ToNodeId { get; set; }
    public string RelationshipType { get; set; } = string.Empty;
    public CloudProvider Provider { get; set; }
    public string? EvidenceReference { get; set; }
    public string? Metadata { get; set; }
}

public class ActivityRecord
{
    public Guid Id { get; set; }
    public Guid PrincipalId { get; set; }
    public Guid CloudAccountId { get; set; }
    public Guid? ResourceId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? Service { get; set; }
    public DateTime Timestamp { get; set; }
    public string? Source { get; set; }
    public bool Success { get; set; }
}

public class RiskFindingModel
{
    public Guid Id { get; set; }
    public Guid RuleId { get; set; }
    public CloudProvider CloudProvider { get; set; }
    public Guid CloudAccountId { get; set; }
    public Guid PrincipalId { get; set; }
    public Guid? ResourceId { get; set; }
    public Guid? EffectiveAccessId { get; set; }
    public RiskSeverity Severity { get; set; }
    public FindingStatus Status { get; set; } = FindingStatus.Open;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public RiskEvidence Evidence { get; set; } = new("Evidence not supplied.");
    public string? Recommendation { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

public class RiskRuleModel
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public RiskSeverity Severity { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class RecommendationModel
{
    public Guid Id { get; set; }
    public Guid RiskFindingId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string CurrentState { get; set; } = string.Empty;
    public string SuggestedState { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public int Priority { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class Scan
{
    public Guid Id { get; set; }
    public Guid CloudAccountId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public ScanStatus Status { get; set; } = ScanStatus.Pending;
    public int ItemsDiscovered { get; set; }
    public int IdentitiesDiscovered { get; set; }
    public int PermissionsDiscovered { get; set; }
    public int FindingsGenerated { get; set; }
    public string? ErrorMessage { get; set; }
}

public class AuditEvent
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public Guid? OrganizationId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string? IpAddress { get; set; }
    public IReadOnlyDictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
}
