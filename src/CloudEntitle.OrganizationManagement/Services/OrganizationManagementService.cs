using CloudEntitle.Domain;
using CloudEntitle.OrganizationManagement.Abstractions;
using CloudEntitle.OrganizationManagement.Models;

namespace CloudEntitle.OrganizationManagement.Services;

public sealed class OrganizationManagementService
{
    private readonly Dictionary<Guid, OrganizationProfile> _organizations = new();
    private readonly Dictionary<Guid, OrganizationMember> _members = new();
    private readonly Dictionary<Guid, TenantBinding> _bindings = new();
    private readonly Dictionary<Guid, OrganizationCustomRole> _customRoles = new();
    private readonly List<OrganizationAuditEntry> _auditEntries = new();
    private readonly IOrganizationStore? _store;

    public OrganizationManagementService(IOrganizationStore? store = null)
    {
        _store = store;
        if (store is null) return;
        var state = store.Load();
        foreach (var organization in state.Organizations) _organizations[organization.Id] = organization;
        foreach (var member in state.Members) _members[member.Id] = member;
        foreach (var binding in state.Bindings) _bindings[binding.Id] = binding;
        foreach (var role in state.CustomRoles) _customRoles[role.Id] = role;
        _auditEntries.AddRange(state.AuditEntries);
    }

    public OrganizationProfile CreateOrganization(string name, string tenantKey, string entraTenantId, OrganizationIdentity creator)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Organization name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(tenantKey)) throw new ArgumentException("Tenant key is required.", nameof(tenantKey));
        if (_organizations.Values.Any(item => string.Equals(item.TenantKey, tenantKey.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("An organization with this tenant key already exists.");
        if (!Guid.TryParse(entraTenantId, out var parsedTenantId) || parsedTenantId == Guid.Empty) throw new ArgumentException("A valid Entra tenant ID is required.", nameof(entraTenantId));
        ValidateIdentity(creator);
        if (!string.Equals(parsedTenantId.ToString(), creator.TenantId, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The signed-in identity does not belong to the organization's Entra tenant.");
        var organization = new OrganizationProfile(Guid.NewGuid(), name.Trim(), tenantKey.Trim(), OrganizationStatus.Active, DateTime.UtcNow)
        {
            EntraTenantId = parsedTenantId.ToString()
        };
        _organizations.Add(organization.Id, organization);
        var owner = new OrganizationMember(Guid.NewGuid(), organization.Id, creator.UserId, creator.Email.Trim(), OrganizationMemberRole.OrganizationAdmin, true, DateTime.UtcNow);
        _members.Add(owner.Id, owner);
        RecordAudit(organization.Id, creator, "OrganizationCreated", organization.Id.ToString(), "Creator assigned OrganizationAdmin.");
        Persist();
        return organization;
    }

    public OrganizationMember AddMember(Guid organizationId, OrganizationIdentity actor, string email, OrganizationMemberRole role)
        => AddMember(organizationId, actor, email, new OrganizationRoleOption(role.ToString(), role, null));

    public OrganizationMember AddMember(Guid organizationId, OrganizationIdentity actor, string email, OrganizationRoleOption role)
    {
        EnsureAuthorized(organizationId, actor, OrganizationAction.ManageMembers);
        if (string.IsNullOrWhiteSpace(email)) throw new ArgumentException("Member email is required.", nameof(email));
        var assignedRole = ResolveRole(organizationId, role);
        var normalizedEmail = email.Trim();
        if (_members.Values.Any(item => item.OrganizationId == organizationId && string.Equals(item.Email, normalizedEmail, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("This email is already a member of the organization.");
        var member = new OrganizationMember(Guid.NewGuid(), organizationId, Guid.Empty, normalizedEmail, assignedRole.BuiltInRole ?? OrganizationMemberRole.Viewer, true, DateTime.UtcNow)
        {
            CustomRoleId = assignedRole.CustomRoleId,
            CustomRoleName = assignedRole.CustomRoleId is null ? null : assignedRole.Name
        };
        _members.Add(member.Id, member);
        RecordAudit(organizationId, actor, "MemberAdded", normalizedEmail, $"Role: {member.AssignedRoleName}.");
        Persist();
        return member;
    }

    public OrganizationMember? AcceptInvitation(Guid organizationId, OrganizationIdentity identity)
    {
        ValidateIdentity(identity);
        var organization = RequireOrganization(organizationId);
        if (organization.Status != OrganizationStatus.Active || !IsTenantMatch(organization, identity)) return null;
        var invitation = _members.Values.FirstOrDefault(item => item.OrganizationId == organizationId && item.IsActive
            && item.IsPending && string.Equals(item.Email, identity.Email, StringComparison.OrdinalIgnoreCase));
        if (invitation is null) return null;
        var accepted = invitation with { UserId = identity.UserId };
        _members[invitation.Id] = accepted;
        RecordAudit(organizationId, identity, "InvitationAccepted", accepted.Email, "Membership linked to the authenticated Entra object ID.");
        Persist();
        return accepted;
    }

    public TenantBinding BindCloudAccount(Guid organizationId, OrganizationIdentity actor, CloudProvider provider, Guid cloudAccountId, string externalTenantId, string displayName)
    {
        EnsureAuthorized(organizationId, actor, OrganizationAction.ManageConnections);
        if (cloudAccountId == Guid.Empty) throw new ArgumentException("Cloud account ID is required.", nameof(cloudAccountId));
        if (string.IsNullOrWhiteSpace(externalTenantId)) throw new ArgumentException("External tenant ID is required.", nameof(externalTenantId));
        var normalizedExternalId = externalTenantId.Trim();
        if (_bindings.Values.Any(item => item.Provider == provider && string.Equals(item.ExternalTenantId, normalizedExternalId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("This cloud account is already bound to an organization.");
        var binding = new TenantBinding(Guid.NewGuid(), organizationId, provider, cloudAccountId, normalizedExternalId, displayName.Trim(), DateTime.UtcNow);
        _bindings.Add(binding.Id, binding);
        RecordAudit(organizationId, actor, "AccountBound", cloudAccountId.ToString(), $"{provider}: {displayName.Trim()} ({externalTenantId.Trim()}).");
        Persist();
        return binding;
    }

    public OrganizationScope GetScope(Guid organizationId, OrganizationIdentity identity)
    {
        EnsureAuthorized(organizationId, identity, OrganizationAction.ViewInventory);
        var bindings = _bindings.Values.Where(binding => binding.OrganizationId == organizationId).ToArray();
        return new OrganizationScope(organizationId, bindings.Select(binding => binding.CloudAccountId).ToHashSet(), bindings.Select(binding => binding.Provider).ToHashSet());
    }

    public AuthorizationDecision Authorize(Guid organizationId, OrganizationIdentity identity, OrganizationAction action)
    {
        var organization = RequireOrganization(organizationId);
        if (identity is null || identity.UserId == Guid.Empty || string.IsNullOrWhiteSpace(identity.Subject)
            || string.IsNullOrWhiteSpace(identity.Email) || string.IsNullOrWhiteSpace(identity.TenantId))
            return new(false, "A validated Entra identity is required.");
        if (!IsTenantMatch(organization, identity)) return new(false, "Signed-in identity does not belong to this organization's Entra tenant.");
        var member = _members.Values.FirstOrDefault(item => item.OrganizationId == organizationId && item.IsActive
                && item.UserId == identity.UserId);
        if (member == null) return new(false, "User is not an active organization member.");
        if (organization.Status != OrganizationStatus.Active) return new(false, $"Organization is {organization.Status}.");
        var allowed = member.CustomRoleId is Guid customRoleId
            ? _customRoles.TryGetValue(customRoleId, out var customRole)
                && customRole.OrganizationId == organizationId
                && customRole.Permissions.Contains(action)
            : BuiltInRoleAllows(member.Role, action);
        return new(allowed, allowed ? "Organization role permits this action." : "Organization role does not permit this action.");
    }

    public IReadOnlyList<OrganizationProfile> GetOrganizations() => _organizations.Values.OrderBy(item => item.Name).ToArray();

    public OrganizationMemberRole? GetCurrentRole(Guid organizationId, OrganizationIdentity identity)
    {
        var organization = RequireOrganization(organizationId);
        if (identity is null || identity.UserId == Guid.Empty || string.IsNullOrWhiteSpace(identity.TenantId)
            || string.IsNullOrWhiteSpace(identity.Email) || !IsTenantMatch(organization, identity)
            || organization.Status != OrganizationStatus.Active)
            return null;
        return _members.Values.FirstOrDefault(item => item.OrganizationId == organizationId && item.IsActive
                && item.UserId == identity.UserId)?.Role;
    }

    public IReadOnlyList<OrganizationMember> GetMembers(Guid organizationId, OrganizationIdentity identity)
    {
        EnsureAuthorized(organizationId, identity, OrganizationAction.ManageMembers);
        return _members.Values.Where(member => member.OrganizationId == organizationId).ToArray();
    }

    public IReadOnlyList<OrganizationRoleOption> GetRoleOptions(Guid organizationId, OrganizationIdentity identity)
    {
        EnsureAuthorized(organizationId, identity, OrganizationAction.ManageMembers);
        var builtInRoles = Enum.GetValues<OrganizationMemberRole>()
            .Select(role => new OrganizationRoleOption(role.ToString(), role, null));
        var customRoles = _customRoles.Values.Where(role => role.OrganizationId == organizationId)
            .OrderBy(role => role.Name, StringComparer.OrdinalIgnoreCase)
            .Select(role => new OrganizationRoleOption(role.Name, null, role.Id));
        return builtInRoles.Concat(customRoles).ToArray();
    }

    public IReadOnlyList<OrganizationCustomRole> GetCustomRoles(Guid organizationId, OrganizationIdentity identity)
    {
        EnsureAuthorized(organizationId, identity, OrganizationAction.ManageMembers);
        return _customRoles.Values.Where(role => role.OrganizationId == organizationId)
            .OrderBy(role => role.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public OrganizationCustomRole CreateCustomRole(Guid organizationId, OrganizationIdentity actor, string name, IEnumerable<OrganizationAction> permissions)
    {
        EnsureAuthorized(organizationId, actor, OrganizationAction.ManageMembers);
        var normalizedName = ValidateCustomRoleName(organizationId, name, null);
        var normalizedPermissions = NormalizePermissions(permissions);
        var now = DateTime.UtcNow;
        var role = new OrganizationCustomRole(Guid.NewGuid(), organizationId, normalizedName, normalizedPermissions, now, now);
        _customRoles.Add(role.Id, role);
        RecordAudit(organizationId, actor, "CustomRoleCreated", role.Name, $"Permissions: {string.Join(", ", role.Permissions)}.");
        Persist();
        return role;
    }

    public OrganizationCustomRole UpdateCustomRole(Guid organizationId, OrganizationIdentity actor, Guid roleId, string name, IEnumerable<OrganizationAction> permissions)
    {
        EnsureAuthorized(organizationId, actor, OrganizationAction.ManageMembers);
        if (!_customRoles.TryGetValue(roleId, out var role) || role.OrganizationId != organizationId)
            throw new KeyNotFoundException("Custom organization role was not found.");
        var normalizedName = ValidateCustomRoleName(organizationId, name, roleId);
        var normalizedPermissions = NormalizePermissions(permissions);
        var updated = role with { Name = normalizedName, Permissions = normalizedPermissions, UpdatedAt = DateTime.UtcNow };
        _customRoles[roleId] = updated;
        foreach (var member in _members.Values.Where(item => item.OrganizationId == organizationId && item.CustomRoleId == roleId).ToArray())
            _members[member.Id] = member with { CustomRoleName = normalizedName };
        RecordAudit(organizationId, actor, "CustomRoleUpdated", normalizedName, $"Permissions: {string.Join(", ", normalizedPermissions)}.");
        Persist();
        return updated;
    }

    public void DeleteCustomRole(Guid organizationId, OrganizationIdentity actor, Guid roleId)
    {
        EnsureAuthorized(organizationId, actor, OrganizationAction.ManageMembers);
        if (!_customRoles.TryGetValue(roleId, out var role) || role.OrganizationId != organizationId)
            throw new KeyNotFoundException("Custom organization role was not found.");
        if (_members.Values.Any(member => member.OrganizationId == organizationId && member.CustomRoleId == roleId))
            throw new InvalidOperationException("A custom role assigned to members cannot be deleted.");
        _customRoles.Remove(roleId);
        RecordAudit(organizationId, actor, "CustomRoleDeleted", role.Name, "Custom role deleted.");
        Persist();
    }

    public OrganizationMember UpdateMemberRole(Guid organizationId, OrganizationIdentity actor, Guid memberId, OrganizationMemberRole role)
        => UpdateMemberRole(organizationId, actor, memberId, new OrganizationRoleOption(role.ToString(), role, null));

    public OrganizationMember UpdateMemberRole(Guid organizationId, OrganizationIdentity actor, Guid memberId, OrganizationRoleOption role)
    {
        EnsureAuthorized(organizationId, actor, OrganizationAction.ManageMembers);
        if (!_members.TryGetValue(memberId, out var member) || member.OrganizationId != organizationId)
            throw new KeyNotFoundException("Organization member was not found.");
        var assignedRole = ResolveRole(organizationId, role);
        if (member.CustomRoleId is null && member.Role == OrganizationMemberRole.OrganizationAdmin
            && assignedRole.BuiltInRole != OrganizationMemberRole.OrganizationAdmin
            && _members.Values.Count(item => item.OrganizationId == organizationId && item.IsActive && item.Role == OrganizationMemberRole.OrganizationAdmin) <= 1)
            throw new InvalidOperationException("An organization must retain at least one active administrator.");
        var updated = member with
        {
            Role = assignedRole.BuiltInRole ?? OrganizationMemberRole.Viewer,
            CustomRoleId = assignedRole.CustomRoleId,
            CustomRoleName = assignedRole.CustomRoleId is null ? null : assignedRole.Name
        };
        _members[memberId] = updated;
        RecordAudit(organizationId, actor, "MemberRoleChanged", member.Email, $"{member.AssignedRoleName} -> {updated.AssignedRoleName}.");
        Persist();
        return updated;
    }

    public OrganizationMember SetMemberActive(Guid organizationId, OrganizationIdentity actor, Guid memberId, bool isActive)
    {
        EnsureAuthorized(organizationId, actor, OrganizationAction.ManageMembers);
        if (!_members.TryGetValue(memberId, out var member) || member.OrganizationId != organizationId)
            throw new KeyNotFoundException("Organization member was not found.");
        if (member.IsActive && !isActive && member.CustomRoleId is null && member.Role == OrganizationMemberRole.OrganizationAdmin
            && _members.Values.Count(item => item.OrganizationId == organizationId && item.IsActive && item.Role == OrganizationMemberRole.OrganizationAdmin) <= 1)
            throw new InvalidOperationException("An organization must retain at least one active administrator.");
        var updated = member with { IsActive = isActive };
        _members[memberId] = updated;
        RecordAudit(organizationId, actor, isActive ? "MemberActivated" : "MemberDeactivated", member.Email, "Membership status changed.");
        Persist();
        return updated;
    }

    public IReadOnlyList<TenantBinding> GetTenantBindings(Guid organizationId, OrganizationIdentity identity)
    {
        EnsureAuthorized(organizationId, identity, OrganizationAction.ViewInventory);
        return _bindings.Values.Where(binding => binding.OrganizationId == organizationId).ToArray();
    }

    public bool IsAccountAvailable(Guid organizationId, OrganizationIdentity identity, CloudProvider provider, string externalAccountId)
    {
        EnsureAuthorized(organizationId, identity, OrganizationAction.ManageConnections);
        if (string.IsNullOrWhiteSpace(externalAccountId)) return false;
        return !_bindings.Values.Any(binding => binding.Provider == provider
            && string.Equals(binding.ExternalTenantId, externalAccountId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<OrganizationAuditEntry> GetAuditTrail(Guid organizationId, OrganizationIdentity identity)
    {
        EnsureAuthorized(organizationId, identity, OrganizationAction.ManageMembers);
        return _auditEntries.Where(entry => entry.OrganizationId == organizationId).OrderByDescending(entry => entry.Timestamp).ToArray();
    }

    private void EnsureAuthorized(Guid organizationId, OrganizationIdentity identity, OrganizationAction action)
    {
        var decision = Authorize(organizationId, identity, action);
        if (!decision.Allowed) throw new UnauthorizedAccessException(decision.Reason);
    }

    private static bool IsTenantMatch(OrganizationProfile organization, OrganizationIdentity identity)
        => string.Equals(organization.EntraTenantId, identity.TenantId, StringComparison.OrdinalIgnoreCase);

    private static bool BuiltInRoleAllows(OrganizationMemberRole role, OrganizationAction action) => action switch
    {
        OrganizationAction.ViewInventory or OrganizationAction.ViewFindings => true,
        OrganizationAction.RunScan or OrganizationAction.ExportReports => role is not OrganizationMemberRole.Viewer,
        OrganizationAction.ManageMembers or OrganizationAction.ManageConnections or OrganizationAction.ManagePolicies => role == OrganizationMemberRole.OrganizationAdmin,
        OrganizationAction.ManageRemediations => role is OrganizationMemberRole.OrganizationAdmin or OrganizationMemberRole.SecurityAnalyst,
        _ => false
    };

    private OrganizationRoleOption ResolveRole(Guid organizationId, OrganizationRoleOption role)
    {
        ArgumentNullException.ThrowIfNull(role);
        if (role.CustomRoleId is Guid customRoleId)
        {
            if (role.BuiltInRole is not null || !_customRoles.TryGetValue(customRoleId, out var customRole) || customRole.OrganizationId != organizationId)
                throw new ArgumentException("The selected custom role is invalid for this organization.", nameof(role));
            return new OrganizationRoleOption(customRole.Name, null, customRoleId);
        }
        if (role.BuiltInRole is not OrganizationMemberRole builtInRole || !Enum.IsDefined(builtInRole))
            throw new ArgumentException("A valid built-in or organization custom role is required.", nameof(role));
        return new OrganizationRoleOption(builtInRole.ToString(), builtInRole, null);
    }

    private string ValidateCustomRoleName(Guid organizationId, string name, Guid? exceptRoleId)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Custom role name is required.", nameof(name));
        var normalizedName = name.Trim();
        if (Enum.GetNames<OrganizationMemberRole>().Contains(normalizedName, StringComparer.OrdinalIgnoreCase)
            || _customRoles.Values.Any(role => role.OrganizationId == organizationId && role.Id != exceptRoleId
                && string.Equals(role.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A built-in or custom role already uses this name.");
        return normalizedName;
    }

    private static IReadOnlyList<OrganizationAction> NormalizePermissions(IEnumerable<OrganizationAction> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        var normalized = permissions.Distinct().ToArray();
        if (normalized.Any(action => !Enum.IsDefined(action)))
            throw new ArgumentException("Custom roles can contain only defined organization actions.", nameof(permissions));
        return normalized;
    }

    private static void ValidateIdentity(OrganizationIdentity identity)
    {
        if (identity.UserId == Guid.Empty || string.IsNullOrWhiteSpace(identity.Subject)
            || string.IsNullOrWhiteSpace(identity.TenantId) || string.IsNullOrWhiteSpace(identity.Email))
            throw new UnauthorizedAccessException("A validated Entra identity is required.");
    }

    private void Persist() => _store?.Save(new OrganizationDirectoryState(
        _organizations.Values.ToArray(), _members.Values.ToArray(), _bindings.Values.ToArray())
    {
        AuditEntries = _auditEntries.ToArray(),
        CustomRoles = _customRoles.Values.ToArray()
    });

    private void RecordAudit(Guid organizationId, OrganizationIdentity actor, string action, string target, string details)
        => _auditEntries.Add(new OrganizationAuditEntry(DateTime.UtcNow, organizationId, actor.UserId, actor.Email.Trim(), action, target, details));

    private OrganizationProfile RequireOrganization(Guid id) => _organizations.TryGetValue(id, out var organization) ? organization : throw new KeyNotFoundException($"Organization {id} was not found.");
}
