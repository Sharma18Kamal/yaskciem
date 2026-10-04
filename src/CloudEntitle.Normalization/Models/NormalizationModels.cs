using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;

namespace CloudEntitle.Normalization.Models;

public enum NormalizationStatus { Normalized, PartiallyNormalized, Unsupported, Invalid, Unknown }
public enum NormalizedAccessType { Allow, Deny, Conditional, Unknown }
public enum PermissionCategory { Read, Write, Delete, Administrative, DataRead, DataWrite, Unknown }
public enum ResourcePatternType { Exact, Pattern, Wildcard, Global, Unknown }
public enum NormalizedScopeType { Organization, Tenant, ManagementGroup, Account, Subscription, Folder, Project, ResourceGroup, Resource, Service, Global, Unknown }

public sealed record NormalizedCondition(string? Title, string? Expression, string EvaluationStatus, string? OriginalExpression);

public sealed record ProviderEvidence(
    CloudProvider Provider,
    string SourceType,
    string SourceId,
    string? SourceName,
    string? OriginalDocument,
    string? OriginalStatement,
    string? OriginalScope,
    string? OriginalAssignment,
    DateTime CollectedAt,
    IReadOnlyDictionary<string, string> ProviderSpecificData);

public sealed record NormalizedScope(
    CloudProvider Provider,
    NormalizedScopeType Type,
    string OriginalScope,
    string? SourceScope,
    string? TargetScope,
    bool IsInherited);

public sealed record NormalizedPermission(
    Guid Id,
    CloudProvider Provider,
    Guid CloudAccountId,
    string Service,
    string Action,
    string ResourcePattern,
    AccessType AccessType,
    string OriginalPermission,
    PermissionCategory Category,
    ResourcePatternType ResourcePatternType,
    IReadOnlyDictionary<string, string> ProviderSpecificData,
    ProviderEvidence Evidence,
    bool IsExclusion = false);

public sealed record NormalizedEntitlement(
    Guid Id,
    CloudProvider Provider,
    Guid CloudAccountId,
    Guid PrincipalId,
    Guid? ResourceId,
    Guid? RoleId,
    Guid? PolicyId,
    Guid PermissionId,
    NormalizedScope Scope,
    AssignmentType AssignmentType,
    NormalizedAccessType AccessType,
    bool IsDirect,
    bool IsInherited,
    bool IsConditional,
    NormalizedCondition? Condition,
    NormalizationStatus Status,
    ProviderEvidence Evidence,
    DateTime CreatedAt,
    DateTime? ExpiresAt,
    string? ResourcePattern = null,
    bool IsExclusion = false,
    AccessType? ConditionalEffect = null);

public sealed record ProviderAuthorizationRecord(
    string SourceId,
    string? SourceName,
    Guid CloudAccountId,
    Guid PrincipalId,
    Guid? RoleId,
    Guid? PolicyId,
    Guid PermissionId,
    string Scope,
    string? ResourcePattern,
    AssignmentType AssignmentType,
    AccessType AccessType,
    string SourceType,
    string? OriginalDocument,
    string? OriginalStatement,
    string? ConditionExpression,
    string? ConditionTitle,
    bool IsInherited,
    string? SourceScope,
    string? TargetScope,
    IReadOnlyDictionary<string, string>? ProviderSpecificData = null);

public sealed record ProviderDiscoveryResult(
    CloudProvider Provider,
    IReadOnlyList<CloudAccount> Accounts,
    IReadOnlyList<CloudPrincipal> Principals,
    IReadOnlyList<CloudRole> Roles,
    IReadOnlyList<CloudPolicy> Policies,
    IReadOnlyList<CloudPermission> Permissions,
    IReadOnlyList<ProviderAuthorizationRecord> AuthorizationRecords,
    DateTime CollectedAt);

public sealed record NormalizationWarning(CloudProvider Provider, string SourceId, NormalizationStatus Status, string Reason);

public sealed record NormalizationStatistics(int ItemsProcessed, int ItemsNormalized, int ItemsPartiallyNormalized, int ItemsUnsupported, int ItemsFailed);

public sealed record NormalizationResult(
    IReadOnlyList<CloudPrincipal> Principals,
    IReadOnlyList<CloudRole> Roles,
    IReadOnlyList<CloudPolicy> Policies,
    IReadOnlyList<CloudPermission> Permissions,
    IReadOnlyList<NormalizedEntitlement> Entitlements,
    IReadOnlyList<NormalizationWarning> Warnings,
    IReadOnlyList<string> Errors,
    NormalizationStatistics Statistics);
