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
    DateTime? EvaluationTime = null);

public sealed record AccessEvaluationContext(
    CloudProvider Provider,
    Guid CloudAccountId,
    Guid PrincipalId,
    Guid ResourceId,
    Guid PermissionId,
    DateTime EvaluationTime,
    IReadOnlyDictionary<string, string> RuntimeConditionContext);

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
    IReadOnlyList<AccessEvaluationDiagnostic> Diagnostics);

public sealed record AccessEvaluationResult(
    IReadOnlyList<DomainEffectiveAccess> EffectiveAccess,
    IReadOnlyList<AccessEvaluationDiagnostic> Diagnostics,
    int AllowCount,
    int DenyCount,
    int ConditionalCount,
    int UnknownCount,
    int UnsupportedCount);
