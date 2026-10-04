using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Normalization.Models;
using CloudEntitle.RiskEngine.Models;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;
using DomainAccessPath = CloudEntitle.Domain.Entities.AccessPath;

namespace CloudEntitle.Recommendations.Models;

public sealed record RecommendationContext(
    IReadOnlyList<RiskFindingModel> Findings,
    IReadOnlyList<DomainEffectiveAccess> EffectiveAccess,
    IReadOnlyList<DomainAccessPath> AccessPaths,
    IReadOnlyList<NormalizedEntitlement> Entitlements,
    IReadOnlyList<CloudPrincipal> Principals,
    IReadOnlyList<CloudResource> Resources,
    IReadOnlyList<CloudPermission> Permissions,
    IReadOnlyList<ActivityRecord> ActivityRecords,
    DateTime EvaluatedAt,
    IReadOnlyList<ActivityEvidenceCoverage>? ActivityCoverage = null,
    int ActivityObservationDays = 30);

public sealed record RecommendationEvidence(
    Guid RiskFindingId,
    Guid? EffectiveAccessId,
    Guid? AccessPathId,
    Guid? EntitlementId,
    CloudProvider Provider,
    string SourceType,
    string SourceId,
    string? SourceName,
    string Principal,
    string Resource,
    string Permission,
    string Scope,
    string CurrentAssignmentType,
    string ObservedEvidence,
    string ActivityEvidence,
    string AuthorizationEvidence,
    string? OriginalProviderObject);

public sealed record RecommendationImpact(
    RecommendationImpactState State,
    IReadOnlyList<Guid> AffectedPrincipals,
    IReadOnlyList<Guid> AffectedResources,
    IReadOnlyList<Guid> AffectedPermissions,
    IReadOnlyList<Guid> PermissionsRemoved,
    IReadOnlyList<Guid> PermissionsRetained,
    IReadOnlyList<Guid> AccessPathsRemoved,
    IReadOnlyList<Guid> AccessPathsRetained,
    bool PotentialAccessLoss);

public sealed record ProviderChangeProposal(CloudProvider Provider, string ProposalType, string CurrentState, string ProposedState, bool Executable, string Reason);
public sealed record RecommendationDiagnostic(string Code, string Message, string? RuleId = null, Guid? FindingId = null);
public sealed record RecommendationStatistics(int TotalRecommendations, int Confirmed, int Potential, int Unknown, int Unsupported, int DeduplicatedCount);
public sealed record RecommendationResult(IReadOnlyList<RecommendationModel> Recommendations, IReadOnlyList<RecommendationDiagnostic> Diagnostics, RecommendationStatistics Statistics);
