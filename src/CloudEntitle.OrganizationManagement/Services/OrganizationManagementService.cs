using CloudEntitle.Domain;
using CloudEntitle.OrganizationManagement.Models;

namespace CloudEntitle.OrganizationManagement.Services;

public sealed class OrganizationManagementService
{
    private readonly Dictionary<Guid, OrganizationProfile> _organizations = new();
    private readonly Dictionary<Guid, OrganizationMember> _members = new();
    private readonly Dictionary<Guid, TenantBinding> _bindings = new();

    public OrganizationProfile CreateOrganization(string name, string tenantKey)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Organization name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(tenantKey)) throw new ArgumentException("Tenant key is required.", nameof(tenantKey));
        var organization = new OrganizationProfile(Guid.NewGuid(), name.Trim(), tenantKey.Trim(), OrganizationStatus.Active, DateTime.UtcNow);
        _organizations.Add(organization.Id, organization);
        return organization;
    }

    public OrganizationMember AddMember(Guid organizationId, Guid userId, string email, OrganizationMemberRole role)
    {
        RequireOrganization(organizationId);
        if (string.IsNullOrWhiteSpace(email)) throw new ArgumentException("Member email is required.", nameof(email));
        var member = new OrganizationMember(Guid.NewGuid(), organizationId, userId, email.Trim(), role, true, DateTime.UtcNow);
        _members.Add(member.Id, member);
        return member;
    }

    public TenantBinding BindCloudAccount(Guid organizationId, CloudProvider provider, Guid cloudAccountId, string externalTenantId, string displayName)
    {
        RequireOrganization(organizationId);
        if (cloudAccountId == Guid.Empty) throw new ArgumentException("Cloud account ID is required.", nameof(cloudAccountId));
        if (string.IsNullOrWhiteSpace(externalTenantId)) throw new ArgumentException("External tenant ID is required.", nameof(externalTenantId));
        var binding = new TenantBinding(Guid.NewGuid(), organizationId, provider, cloudAccountId, externalTenantId.Trim(), displayName.Trim(), DateTime.UtcNow);
        _bindings.Add(binding.Id, binding);
        return binding;
    }

    public OrganizationScope GetScope(Guid organizationId)
    {
        RequireOrganization(organizationId);
        var bindings = _bindings.Values.Where(binding => binding.OrganizationId == organizationId).ToArray();
        return new OrganizationScope(organizationId, bindings.Select(binding => binding.CloudAccountId).ToHashSet(), bindings.Select(binding => binding.Provider).ToHashSet());
    }

    public AuthorizationDecision Authorize(Guid organizationId, Guid userId, OrganizationAction action)
    {
        var organization = RequireOrganization(organizationId);
        var member = _members.Values.FirstOrDefault(item => item.OrganizationId == organizationId && item.UserId == userId && item.IsActive);
        if (member == null) return new(false, "User is not an active organization member.");
        if (organization.Status != OrganizationStatus.Active) return new(false, $"Organization is {organization.Status}.");
        var allowed = action switch
        {
            OrganizationAction.ViewInventory or OrganizationAction.ViewFindings => true,
            OrganizationAction.RunScan or OrganizationAction.ExportReports => member.Role is not OrganizationMemberRole.Viewer,
            OrganizationAction.ManageMembers or OrganizationAction.ManageConnections or OrganizationAction.ManagePolicies => member.Role == OrganizationMemberRole.OrganizationAdmin,
            _ => false
        };
        return new(allowed, allowed ? "Organization role permits this action." : "Organization role does not permit this action.");
    }

    public IReadOnlyList<OrganizationMember> GetMembers(Guid organizationId) => _members.Values.Where(member => member.OrganizationId == organizationId).ToArray();
    public IReadOnlyList<TenantBinding> GetTenantBindings(Guid organizationId) => _bindings.Values.Where(binding => binding.OrganizationId == organizationId).ToArray();

    private OrganizationProfile RequireOrganization(Guid id) => _organizations.TryGetValue(id, out var organization) ? organization : throw new KeyNotFoundException($"Organization {id} was not found.");
}
