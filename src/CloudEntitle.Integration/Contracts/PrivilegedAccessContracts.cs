namespace CloudEntitle.Integration.Contracts;

/// <summary>A caller-owned JIT request; provider, human identity and privilege are resolved from a server allowlist.</summary>
public sealed record CreatePrivilegedAccessRequest(string Target, int DurationMinutes, bool Emergency, string Reason);
/// <summary>An optimistic-concurrency decision or explicitly consented activation.</summary>
public sealed record PrivilegedAccessDecision(long ExpectedVersion, string Reason, bool LiveConsent = false);
/// <summary>Durable request state. Submitted/unknown/expiry-pending states are not verified cloud success.</summary>
public sealed record PrivilegedAccessRequest(Guid RequestId, Guid RequestedBy, string Target, string SubscriptionId,
    string Scope, string RoleDefinitionId, int DurationMinutes, bool Emergency, string Reason,
    DateTimeOffset RequestedAtUtc, DateTimeOffset ApprovalDeadlineUtc, string Status, long Version,
    IReadOnlyList<Guid> ApprovedBy, DateTimeOffset? StartsAtUtc = null, DateTimeOffset? ExpiresAtUtc = null,
    string? ScheduleId = null, string? FailureCode = null,
    CloudEntitle.Domain.CloudProvider Provider = CloudEntitle.Domain.CloudProvider.Azure,
    string? HumanPrincipal = null, string? BrokerEndpoint = null, string? AuthSecretKey = null,
    string? RevocationAuthSecretKey = null, Guid? RevokeId = null, string? RevokeReason = null);
/// <summary>An append-only governance event, not a WORM or provider-signed record.</summary>
public sealed record PrivilegedAccessAudit(Guid EventId, Guid RequestId, Guid ActorId, string Action, string Reason, DateTimeOffset AtUtc);
/// <summary>A configured eligible target. Configuration alone does not connect or grant access.</summary>
public sealed record PrivilegedAccessTarget(string Alias, string SubscriptionId, string Scope, string RoleDefinitionId,
    int MaximumMinutes, int EmergencyMaximumMinutes, bool Connected,
    CloudEntitle.Domain.CloudProvider Provider = CloudEntitle.Domain.CloudProvider.Azure,
    string? BrokerEndpoint = null, string? AuthSecretKey = null, string? RevocationAuthSecretKey = null);
