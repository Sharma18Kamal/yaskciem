using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Normalization.Models;
using DomainAccessPath = CloudEntitle.Domain.Entities.AccessPath;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;

namespace CloudEntitle.AccessPath.Models;

public enum PathMode { Shortest, All }

public sealed record AccessPathRequest(
    IReadOnlyList<DomainEffectiveAccess> EffectiveAccess,
    NormalizationResult Normalized,
    IReadOnlyList<CloudResource> Resources,
    IReadOnlyList<GroupMembership> GroupMemberships,
    PathMode Mode = PathMode.All,
    int MaxPathDepth = 20,
    int MaxPathsPerEffectiveAccess = 100,
    int MaxNodesPerPath = 30,
    int MaxTraversalOperations = 10000,
    Guid? EffectiveAccessId = null,
    Guid? PrincipalId = null,
    Guid? ResourceId = null,
    IReadOnlyList<ResourceSecuritySignal>? ResourceSecuritySignals = null,
    IReadOnlyList<ResourceSecurityEvidenceCoverage>? ResourceSecurityCoverage = null,
    DateTime? EvaluatedAt = null);

public sealed record AccessPathDiagnostic(string Code, string Message, Guid? PathId = null);
public sealed record AccessPathStatistics(int TotalPaths, int CompletePaths, int PartialPaths, int UnknownPaths, int UnsupportedPaths, int DirectPaths, int GroupPaths, int InheritedPaths, int ConditionalPaths);
public sealed record AccessPathResult(IReadOnlyList<DomainAccessPath> Paths, DomainAccessPath? PrimaryPath, IReadOnlyList<AccessPathDiagnostic> Diagnostics, AccessPathStatistics Statistics);
