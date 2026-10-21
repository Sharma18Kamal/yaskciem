using CloudEntitle.Domain;
using CloudEntitle.IdentityGovernance.Models;
using CloudEntitle.OrganizationManagement.Models;

namespace CloudEntitle.Integration.Contracts;

public static class ApiContractVersion
{
    public const string Current = "v1";
}

public sealed record ApiHealthResponse(string Status, string Version, DateTime UtcTime);
public sealed record ApiReadinessResponse(string Status, bool DatabaseReady, bool ConfigurationReady, string Version)
{
    public bool AuditStorageReady { get; init; }
    public bool ProductionReady { get; init; }
    public string AuditStorageDetails { get; init; } = string.Empty;
    public bool AuditArchiveReady { get; init; }
    public string AuditArchiveDetails { get; init; } = string.Empty;
    public long AuditArchivePendingEvents { get; init; }
    public DateTime? AuditArchiveOldestPendingAtUtc { get; init; }
    public DateTime? AuditArchiveLastSuccessAtUtc { get; init; }
}
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
public sealed record SharedMonitoringScopeDto(CloudProvider Provider, string ExternalAccountId, string Kind, string Profile);
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
    IReadOnlyList<SharedScanCoverageDto> Coverage)
{
    public IReadOnlyList<SharedCiemRecordDto> Records { get; init; } = Array.Empty<SharedCiemRecordDto>();
    public IReadOnlyList<SharedMonitoringScopeDto> MonitoringScopes { get; init; } = Array.Empty<SharedMonitoringScopeDto>();
}
public sealed record SharedCiemRecordDto(CloudProvider Provider, string Kind, string RecordId, string ExternalAccountId, string PayloadJson);
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
    IReadOnlyList<SharedScanCoverageDto> Coverage)
{
    public IReadOnlyList<SharedMonitoringScopeDto> MonitoringScopes { get; init; } = Array.Empty<SharedMonitoringScopeDto>();
}
public enum SharedAlertSeverity { Informational, Low, Medium, High, Critical }
public sealed record SharedNotificationRuleDto(
    Guid RuleId, string Name, bool Enabled, SharedAlertSeverity MinimumSeverity,
    bool Findings, bool EffectiveAccess, bool Added, bool Removed, bool Changed, string Channel)
{
    public string ConnectorKind { get; init; } = "SignedWebhook";
    public bool AllowAutomaticTickets { get; init; }
    public IReadOnlyList<string> EventTypes { get; init; } = Array.Empty<string>();
}
public sealed record SharedDriftEventDto(
    Guid EventId, Guid ScanId, Guid PreviousScanId, CloudProvider Provider, string ExternalAccountId,
    string Kind, string Change, string EntityKey, SharedAlertSeverity Severity,
    string? PreviousStateHash, string? CurrentStateHash, DateTime DetectedAtUtc)
{
    public string EventType { get; init; } = OperationalEventTypes.ForDrift(Kind, Change);
    public Guid? SubjectId { get; init; }
}
public sealed record SharedDeliveryDto(
    Guid DeliveryId, Guid EventId, Guid RuleId, string Channel, string Status, int AttemptCount,
    DateTime CreatedAtUtc, DateTime? NextAttemptAtUtc, DateTime? CompletedAtUtc, string? FailureCode)
{
    public string ConnectorKind { get; init; } = "SignedWebhook";
    public Guid? RequestedBy { get; init; }
    public bool DeadLettered => Status == "Failed";
}
public sealed record SharedDeliveryAttemptDto(
    Guid AttemptId, int AttemptNumber, DateTime StartedAtUtc, DateTime? CompletedAtUtc,
    int? HttpStatus, string? FailureCode)
{
    public double? LatencyMilliseconds => CompletedAtUtc.HasValue ? Math.Max(0, (CompletedAtUtc.Value - StartedAtUtc).TotalMilliseconds) : null;
}
public sealed record SharedDeliveryStatusDto(SharedDeliveryDto Delivery, IReadOnlyList<SharedDeliveryAttemptDto> Attempts);
public sealed record CreateScanScheduleRequest(CloudProvider Provider, string ExternalAccountId, string DisplayName, int IntervalMinutes, int MaxRetries = 2);
public sealed record UpdateScanScheduleRequest(bool Enabled, int IntervalMinutes, int MaxRetries);
public sealed record ScanScheduleDto(
    Guid ScheduleId,
    Guid TenantId,
    Guid OrganizationId,
    CloudProvider Provider,
    string ExternalAccountId,
    string DisplayName,
    int IntervalMinutes,
    int MaxRetries,
    bool Enabled,
    DateTime NextRunAtUtc,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? LastSuccessfulScanAtUtc);
public sealed record ScanJobDto(
    Guid JobId,
    Guid ScheduleId,
    Guid TenantId,
    Guid OrganizationId,
    CloudProvider Provider,
    string ExternalAccountId,
    string DisplayName,
    string Status,
    int AttemptCount,
    int MaxRetries,
    DateTime QueuedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    DateTime? NextAttemptAtUtc,
    long? DurationMilliseconds,
    Guid? ScanId,
    bool CancellationRequested,
    string? LastError);
public sealed record ScanJobAttemptDto(
    Guid AttemptId,
    Guid JobId,
    int AttemptNumber,
    DateTime StartedAtUtc,
    DateTime? CompletedAtUtc,
    long? DurationMilliseconds,
    string Outcome,
    string? Error);
public sealed record ScanJobCheckpointDto(
    Guid JobId,
    string DataSource,
    string Status,
    int Records,
    DateTime UpdatedAtUtc,
    string? Details);
public sealed record ScanJobStatusDto(
    ScanJobDto Job,
    IReadOnlyList<ScanJobAttemptDto> Attempts,
    IReadOnlyList<ScanJobCheckpointDto> Checkpoints);
public sealed record CentralAuditAppendRequest(string EventType, string Description, DateTime? SourceTimestampUtc, IReadOnlyDictionary<string, string> Metadata, Guid EventId = default);
public sealed record CentralAuditEventDto(long Sequence, Guid EventId, Guid TenantId, Guid ActorObjectId, string EventType, string Description, DateTime RecordedAtUtc, DateTime? SourceTimestampUtc, IReadOnlyDictionary<string, string> Metadata, string PreviousHash, string Hash, DateTime RetainUntilUtc)
{
    public string IntegrityKeyId { get; init; } = "legacy";
}
public sealed record PostureSummaryDto(int CloudAccounts, int Identities, int Resources, int Entitlements, int RiskFindings, int CriticalFindings, DateTime? LastScanAt);
public sealed record ApiError(string Code, string Message, string CorrelationId);
public sealed record ApiEnvelope<T>(T Data, string CorrelationId, string Version);
