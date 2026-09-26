using CloudEntitle.Normalization.Abstractions;
using CloudEntitle.Normalization.Models;

namespace CloudEntitle.Normalization.Validation;

public sealed class NormalizationValidator : INormalizationValidator
{
    public IReadOnlyList<string> Validate(ProviderDiscoveryResult source, NormalizedEntitlement entitlement)
    {
        var errors = new List<string>();
        if (!Enum.IsDefined(source.Provider)) errors.Add("Provider is unknown.");
        if (!source.Accounts.Any(account => account.Id == entitlement.CloudAccountId)) errors.Add("Cloud account reference is missing.");
        if (!source.Principals.Any(principal => principal.Id == entitlement.PrincipalId)) errors.Add("Principal reference is missing.");
        if (!source.Permissions.Any(permission => permission.Id == entitlement.PermissionId)) errors.Add("Permission reference is missing.");
        if (entitlement.RoleId.HasValue && !source.Roles.Any(role => role.Id == entitlement.RoleId)) errors.Add("Role reference is missing.");
        if (entitlement.PolicyId.HasValue && !source.Policies.Any(policy => policy.Id == entitlement.PolicyId)) errors.Add("Policy reference is missing.");
        if (string.IsNullOrWhiteSpace(entitlement.Scope.OriginalScope)) errors.Add("Scope is missing.");
        if (entitlement.Evidence == null) errors.Add("Provider evidence is missing.");
        return errors;
    }
}
