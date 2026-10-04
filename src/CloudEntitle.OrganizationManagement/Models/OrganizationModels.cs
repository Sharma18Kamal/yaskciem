using CloudEntitle.Domain;

namespace CloudEntitle.OrganizationManagement.Models;

public enum OrganizationStatus { Active, Suspended, Archived }
public enum OrganizationMemberRole { OrganizationAdmin, SecurityAnalyst, Auditor, Viewer }
public enum OrganizationAction { ViewInventory, RunScan, ViewFindings, ManageMembers, ManageConnections, ManagePolicies, ExportReports }

public sealed record OrganizationProfile(Guid Id, string Name, string TenantKey, OrganizationStatus Status, DateTime CreatedAt)
{
	public string EntraTenantId { get; init; } = string.Empty;
}
public sealed record OrganizationMember(Guid Id, Guid OrganizationId, Guid UserId, string Email, OrganizationMemberRole Role, bool IsActive, DateTime AddedAt)
{
	public bool IsPending => UserId == Guid.Empty;
}
public sealed record TenantBinding(Guid Id, Guid OrganizationId, CloudProvider Provider, Guid CloudAccountId, string ExternalTenantId, string DisplayName, DateTime BoundAt);
public sealed record OrganizationScope(Guid OrganizationId, IReadOnlySet<Guid> CloudAccountIds, IReadOnlySet<CloudProvider> Providers);
public sealed record AuthorizationDecision(bool Allowed, string Reason);
public sealed record OrganizationIdentity(Guid UserId, string Subject, string TenantId, string Email);
public sealed record OrganizationAuditEntry(DateTime Timestamp, Guid OrganizationId, Guid ActorUserId, string ActorEmail, string Action, string Target, string Details);
public sealed record OrganizationDirectoryState(
	IReadOnlyList<OrganizationProfile> Organizations,
	IReadOnlyList<OrganizationMember> Members,
	IReadOnlyList<TenantBinding> Bindings)
{
	public IReadOnlyList<OrganizationAuditEntry> AuditEntries { get; init; } = Array.Empty<OrganizationAuditEntry>();
}
