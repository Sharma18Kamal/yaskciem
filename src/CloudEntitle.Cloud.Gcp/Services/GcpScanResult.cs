using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Cloud.Gcp.Mappers;

namespace CloudEntitle.Cloud.Gcp.Services;

public sealed record GcpScanResult(
    Scan Scan,
    IReadOnlyList<CloudAccount> Accounts,
    IReadOnlyList<CloudResource> Resources,
    IReadOnlyList<CloudPrincipal> Principals,
    IReadOnlyList<CloudGroup> Groups,
    IReadOnlyList<CloudRole> Roles,
    IReadOnlyList<CloudPolicy> Policies,
    IReadOnlyList<CloudPermission> Permissions,
    IReadOnlyList<Entitlement> Entitlements,
    IReadOnlyList<GcpBinding> Bindings,
    IReadOnlyList<string> Errors);
