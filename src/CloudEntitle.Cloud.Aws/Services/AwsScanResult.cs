using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;

namespace CloudEntitle.Cloud.Aws.Services;

public sealed record AwsTrustRelationship(Guid PrincipalId, Guid RoleId, string PrincipalExternalId, string Source);

public sealed record AwsScanResult(
    Scan Scan,
    IReadOnlyList<CloudAccount> Accounts,
    IReadOnlyList<CloudPrincipal> Principals,
    IReadOnlyList<CloudGroup> Groups,
    IReadOnlyList<CloudRole> Roles,
    IReadOnlyList<CloudPolicy> Policies,
    IReadOnlyList<CloudPermission> Permissions,
    IReadOnlyList<Entitlement> Entitlements,
    IReadOnlyList<AwsTrustRelationship> TrustRelationships,
    IReadOnlyList<string> Errors);
