using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;

namespace CloudEntitle.Cloud.Azure.Services;

public sealed record AzureScanResult(
    Scan Scan,
    IReadOnlyList<CloudAccount> Accounts,
    IReadOnlyList<CloudResource> Resources,
    IReadOnlyList<CloudPrincipal> Principals,
    IReadOnlyList<CloudRole> Roles,
    IReadOnlyList<CloudPermission> Permissions,
    IReadOnlyList<RoleAssignment> RoleAssignments,
    IReadOnlyList<Entitlement> Entitlements,
    IReadOnlyList<string> Errors);
