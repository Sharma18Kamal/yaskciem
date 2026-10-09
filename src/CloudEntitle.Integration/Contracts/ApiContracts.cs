using CloudEntitle.Domain;
using CloudEntitle.IdentityGovernance.Models;
using CloudEntitle.OrganizationManagement.Models;

namespace CloudEntitle.Integration.Contracts;

public static class ApiContractVersion
{
    public const string Current = "v1";
}

public sealed record ApiHealthResponse(string Status, string Version, DateTime UtcTime);
public sealed record ApiReadinessResponse(string Status, bool DatabaseReady, bool ConfigurationReady, string Version);
public sealed record ProviderCapabilityDto(CloudProvider Provider, bool ReadOnly, bool ConnectorAvailable, string AuthenticationMode);
public sealed record ApiIdentityDto(Guid TenantId, Guid UserObjectId);
public sealed record CreateOrganizationRequest(string Name, string TenantKey);
public sealed record InviteOrganizationMemberRequest(string Email, OrganizationRoleOption Role);
public sealed record UpdateOrganizationMemberRoleRequest(OrganizationRoleOption Role);
public sealed record SetOrganizationMemberStatusRequest(bool IsActive);
public sealed record BindOrganizationAccountRequest(CloudProvider Provider, Guid CloudAccountId, string ExternalAccountId, string DisplayName);
public sealed record SaveOrganizationCustomRoleRequest(string Name, IReadOnlyList<OrganizationAction> Permissions);
public sealed record OrganizationWorkspaceDto(
    OrganizationProfile Organization,
    OrganizationMember CurrentMember,
    IReadOnlyList<OrganizationAction> AllowedActions,
    IReadOnlyList<OrganizationMember> Members,
    IReadOnlyList<TenantBinding> Bindings,
    IReadOnlyList<OrganizationCustomRole> CustomRoles,
    IReadOnlyList<OrganizationAuditEntry> AuditTrail);
public sealed record SharedScanAccountDto(CloudProvider Provider, string ExternalAccountId, string DisplayName);
public sealed record SharedScanCoverageDto(string Provider, string DataSource, string Status, int Records, string Details);
public sealed record UploadSharedScanSummaryRequest(
    Guid ScanId,
    DateTime StartedAtUtc,
    DateTime CompletedAtUtc,
    string Provider,
    IReadOnlyList<SharedScanAccountDto> Accounts,
    int Identities,
    int Resources,
    int Roles,
    int Permissions,
    int EffectiveAccess,
    int Findings,
    int CriticalFindings,
    int HighFindings,
    IReadOnlyList<SharedScanCoverageDto> Coverage);
public sealed record SharedScanSummaryDto(
    Guid ScanId,
    Guid OrganizationId,
    Guid TenantId,
    Guid UploadedByObjectId,
    DateTime StartedAtUtc,
    DateTime CompletedAtUtc,
    string Provider,
    IReadOnlyList<SharedScanAccountDto> Accounts,
    int Identities,
    int Resources,
    int Roles,
    int Permissions,
    int EffectiveAccess,
    int Findings,
    int CriticalFindings,
    int HighFindings,
    IReadOnlyList<SharedScanCoverageDto> Coverage);
public sealed record CentralAuditAppendRequest(string EventType, string Description, DateTime? SourceTimestampUtc, IReadOnlyDictionary<string, string> Metadata, Guid EventId = default);
public sealed record CentralAuditEventDto(long Sequence, Guid EventId, Guid TenantId, Guid ActorObjectId, string EventType, string Description, DateTime RecordedAtUtc, DateTime? SourceTimestampUtc, IReadOnlyDictionary<string, string> Metadata, string PreviousHash, string Hash, DateTime RetainUntilUtc);
public sealed record PostureSummaryDto(int CloudAccounts, int Identities, int Resources, int Entitlements, int RiskFindings, int CriticalFindings, DateTime? LastScanAt);
public sealed record ApiError(string Code, string Message, string CorrelationId);
public sealed record ApiEnvelope<T>(T Data, string CorrelationId, string Version);
