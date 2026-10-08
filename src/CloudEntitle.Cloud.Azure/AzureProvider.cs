namespace CloudEntitle.Cloud.Azure;

using System.Text.Json;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Cloud.Azure.Services;
using CloudEntitle.Cloud.Azure.Mappers;

public class AzureProvider : ICloudProvider, IIdentityProvider, IResourceProvider, IRoleProvider, IPermissionProvider, ICloudEffectiveAccessProvider, IEntraDirectoryProvider, ICloudProviderDiagnostics
{
    private readonly ICloudCommandRunner? _commandRunner;
    private readonly bool _useLiveDiscovery;
    private readonly Dictionary<Guid, CloudAccount> _accounts = new();
    private readonly List<string> _providerMessages = new();

    public AzureProvider(ICloudCommandRunner? commandRunner = null, bool useLiveDiscovery = false)
    {
        _commandRunner = commandRunner;
        _useLiveDiscovery = useLiveDiscovery && commandRunner is not null;
    }

    public CloudProviderType ProviderType => CloudProviderType.Azure;
    public IReadOnlyList<string> DrainProviderMessages()
    {
        var messages = _providerMessages.ToArray();
        _providerMessages.Clear();
        return messages;
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery)
        {
            return true;
        }

        var result = await RunAzAsync(new[] { "account", "list", "--all", "--output", "json" }, cancellationToken);
        if (!result.IsSuccess) return false;
        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.ValueKind == JsonValueKind.Array
            && document.RootElement.EnumerateArray().Any(subscription =>
                subscription.TryGetProperty("id", out var id)
                && !string.IsNullOrWhiteSpace(id.GetString())
                && (!subscription.TryGetProperty("state", out var state)
                    || !string.Equals(state.GetString(), "Disabled", StringComparison.OrdinalIgnoreCase)));
    }

    public async Task<IReadOnlyList<CloudAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery)
        {
            return MockAccounts();
        }

        var output = await GetAzOutputAsync(new[] { "account", "list", "--all", "--output", "json" }, cancellationToken);
        using var document = JsonDocument.Parse(output);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Azure CLI account list returned an unexpected response.");

        _accounts.Clear();
        var accounts = document.RootElement.EnumerateArray()
            .Where(subscription => !subscription.TryGetProperty("state", out var state)
                || !string.Equals(state.GetString(), "Disabled", StringComparison.OrdinalIgnoreCase))
            .Select(subscription =>
            {
                var tenantId = subscription.TryGetProperty("tenantId", out var tenant) ? tenant.GetString() : null;
                var account = new CloudAccount
                {
                    Id = Guid.NewGuid(),
                    Name = subscription.TryGetProperty("name", out var name) ? name.GetString() ?? "Azure subscription" : "Azure subscription",
                    CloudProvider = CloudProviderType.Azure,
                    SubscriptionId = subscription.TryGetProperty("id", out var id) ? id.GetString() : null,
                    ExternalAccountId = tenantId,
                    TenantId = tenantId,
                    ConnectionStatus = ConnectionStatus.Connected,
                    IsReadOnly = true,
                    LastScanAt = DateTime.UtcNow
                };
                _accounts[account.Id] = account;
                return account;
            })
            .Where(account => !string.IsNullOrWhiteSpace(account.SubscriptionId))
            .ToArray();
        return accounts;
    }

    public async Task<IReadOnlyList<CloudPrincipal>> DiscoverIdentitiesAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery)
        {
            return MockIdentities(accountId);
        }

        return (await GetAssignmentsAsync(accountId, cancellationToken))
            .Where(assignment => assignment.TryGetProperty("principalId", out _))
            .GroupBy(assignment => assignment.GetProperty("principalId").GetString())
            .Select(group =>
            {
                var assignment = group.First();
                var principalType = assignment.TryGetProperty("principalType", out var type)
                    ? type.GetString()
                    : null;
                return new CloudPrincipal
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    CloudProvider = CloudProviderType.Azure,
                    Name = assignment.TryGetProperty("principalName", out var name)
                        ? name.GetString() ?? group.Key ?? "unknown-principal"
                        : group.Key ?? "unknown-principal",
                    PrincipalId = group.Key,
                    IdentityType = principalType?.Equals("Group", StringComparison.OrdinalIgnoreCase) == true
                        ? IdentityType.Group
                        : principalType?.Contains("Service", StringComparison.OrdinalIgnoreCase) == true
                            ? IdentityType.ServiceAccount
                            : IdentityType.User,
                    IsPrivileged = group.Any(IsPrivilegedAssignment),
                    IsActive = true
                };
            })
            .ToArray();
    }

    public async Task<IReadOnlyList<CloudPermission>> DiscoverPermissionsAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery)
        {
            return MockPermissions(accountId);
        }

        return (await GetAssignmentsAsync(accountId, cancellationToken))
            .Select(assignment => new CloudPermission
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Name = assignment.TryGetProperty("roleDefinitionName", out var role)
                    ? role.GetString() ?? "Unknown role"
                    : "Unknown role",
                Action = assignment.TryGetProperty("roleDefinitionName", out role) ? role.GetString() : null,
                Resource = assignment.TryGetProperty("scope", out var scope) ? scope.GetString() : null,
                Scope = assignment.TryGetProperty("scope", out scope) ? scope.GetString() : null,
                Source = "Azure RBAC"
            })
            .ToArray();
    }

    public async Task<IReadOnlyList<CloudRole>> DiscoverRolesAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery)
        {
            return MockRoles(accountId);
        }

        return (await GetAssignmentsAsync(accountId, cancellationToken))
            .Where(assignment => assignment.TryGetProperty("roleDefinitionId", out _))
            .GroupBy(assignment => assignment.GetProperty("roleDefinitionId").GetString())
            .Select(group =>
            {
                var assignment = group.First();
                return new CloudRole
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    Name = assignment.TryGetProperty("roleDefinitionName", out var name)
                        ? name.GetString() ?? "Unknown role"
                        : "Unknown role",
                    RoleId = group.Key ?? string.Empty,
                    Scope = assignment.TryGetProperty("scope", out var scope) ? scope.GetString() : null
                };
            })
            .ToArray();
    }

    public async Task<IReadOnlyList<CloudEffectiveAccessAssignment>> DiscoverEffectiveAccessAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery)
        {
            return Array.Empty<CloudEffectiveAccessAssignment>();
        }

        var assignments = await GetAssignmentsAsync(accountId, cancellationToken);
        var lastChanged = await GetAzureRoleAssignmentChangeTimesAsync(accountId, cancellationToken);
        return assignments
            .Where(assignment => assignment.TryGetProperty("principalId", out _))
            .Select(assignment =>
            {
                var roleName = assignment.TryGetProperty("roleDefinitionName", out var role) ? role.GetString() ?? "Unknown role" : "Unknown role";
                var roleId = assignment.TryGetProperty("roleDefinitionId", out var roleDefinitionId) ? roleDefinitionId.GetString() : null;
                var principal = assignment.TryGetProperty("principalName", out var name) ? name.GetString() : null;
                var principalId = assignment.TryGetProperty("principalId", out var principalIdValue) ? principalIdValue.GetString() : null;
                if (string.IsNullOrWhiteSpace(principal) && !string.IsNullOrWhiteSpace(principalId))
                {
                    principal = principalId;
                }
                var scope = assignment.TryGetProperty("scope", out var scopeValue) ? scopeValue.GetString() ?? "Subscription" : "Subscription";
                (DateTime ChangedAt, string? ChangedBy)? audit = principalId is not null && roleId is not null
                    && lastChanged.TryGetValue(AzureAuditKey(principalId, roleId, scope), out var auditChange)
                    ? auditChange
                    : null;
                var lastChangedAt = audit?.ChangedAt;
                return new CloudEffectiveAccessAssignment(
                    principal ?? "Unknown principal",
                    roleName,
                    scope,
                    lastChangedAt.HasValue ? "Azure RBAC role assignment; Activity Log" : "Azure RBAC role assignment",
                    roleName.Contains("Owner", StringComparison.OrdinalIgnoreCase) || roleName.Contains("Administrator", StringComparison.OrdinalIgnoreCase),
                    LastChangedAt: lastChangedAt,
                    LastChangedBy: audit?.ChangedBy);
            })
            .ToArray();
    }

    private async Task<Dictionary<string, (DateTime ChangedAt, string? ChangedBy)>> GetAzureRoleAssignmentChangeTimesAsync(Guid accountId, CancellationToken cancellationToken)
    {
        if (!_accounts.TryGetValue(accountId, out var account) || string.IsNullOrWhiteSpace(account.SubscriptionId)) return new Dictionary<string, (DateTime, string?)>();
        var result = await RunAzAsync(new[]
        {
            "monitor", "activity-log", "list", "--offset", "90d", "--max-events", "1000",
            "--subscription", account.SubscriptionId, "--output", "json"
        }, cancellationToken);
        if (!result.IsSuccess)
        {
            _providerMessages.Add($"Azure role-assignment change history unavailable: {result.StandardError.Trim()}");
            return new Dictionary<string, (DateTime, string?)>();
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return new Dictionary<string, (DateTime, string?)>();
        var changes = new Dictionary<string, (DateTime ChangedAt, string? ChangedBy)>(StringComparer.OrdinalIgnoreCase);
        foreach (var activity in document.RootElement.EnumerateArray())
        {
            var action = ReadNestedString(activity, "authorization", "action");
            if (action is null || !action.Contains("roleAssignments/write", StringComparison.OrdinalIgnoreCase)
                || !activity.TryGetProperty("eventTimestamp", out var timestamp)
                || !DateTime.TryParse(timestamp.GetString(), out var changedAt)) continue;
            var principalId = ReadNestedString(activity, "properties", "responseBody", "properties", "principalId");
            var roleId = ReadNestedString(activity, "properties", "responseBody", "properties", "roleDefinitionId");
            var scope = ReadNestedString(activity, "properties", "responseBody", "properties", "scope")
                ?? ReadNestedString(activity, "authorization", "scope");
            if (principalId is null || roleId is null || scope is null) continue;
            var key = AzureAuditKey(principalId, roleId, scope);
            var changedBy = activity.TryGetProperty("caller", out var caller) && caller.ValueKind == JsonValueKind.String ? caller.GetString() : null;
            if (!changes.TryGetValue(key, out var existing) || changedAt > existing.ChangedAt) changes[key] = (changedAt.ToUniversalTime(), changedBy);
        }
        return changes;
    }

    private static string AzureAuditKey(string principalId, string roleId, string scope)
        => $"{principalId}|{roleId}|{scope.TrimEnd('/')}";

    private static string? ReadNestedString(JsonElement element, params string[] path)
    {
        for (var index = 0; index < path.Length; index++)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                using var nestedDocument = JsonDocument.Parse(element.GetString()!);
                return ReadNestedString(nestedDocument.RootElement, path.Skip(index).ToArray());
            }
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(path[index], out element)) return null;
        }
        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    public async Task<EntraDirectoryScanResult> DiscoverEntraDirectoryAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery)
        {
            return new EntraDirectoryScanResult(Array.Empty<CloudPrincipal>(), Array.Empty<CloudEffectiveAccessAssignment>(), Array.Empty<string>());
        }

        var principals = new List<CloudPrincipal>();
        var accessAssignments = new List<CloudEffectiveAccessAssignment>();
        var messages = new List<string>();

        await LoadDirectoryCollectionAsync("users", "https://graph.microsoft.com/v1.0/users?$select=id,displayName,userPrincipalName,accountEnabled&$top=999", element =>
        {
            principals.Add(new CloudPrincipal
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                CloudProvider = CloudProviderType.Azure,
                Name = ReadString(element, "displayName") ?? "Entra user",
                PrincipalId = ReadString(element, "id"),
                Email = ReadString(element, "userPrincipalName"),
                IdentityType = IdentityType.User,
                IsActive = ReadBoolean(element, "accountEnabled")
            });
        });
        await LoadDirectoryCollectionAsync("groups", "https://graph.microsoft.com/v1.0/groups?$select=id,displayName,securityEnabled,groupTypes&$top=999", element =>
        {
            principals.Add(new CloudPrincipal
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                CloudProvider = CloudProviderType.Azure,
                Name = ReadString(element, "displayName") ?? "Entra group",
                PrincipalId = ReadString(element, "id"),
                IdentityType = IdentityType.Group,
                IsActive = true
            });
        });
        await LoadDirectoryCollectionAsync("service principals", "https://graph.microsoft.com/v1.0/servicePrincipals?$select=id,displayName,appId,servicePrincipalType,accountEnabled&$top=999", element =>
        {
            var type = ReadString(element, "servicePrincipalType");
            principals.Add(new CloudPrincipal
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                CloudProvider = CloudProviderType.Azure,
                Name = ReadString(element, "displayName") ?? "Entra service principal",
                PrincipalId = ReadString(element, "id"),
                IdentityType = type?.Equals("ManagedIdentity", StringComparison.OrdinalIgnoreCase) == true ? IdentityType.ManagedIdentity : IdentityType.ServicePrincipal,
                IsActive = ReadBoolean(element, "accountEnabled")
            });
        });

        await LoadPimAssignmentsAsync(
            "active PIM assignments",
            "https://graph.microsoft.com/v1.0/roleManagement/directory/roleAssignmentScheduleInstances?$expand=principal,roleDefinition&$top=999",
            "Active (PIM)");
        await LoadPimAssignmentsAsync(
            "eligible PIM assignments",
            "https://graph.microsoft.com/v1.0/roleManagement/directory/roleEligibilityScheduleInstances?$expand=principal,roleDefinition&$top=999",
            "Eligible (PIM)");

        messages.Add($"Azure Entra: discovered {principals.Count} directory identities and {accessAssignments.Count} PIM role schedule(s).");
        return new EntraDirectoryScanResult(principals, accessAssignments, messages);

        async Task LoadDirectoryCollectionAsync(string label, string url, Action<JsonElement> map)
        {
            try
            {
                foreach (var element in await GetGraphCollectionAsync(url, cancellationToken)) map(element);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                messages.Add($"Azure Entra {label} unavailable: {exception.Message}");
            }
        }

        async Task LoadPimAssignmentsAsync(string label, string url, string assignmentType)
        {
            try
            {
                foreach (var assignment in await GetGraphCollectionAsync(url, cancellationToken))
                {
                    var principal = ReadExpandedName(assignment, "principal")
                        ?? ReadString(assignment, "principalId")
                        ?? "Unknown principal";
                    var role = ReadExpandedName(assignment, "roleDefinition")
                        ?? ReadString(assignment, "roleDefinitionId")
                        ?? "Unknown Entra role";
                    accessAssignments.Add(new CloudEffectiveAccessAssignment(
                        principal,
                        role,
                        ReadString(assignment, "directoryScopeId") ?? "/",
                        $"Entra {assignmentType}",
                        true,
                        assignmentType));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                messages.Add(FormatPimFailure(label, assignmentType, exception));
            }
        }
    }

    private static string FormatPimFailure(string label, string assignmentType, Exception exception)
    {
        var requiredPermission = assignmentType == "Active (PIM)"
            ? "RoleAssignmentSchedule.Read.Directory"
            : "RoleEligibilitySchedule.Read.Directory";
        var message = exception.ToString();
        var permissionDenied = message.Contains("PermissionScopeNotGranted", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Insufficient privileges", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Forbidden", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Authorization_RequestDenied", StringComparison.OrdinalIgnoreCase);
        if (permissionDenied)
        {
            return $"Azure Entra {label} unavailable: Microsoft Graph denied the read permission '{requiredPermission}'. An Entra tenant administrator must grant admin consent for this delegated permission to the Microsoft Azure CLI enterprise application. Then refresh the Azure CLI sign-in. Azure RBAC and successfully discovered directory identities are retained; this PIM data is omitted.";
        }

        return $"Azure Entra {label} unavailable: {ExtractCliErrorDetail(exception.Message)}";
    }

    private static string ExtractCliErrorDetail(string message)
    {
        var jsonStart = message.IndexOf('{');
        if (jsonStart >= 0)
        {
            try
            {
                using var document = JsonDocument.Parse(message[jsonStart..]);
                var error = document.RootElement.TryGetProperty("error", out var nestedError) ? nestedError : document.RootElement;
                var code = ReadString(error, "code");
                var detail = ReadString(error, "message");
                if (!string.IsNullOrWhiteSpace(detail)) return string.IsNullOrWhiteSpace(code) ? detail : $"{code}: {detail}";
            }
            catch (JsonException)
            {
            }
        }

        var singleLine = string.Join(" ", message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        return singleLine.Length <= 240 ? singleLine : singleLine[..237] + "...";
    }

    private async Task<IReadOnlyList<JsonElement>> GetGraphCollectionAsync(string url, CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        var nextUrl = url;
        while (!string.IsNullOrWhiteSpace(nextUrl))
        {
            using var document = JsonDocument.Parse(await GetAzOutputAsync(new[] { "rest", "--method", "GET", "--url", nextUrl, "--output", "json" }, cancellationToken));
            var root = document.RootElement;
            if (root.TryGetProperty("value", out var values) && values.ValueKind == JsonValueKind.Array)
            {
                items.AddRange(values.EnumerateArray().Select(item => item.Clone()));
            }
            nextUrl = ReadString(root, "@odata.nextLink") ?? string.Empty;
        }
        return items;
    }

    private static string? ReadString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBoolean(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? ReadExpandedName(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var expanded)
            ? ReadString(expanded, "displayName")
            : null;

    public async Task<(IReadOnlyList<CloudRole> Roles, IReadOnlyList<CloudPermission> Permissions)> DiscoverRoleDefinitionsAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery)
        {
            return (MockRoles(accountId), MockPermissions(accountId));
        }

        if (!_accounts.TryGetValue(accountId, out var account) || string.IsNullOrWhiteSpace(account.SubscriptionId))
        {
            throw new InvalidOperationException("The Azure account must be discovered before role definitions can be loaded.");
        }

        using var document = JsonDocument.Parse(await GetAzOutputAsync(new[] { "role", "definition", "list", "--subscription", account.SubscriptionId, "--output", "json" }, cancellationToken));
        var roles = new List<CloudRole>();
        var permissions = new List<CloudPermission>();
        foreach (var roleDefinition in document.RootElement.EnumerateArray())
        {
            var roleId = roleDefinition.TryGetProperty("name", out var name) ? name.GetString() : null;
            var properties = roleDefinition.TryGetProperty("properties", out var value) ? value : roleDefinition;
            var roleNameText = properties.TryGetProperty("roleName", out var roleName) ? roleName.GetString() ?? "Azure role" : "Azure role";
            var role = new CloudRole { Id = Guid.NewGuid(), AccountId = accountId, RoleId = roleId ?? string.Empty, Name = roleNameText, Description = properties.TryGetProperty("description", out var description) ? description.GetString() : null, IsPrivileged = roleNameText.Contains("Owner", StringComparison.OrdinalIgnoreCase) || roleNameText.Contains("Administrator", StringComparison.OrdinalIgnoreCase) };
            roles.Add(role);
            permissions.AddRange(AzurePermissionMapper.MapRolePermissions(accountId, roleDefinition));
        }

        return (roles, permissions);
    }

    public Task<IReadOnlyList<CloudPrincipal>> GetPrincipalsAsync(Guid accountId, CancellationToken cancellationToken = default)
        => DiscoverIdentitiesAsync(accountId, cancellationToken);

    public Task<IReadOnlyList<CloudPermission>> GetPermissionsAsync(Guid accountId, CancellationToken cancellationToken = default)
        => DiscoverPermissionsAsync(accountId, cancellationToken);

    public async Task<IReadOnlyList<CloudResource>> GetResourcesAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery)
        {
            return new[] { new CloudResource { Id = Guid.NewGuid(), AccountId = accountId, Name = "rg-production", Type = "Microsoft.Resources/resourceGroups", ResourceId = "/subscriptions/sub-demo-azure/resourceGroups/rg-production", Scope = "/subscriptions/sub-demo-azure" } };
        }

        if (!_accounts.TryGetValue(accountId, out var account) || string.IsNullOrWhiteSpace(account.SubscriptionId))
        {
            throw new InvalidOperationException("The Azure account must be discovered before resources can be loaded.");
        }

        using var document = JsonDocument.Parse(await GetAzOutputAsync(new[] { "resource", "list", "--subscription", account.SubscriptionId, "--output", "json" }, cancellationToken));
        return document.RootElement.EnumerateArray().Select(resource =>
        {
            var tags = resource.TryGetProperty("tags", out var resourceTags) && resourceTags.ValueKind == JsonValueKind.Object
                ? resourceTags.EnumerateObject().ToDictionary(tag => tag.Name,
                    tag => tag.Value.ValueKind == JsonValueKind.String ? tag.Value.GetString() ?? string.Empty : tag.Value.ToString(),
                    StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (resource.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            {
                CopyPropertyTag(properties, tags, "publicNetworkAccess", "PublicNetworkAccess");
                CopyPropertyTag(properties, tags, "allowBlobPublicAccess", "AllowBlobPublicAccess");
                if (properties.TryGetProperty("networkAcls", out var networkAcls) && networkAcls.ValueKind == JsonValueKind.Object)
                    CopyPropertyTag(networkAcls, tags, "defaultAction", "NetworkDefaultAction");
            }
            return new CloudResource
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                ExternalId = resource.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                Name = resource.TryGetProperty("name", out var name) ? name.GetString() ?? "Azure resource" : "Azure resource",
                Type = resource.TryGetProperty("type", out var type) ? type.GetString() ?? "Unknown" : "Unknown",
                ResourceType = ResourceType.Resource,
                ResourceUri = resource.TryGetProperty("id", out id) ? id.GetString() : null,
                Region = resource.TryGetProperty("location", out var location) ? location.GetString() : null,
                Location = resource.TryGetProperty("location", out location) ? location.GetString() : null,
                Scope = resource.TryGetProperty("id", out id) ? id.GetString() : null,
                Tags = tags
            };
        }).ToArray();
    }

    private static void CopyPropertyTag(JsonElement source, IDictionary<string, string> tags, string propertyName, string tagName)
    {
        if (source.TryGetProperty(propertyName, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            tags[tagName] = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
    }

    public Task<IReadOnlyList<CloudRole>> GetRolesAsync(Guid accountId, CancellationToken cancellationToken = default)
        => DiscoverRolesAsync(accountId, cancellationToken);

    private async Task<JsonElement[]> GetAssignmentsAsync(Guid accountId, CancellationToken cancellationToken)
    {
        if (!_accounts.TryGetValue(accountId, out var account) || string.IsNullOrWhiteSpace(account.SubscriptionId))
        {
            throw new InvalidOperationException("The Azure account must be discovered before assignments can be loaded.");
        }

        using var document = JsonDocument.Parse(await GetAzOutputAsync(
            new[] { "role", "assignment", "list", "--all", "--subscription", account.SubscriptionId, "--output", "json" },
            cancellationToken));
        return document.RootElement.EnumerateArray().Select(element => element.Clone()).ToArray();
    }

    private async Task<string> GetAzOutputAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await RunAzAsync(arguments, cancellationToken);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"Azure CLI command failed: {result.StandardError}");
        }

        return result.StandardOutput;
    }

    private Task<CloudCommandResult> RunAzAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        => RunAzWithRetryAsync(arguments, cancellationToken);

    public async Task<AzureScanResult> ScanAsync(Guid? accountId = null, CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;
        var accounts = new List<CloudAccount>();
        var resources = new List<CloudResource>();
        var principals = new List<CloudPrincipal>();
        var roles = new List<CloudRole>();
        var permissions = new List<CloudPermission>();
        var assignments = new List<RoleAssignment>();
        var entitlements = new List<Entitlement>();
        var errors = new List<string>();
        try
        {
            accounts.AddRange(await DiscoverAccountsAsync(cancellationToken));
            if (accountId.HasValue) accounts = accounts.Where(account => account.Id == accountId.Value).ToList();
            foreach (var account in accounts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    resources.AddRange(await GetResourcesAsync(account.Id, cancellationToken));
                    principals.AddRange(await DiscoverIdentitiesAsync(account.Id, cancellationToken));
                    roles.AddRange(await DiscoverRolesAsync(account.Id, cancellationToken));
                    permissions.AddRange(await DiscoverPermissionsAsync(account.Id, cancellationToken));
                    var discoveredAssignments = await GetRoleAssignmentsAsync(account, cancellationToken);
                    assignments.AddRange(discoveredAssignments.Select(item => new RoleAssignment { Id = Guid.NewGuid(), AccountId = account.Id, PrincipalId = principals.FirstOrDefault(principal => principal.PrincipalId == item.PrincipalId)?.Id ?? Guid.Empty, RoleId = roles.FirstOrDefault(role => role.RoleId == item.RoleId)?.Id ?? Guid.Empty, Scope = item.Scope, Source = "Azure RBAC" }));
                    entitlements.AddRange(assignments.Where(item => item.AccountId == account.Id && item.PrincipalId != Guid.Empty && item.RoleId != Guid.Empty).Select(item => new Entitlement { Id = Guid.NewGuid(), PrincipalId = item.PrincipalId, RoleId = item.RoleId, CloudAccountId = account.Id, Scope = item.Scope, AssignmentType = AssignmentType.Direct, GrantedAt = startedAt }));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    errors.Add($"{account.Name}: {exception.Message}");
                }
            }

            var scan = new Scan { Id = Guid.NewGuid(), CloudAccountId = accounts.FirstOrDefault()?.Id ?? Guid.Empty, StartedAt = startedAt, CompletedAt = DateTime.UtcNow, Status = errors.Count == accounts.Count && accounts.Count > 0 ? ScanStatus.Failed : ScanStatus.Completed, ItemsDiscovered = resources.Count, IdentitiesDiscovered = principals.Count, PermissionsDiscovered = permissions.Count, ErrorMessage = errors.Count == 0 ? null : string.Join(" | ", errors) };
            return new AzureScanResult(scan, accounts, resources, principals, roles, permissions, assignments, entitlements, errors);
        }
        catch (OperationCanceledException)
        {
            return new AzureScanResult(new Scan { Id = Guid.NewGuid(), CloudAccountId = accounts.FirstOrDefault()?.Id ?? Guid.Empty, StartedAt = startedAt, CompletedAt = DateTime.UtcNow, Status = ScanStatus.Cancelled, ErrorMessage = "Azure scan cancelled." }, accounts, resources, principals, roles, permissions, assignments, entitlements, errors);
        }
    }

    private async Task<IReadOnlyList<RoleAssignmentRecord>> GetRoleAssignmentsAsync(CloudAccount account, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await GetAzOutputAsync(new[] { "role", "assignment", "list", "--all", "--subscription", account.SubscriptionId!, "--output", "json" }, cancellationToken));
        return document.RootElement.EnumerateArray().Select(item => new RoleAssignmentRecord(
            item.TryGetProperty("principalId", out var principal) ? principal.GetString() : null,
            item.TryGetProperty("roleDefinitionId", out var role) ? role.GetString() : null,
            item.TryGetProperty("scope", out var scope) ? scope.GetString() : null)).ToArray();
    }

    private async Task<CloudCommandResult> RunAzWithRetryAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        CloudCommandResult? result = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = await _commandRunner!.RunAsync("az", arguments, cancellationToken);
            if (result.IsSuccess || attempt == 2) return result;
            await Task.Delay(TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt)), cancellationToken);
        }

        return result!;
    }

    private sealed record RoleAssignmentRecord(string? PrincipalId, string? RoleId, string? Scope);

    private static bool IsPrivilegedAssignment(JsonElement assignment)
        => assignment.TryGetProperty("roleDefinitionName", out var role)
            && (role.GetString()?.Contains("Owner", StringComparison.OrdinalIgnoreCase) == true
                || role.GetString()?.Contains("Administrator", StringComparison.OrdinalIgnoreCase) == true);

    private static IReadOnlyList<CloudAccount> MockAccounts() => new[]
    {
        new CloudAccount
        {
            Id = Guid.NewGuid(), Name = "Azure Demo Subscription", CloudProvider = CloudProviderType.Azure,
            SubscriptionId = "sub-demo-azure", ExternalAccountId = "tenant-demo",
            ConnectionStatus = ConnectionStatus.Connected, IsReadOnly = true
        }
    };

    private static IReadOnlyList<CloudPrincipal> MockIdentities(Guid accountId) => new[]
    {
        new CloudPrincipal
        {
            Id = Guid.NewGuid(), AccountId = accountId, CloudProvider = CloudProviderType.Azure,
            Name = "Global Administrator", IdentityType = IdentityType.User, Email = "admin@contoso.com",
            IsPrivileged = true, IsActive = true
        }
    };

    private static IReadOnlyList<CloudPermission> MockPermissions(Guid accountId) => new[]
    {
        new CloudPermission
        {
            Id = Guid.NewGuid(), AccountId = accountId, Name = "Owner", Action = "*/*",
            Resource = "/subscriptions/sub-demo-azure", Scope = "/subscriptions/sub-demo-azure", Source = "Azure RBAC"
        }
    };

    private static IReadOnlyList<CloudRole> MockRoles(Guid accountId) => new[]
    {
        new CloudRole
        {
            Id = Guid.NewGuid(), AccountId = accountId, Name = "Owner",
            RoleId = "8e3af657-a8ff-443c-a75c-2eb6d3b5c6a9", Description = "Full access to all resources",
            Scope = "/subscriptions/sub-demo-azure"
        }
    };
}
