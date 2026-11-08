using CloudEntitle.Integration.Contracts;
using CloudEntitle.OrganizationManagement.Models;
using CloudEntitle.OrganizationManagement.Services;

namespace CloudEntitle.LicenseServer.Services;

public static class PrivilegedAccessPolicy
{
    public static IConfigurationSection Section(IConfiguration configuration, Guid tenant, Guid organization)
        => configuration.GetSection($"PrivilegedAccess:Organizations:{tenant:D}:{organization:D}");

    public static PrivilegedAccessTarget Target(IConfiguration configuration, Guid tenant, Guid organization, string alias)
    {
        if (string.IsNullOrEmpty(alias) || alias.Length > 64 || alias.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException("A configured target alias is required.");
        var section = Section(configuration, tenant, organization);
        var target = section.GetSection($"Targets:{alias}");
        if (!Guid.TryParse(section["TenantId"], out var ownerTenant) || ownerTenant != tenant)
            throw new ArgumentException("Configure the owning Entra tenant.");
        if (!Enum.TryParse<Domain.CloudProvider>(target["Provider"] ?? "Azure", true, out var provider))
            throw new ArgumentException("Unknown JIT provider.");
        var subscription = target["SubscriptionId"];
        var scope = target["Scope"];
        var role = target["RoleDefinitionId"];
        var maximum = target.GetValue("MaximumMinutes", 60);
        var emergency = target.GetValue("EmergencyMaximumMinutes", 15);
        if (maximum is < 2 or > 60 || emergency < 1 || emergency > 15 || emergency >= maximum)
            throw new ArgumentException("Standard maximum is <=60 minutes; emergency is shorter and <=15.");
        if (provider != Domain.CloudProvider.Azure)
        {
            var external = target["ExternalTenantId"];
            var auth = target["AuthSecretKey"];
            var revokeAuth = target["RevocationAuthSecretKey"] ?? auth;
            if (string.IsNullOrWhiteSpace(external) || external.Length > 256 || string.IsNullOrWhiteSpace(auth)
                || string.IsNullOrWhiteSpace(configuration[auth]) || string.IsNullOrWhiteSpace(configuration[revokeAuth!]))
                throw new ArgumentException("Client-owned native authentication and external account binding are required.");
            if (provider == Domain.CloudProvider.Aws)
            {
                if (external.Length != 12 || !external.All(char.IsAsciiDigit) || !SafeBroker(target["BrokerEndpoint"])
                    || string.IsNullOrWhiteSpace(role) || role.Length > 2048 || role.Any(char.IsControl))
                    throw new ArgumentException("AWS requires a client-owned HTTPS bounded-human-session broker, account and configured role.");
                return new(alias, external, external, role, maximum, emergency,
                    section.GetValue<bool>("Enabled") && target.GetValue<bool>("Connected"), provider,
                    target["BrokerEndpoint"]!.TrimEnd('/'), auth, revokeAuth);
            }
            if (provider != Domain.CloudProvider.Gcp || scope is null || !IsEntitlement(scope)
                || scope.Split('/')[1] != external || string.IsNullOrWhiteSpace(role))
                throw new ArgumentException("GCP requires an exact native PAM entitlement and configured role.");
            return new(alias, external, scope, role, maximum, emergency,
                section.GetValue<bool>("Enabled") && target.GetValue<bool>("Connected"), provider, null, auth, revokeAuth);
        }
        if (!Guid.TryParse(subscription, out var subscriptionId) || subscriptionId == Guid.Empty
            || !Guid.TryParse(section["TenantId"], out var configuredTenant) || configuredTenant != tenant
            || scope is null || !(scope.Equals($"/subscriptions/{subscriptionId:D}", StringComparison.OrdinalIgnoreCase)
                || scope.StartsWith($"/subscriptions/{subscriptionId:D}/", StringComparison.OrdinalIgnoreCase))
            || scope.Length > 2048 || scope.Any(c => char.IsControl(c) || c is '?' or '#' or '\\' or '%')
            || scope.Split('/').Any(part => part is "." or "..")
            || role is null || !role.StartsWith($"/subscriptions/{subscriptionId:D}/providers/Microsoft.Authorization/roleDefinitions/", StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParse(role.Split('/').Last(), out var roleId) || roleId == Guid.Empty
            || role.Split('/').Length != 7 || maximum is < 1 or > 60 || emergency < 1 || emergency > 15 || emergency >= maximum)
            throw new ArgumentException("Configure a same-tenant Azure subscription/scope/role target with standard duration <=60 minutes and a shorter emergency duration <=15 minutes.");
        return new(alias, subscriptionId.ToString("D"), scope.TrimEnd('/'), role, maximum, emergency,
            section.GetValue<bool>("Enabled") && target.GetValue<bool>("Connected"));
    }

    internal static bool SafeBroker(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment) && !uri.IsLoopback;

    internal static bool IsEntitlement(string value)
    {
        var parts = value.Split('/');
        return parts.Length == 6 && parts[0] is ("projects" or "folders" or "organizations")
            && parts[2] == "locations" && parts[4] == "entitlements"
            && parts.All(part => part.Length > 0 && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
    }

    public static (string Principal, string SecretKey) Human(IConfiguration configuration, Guid tenant, Guid organization,
        PrivilegedAccessTarget target, Guid requester)
    {
        if (target.Provider != Domain.CloudProvider.Gcp) return (requester.ToString("D"), target.AuthSecretKey ?? "");
        var mapping = Section(configuration, tenant, organization).GetSection($"HumanPrincipals:{requester:D}");
        var principal = mapping["Principal"];
        var key = mapping["AccessTokenSecretKey"];
        if (principal is null || !principal.StartsWith("user:", StringComparison.Ordinal) || principal.Length > 320
            || !principal[5..].Contains('@') || principal.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(configuration[key]))
            throw new ArgumentException("Map the requester Entra oid to an explicit GCP human user and client-owned native user OAuth token secret.");
        return (principal, key);
    }

    public static PrivilegedAccessRequest Revoke(PrivilegedAccessRequest current, Guid actor, PrivilegedAccessDecision decision, bool approver)
    {
        ValidateReason(decision.Reason);
        if (current.Version != decision.ExpectedVersion) throw new InvalidOperationException("Refresh the changed request.");
        if (!decision.LiveConsent) throw new InvalidOperationException("Explicit live revocation/cancellation consent is required.");
        if (actor != current.RequestedBy && !approver) throw new UnauthorizedAccessException();
        if (current.Status is "Revoked" or "Cancelled" or "Rejected" or "Failed" || current.RevokeId is not null)
            throw new InvalidOperationException("The request is terminal or revocation is already queued.");
        return current with { Status = current.StartsAtUtc is null && current.Status is ("PendingApproval" or "Approved" or "Queued")
                ? "Cancelled" : "RevokeQueued",
            RevokeId = Guid.NewGuid(), RevokeReason = decision.Reason.Trim(), Version = current.Version + 1 };
    }

    public static bool IsApprover(IConfiguration configuration, Guid tenant, Guid organization, Guid user)
        => Approvers(configuration, tenant, organization).Contains(user);

    public static IReadOnlyList<Guid> Approvers(IConfiguration configuration, Guid tenant, Guid organization)
    {
        var values = Section(configuration, tenant, organization).GetSection("ApproverIds").GetChildren().Select(child => child.Value).ToArray();
        if (values.Length != 2 || values.Any(value => !Guid.TryParse(value, out var id) || id == Guid.Empty))
            throw new ArgumentException("Configure two distinct manager/security approver object IDs.");
        var ids = values.Select(value => Guid.Parse(value!)).ToArray();
        if (ids.Distinct().Count() != 2) throw new ArgumentException("Manager/security approvers must be distinct.");
        return ids;
    }

    public static bool Authorized(OrganizationManagementService service, Guid organization, OrganizationIdentity actor, bool approve)
        => service.Authorize(organization, actor, OrganizationAction.ViewInventory).Allowed
            && service.Authorize(organization, actor, OrganizationAction.ViewFindings).Allowed
            && (!approve || service.Authorize(organization, actor, OrganizationAction.ManageRemediations).Allowed);

    public static void ValidateReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 2000 || reason.Any(char.IsControl))
            throw new ArgumentException("A single-line business justification of 1-2,000 characters is required.");
    }
}
