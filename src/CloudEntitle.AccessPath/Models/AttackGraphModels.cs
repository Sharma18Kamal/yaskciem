using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;

namespace CloudEntitle.AccessPath.Models;

public enum AttackGraphNodeType
{
    Identity,
    Role,
    Policy,
    Permission,
    Resource
}

public enum AttackEdgeFeasibility
{
    Verified,
    Potential,
    Denied,
    Unknown
}

public enum AttackPathReachability
{
    VerifiedReachable,
    Potential,
    Theoretical
}

public enum AttackPathCategory
{
    InitialAccess,
    LateralMovement,
    PrivilegeEscalation,
    CrossAccount,
    CrossCloudFederation,
    SensitiveResourceExposure
}

public sealed record AttackTrustRelationship(
    Guid SourcePrincipalId,
    Guid SourceCloudAccountId,
    CloudProvider SourceProvider,
    Guid TargetPrincipalId,
    Guid TargetCloudAccountId,
    CloudProvider TargetProvider,
    string RelationshipType,
    string Status,
    string EvidenceSource,
    string EvidenceReference,
    DateTime ObservedAtUtc,
    bool RequiresSourcePermission = false,
    string? RequiredSourcePermissionPattern = null,
    string ConditionState = "Satisfied",
    string? ConditionEvidence = null,
    string? ExternalSourceName = null,
    string? ExternalSourceDetails = null,
    bool CreateSourceNode = false,
    bool SourceIsInitialAccessPoint = false);

public sealed record AttackGraphNode(
    string Key,
    AttackGraphNodeType Type,
    Guid? EntityId,
    CloudProvider Provider,
    Guid CloudAccountId,
    string Name,
    string? EvidenceReference,
    bool IsInitialAccessPoint = false,
    bool IsSensitive = false,
    string? Details = null,
    bool IsPrivileged = false);

public sealed record AttackGraphEdge(
    string Key,
    string FromNodeKey,
    string ToNodeKey,
    string RelationshipType,
    CloudProvider Provider,
    Guid CloudAccountId,
    AttackEdgeFeasibility Feasibility,
    string EvidenceSource,
    string EvidenceReference,
    Guid? EffectiveAccessId = null,
    string? Scope = null,
    string? ConditionEvidence = null,
    string? Details = null);

public sealed record AttackPath(
    string Id,
    string EntryPointNodeKey,
    string TargetNodeKey,
    AttackPathCategory Category,
    AttackPathReachability Reachability,
    int RiskScore,
    IReadOnlyList<string> NodeKeys,
    IReadOnlyList<string> EdgeKeys,
    string EvidenceSummary);

public sealed record AttackPathFinding(
    string Id,
    string AttackPathId,
    AttackPathCategory Category,
    AttackPathReachability Reachability,
    RiskSeverity Severity,
    int RiskScore,
    string Title,
    string Description,
    string EvidenceSummary,
    IReadOnlyList<string> EvidenceReferences);

public sealed record AttackGraph(
    string Id,
    DateTime EvaluatedAtUtc,
    IReadOnlyList<AttackGraphNode> Nodes,
    IReadOnlyList<AttackGraphEdge> Edges,
    IReadOnlyList<AttackPath> Paths,
    IReadOnlyList<AttackPathFinding> Findings,
    IReadOnlyList<string> Diagnostics,
    bool IsComplete);

public sealed record AttackGraphBuildRequest(
    IReadOnlyList<CloudPrincipal> Principals,
    IReadOnlyList<CloudRole> Roles,
    IReadOnlyList<CloudPolicy> Policies,
    IReadOnlyList<CloudPermission> Permissions,
    IReadOnlyList<CloudResource> Resources,
    IReadOnlyList<CloudEntitle.Domain.Entities.EffectiveAccess> EffectiveAccess,
    IReadOnlyList<AttackTrustRelationship> TrustRelationships,
    IReadOnlyList<ResourceSecuritySignal>? ResourceSecuritySignals = null,
    int MaxPathDepth = 8,
    int MaxPaths = 1000,
    int MaxNodes = 10000,
    int MaxEdges = 25000,
    int MaxTraversalOperations = 100000,
    DateTime? EvaluatedAtUtc = null);
