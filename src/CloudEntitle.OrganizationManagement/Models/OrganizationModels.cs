using CloudEntitle.Domain;

namespace CloudEntitle.OrganizationManagement.Models;

public enum OrganizationStatus { Active, Suspended, Archived }
public enum OrganizationMemberRole { OrganizationAdmin, SecurityAnalyst, Auditor, Viewer }
public enum OrganizationAction { ViewInventory, RunScan, ViewFindings, ManageMembers, ManageConnections, ManagePolicies, ExportReports }

public sealed record OrganizationProfile(Guid Id, string Name, string TenantKey, OrganizationStatus Status, DateTime CreatedAt);
public sealed record OrganizationMember(Guid Id, Guid OrganizationId, Guid UserId, string Email, OrganizationMemberRole Role, bool IsActive, DateTime AddedAt);
public sealed record TenantBinding(Guid Id, Guid OrganizationId, CloudProvider Provider, Guid CloudAccountId, string ExternalTenantId, string DisplayName, DateTime BoundAt);
public sealed record OrganizationScope(Guid OrganizationId, IReadOnlySet<Guid> CloudAccountIds, IReadOnlySet<CloudProvider> Providers);
public sealed record AuthorizationDecision(bool Allowed, string Reason);
