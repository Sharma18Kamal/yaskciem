using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Normalization.Models;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;

namespace CloudEntitle.EffectiveAccess.Models;

public sealed record AccessEvaluationRequest(
    NormalizationResult Normalized,
    IReadOnlyList<CloudResource> Resources,
    IReadOnlyList<GroupMembership> GroupMemberships,
    IReadOnlyList<CloudPrincipal>? Principals = null,
    Guid? PrincipalId = null,
    Guid? ResourceId = null,
    Guid? PermissionId = null,
    DateTime? EvaluationTime = null,
    IReadOnlyDictionary<string, string>? RuntimeConditionContext = null,
    IReadOnlyList<RoleTrustRelationship>? RoleTrustRelationships = null);

public sealed record AccessEvaluationContext(
    CloudProvider Provider,
    Guid CloudAccountId,
    Guid PrincipalId,
    Guid ResourceId,
    Guid PermissionId,
    DateTime EvaluationTime,
    IReadOnlyDictionary<string, string> RuntimeConditionContext)
{
    public CloudResource? Resource { get; init; }
    public CloudPermission? Permission { get; init; }
    public IReadOnlyList<NormalizedEntitlement> ApplicableEntitlements { get; init; } = Array.Empty<NormalizedEntitlement>();
    public bool HasIncompleteNormalization { get; init; }
}

public sealed record RoleTrustRelationship(
    CloudProvider Provider,
    Guid CloudAccountId,
    Guid? TrustedPrincipalId,
    string? TrustedPrincipalExternalId,
    Guid RolePrincipalId,
    Guid RoleId,
    string SourceId,
    string Description);

public sealed record AccessPathStep(string Type, string Name, string? EvidenceSource, string? Details);

public sealed record AccessExplanationPath(string SourceId, string Decision, IReadOnlyList<AccessPathStep> Steps);

public sealed record AccessEvaluationDiagnostic(string Code, string Message, string? SourceId = null, bool IsBlocking = false);

public sealed record AccessDecisionResult(
    AccessDecision Decision,
    EvaluationStatus EvaluationStatus,
    bool IsDirect,
    bool IsInherited,
    bool IsConditional,
    string AccessSource,
    IReadOnlyList<NormalizedEntitlement> ApplicableEntitlements,
    IReadOnlyList<ProviderEvidence> Evidence,
    IReadOnlyList<AccessEvaluationDiagnostic> Diagnostics)
{
    public IReadOnlyList<AccessExplanationPath> AccessPaths { get; init; } = Array.Empty<AccessExplanationPath>();
}

public sealed record AccessEvaluationResult(
    IReadOnlyList<DomainEffectiveAccess> EffectiveAccess,
    IReadOnlyList<AccessEvaluationDiagnostic> Diagnostics,
    int AllowCount,
    int DenyCount,
    int ConditionalCount,
    int UnknownCount,
    int UnsupportedCount)
{
    public IReadOnlyDictionary<Guid, IReadOnlyList<AccessExplanationPath>> AccessPaths { get; init; } = new Dictionary<Guid, IReadOnlyList<AccessExplanationPath>>();
}
