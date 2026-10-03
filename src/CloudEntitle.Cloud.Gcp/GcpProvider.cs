namespace CloudEntitle.Cloud.Gcp;

using System.Text.Json;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Cloud.Gcp.Mappers;
using CloudEntitle.Cloud.Gcp.Services;

public class GcpProvider : ICloudProvider, IIdentityProvider, IResourceProvider, IRoleProvider, IPermissionProvider, ICloudEffectiveAccessProvider, ICloudOrganizationProvider
{
    private readonly ICloudCommandRunner? _commandRunner;
    private readonly bool _useLiveDiscovery;
    private readonly Dictionary<Guid, CloudAccount> _accounts = new();
    private readonly Dictionary<string, IReadOnlyList<GcpResourceAncestor>> _projectAncestors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<GcpBinding>> _ancestorBindingsByScope = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, IReadOnlyList<GcpBinding>> _bindingsByAccount = new();
    private readonly List<string> _providerMessages = new();

    private sealed record GcpResourceAncestor(string Type, string Id);

    public GcpProvider(ICloudCommandRunner? commandRunner = null, bool useLiveDiscovery = false)
    {
        _commandRunner = commandRunner;
        _useLiveDiscovery = useLiveDiscovery && commandRunner is not null;
    }

    public CloudProviderType ProviderType => CloudProviderType.Gcp;
    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default) => !_useLiveDiscovery || (await RunGcloudAsync(new[] { "auth", "list", "--format=json" }, cancellationToken)).IsSuccess;

    public async Task<IReadOnlyList<CloudAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery) return MockAccounts();
        _accounts.Clear();
        _projectAncestors.Clear();
        _ancestorBindingsByScope.Clear();
        _bindingsByAccount.Clear();
        _providerMessages.Clear();
        using var document = JsonDocument.Parse(await GetGcloudOutputAsync(new[] { "projects", "list", "--format=json" }, cancellationToken));
        var accounts = document.RootElement.EnumerateArray().Select(project => new CloudAccount
        {
            Id = Guid.NewGuid(),
            Name = project.TryGetProperty("name", out var name) ? name.GetString() ?? "GCP project" : "GCP project",
            CloudProvider = CloudProviderType.Gcp,
            ProjectId = project.GetProperty("projectId").GetString(),
            ExternalAccountId = project.TryGetProperty("projectNumber", out var number) ? number.GetString() : null,
            ConnectionStatus = ConnectionStatus.Connected, IsReadOnly = true, LastScanAt = DateTime.UtcNow
        }).ToArray();
        foreach (var account in accounts) _accounts[account.Id] = account;
        return accounts;
    }

    public async Task<CloudOrganizationScanResult> DiscoverOrganizationAccountsAsync(CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery) return new CloudOrganizationScanResult(Array.Empty<CloudAccount>(), Array.Empty<string>());

        var hierarchyAccounts = new Dictionary<string, CloudAccount>(StringComparer.OrdinalIgnoreCase);
        var messages = new List<string>(_providerMessages);
        _providerMessages.Clear();
        foreach (var project in _accounts.Values)
        {
            if (string.IsNullOrWhiteSpace(project.ProjectId)) continue;
            IReadOnlyList<GcpResourceAncestor> ancestors;
            try
            {
                ancestors = await GetProjectAncestorsAsync(project.ProjectId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                messages.Add($"GCP hierarchy unavailable for project {project.ProjectId}: {exception.Message}");
                continue;
            }

            var parent = ancestors.Skip(1).FirstOrDefault(ancestor => ancestor.Type is "folder" or "organization");
            if (parent is not null) project.ParentAccountId = $"{parent.Type}s/{parent.Id}";

            foreach (var ancestor in ancestors.Where(item => item.Type is "folder" or "organization"))
            {
                var externalId = $"{ancestor.Type}s/{ancestor.Id}";
                if (!hierarchyAccounts.ContainsKey(externalId))
                {
                    hierarchyAccounts[externalId] = new CloudAccount
                    {
                        Id = Guid.NewGuid(),
                        Name = $"GCP {ancestor.Type} {ancestor.Id}",
                        CloudProvider = CloudProviderType.Gcp,
                        ExternalAccountId = externalId,
                        AccountType = ancestor.Type == "organization" ? AccountType.GcpOrganization : AccountType.Folder,
                        ConnectionStatus = ConnectionStatus.Connected,
                        IsReadOnly = true,
                        ParentAccountId = GetParentHierarchyId(ancestors, ancestor)
                    };
                }
            }
        }

        return new CloudOrganizationScanResult(hierarchyAccounts.Values.ToArray(), messages);
    }

    public async Task<IReadOnlyList<CloudPrincipal>> DiscoverIdentitiesAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery) return MockIdentities(accountId);
        var bindings = await GetEffectiveBindingsAsync(accountId, cancellationToken);
        return bindings.GroupBy(binding => binding.Member, StringComparer.OrdinalIgnoreCase).Select(group => new CloudPrincipal
        {
            Id = Guid.NewGuid(), AccountId = accountId, CloudProvider = CloudProviderType.Gcp,
            Name = group.Key, PrincipalId = group.Key,
            IdentityType = group.Key.StartsWith("serviceAccount:", StringComparison.OrdinalIgnoreCase) ? IdentityType.ServiceAccount : IdentityType.User,
            IsPrivileged = group.Any(binding => binding.Role.Contains("owner", StringComparison.OrdinalIgnoreCase)), IsActive = true
        }).ToArray();
    }

    public async Task<IReadOnlyList<CloudPermission>> DiscoverPermissionsAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery) return MockPermissions(accountId);
        var project = GetProject(accountId);
        return (await GetEffectiveBindingsAsync(accountId, cancellationToken)).Select(binding => GcpIamPolicyMapper.ToPermission(accountId, binding, project)).ToArray();
    }

    public async Task<IReadOnlyList<CloudRole>> DiscoverRolesAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery) return MockRoles(accountId);
        var project = GetProject(accountId);
        return (await GetEffectiveBindingsAsync(accountId, cancellationToken)).GroupBy(binding => (binding.Role, binding.Scope)).Select(group => new CloudRole
        {
            Id = Guid.NewGuid(), AccountId = accountId, Name = group.Key.Role, RoleId = group.Key.Role, Scope = group.Key.Scope ?? $"projects/{project}"
        }).ToArray();
    }

    public async Task<IReadOnlyList<CloudEffectiveAccessAssignment>> DiscoverEffectiveAccessAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery)
        {
            return Array.Empty<CloudEffectiveAccessAssignment>();
        }

        var project = GetProject(accountId);
        return (await GetEffectiveBindingsAsync(accountId, cancellationToken))
            .Select(binding => new CloudEffectiveAccessAssignment(
                binding.Member,
                binding.Role,
                binding.Scope ?? $"projects/{project}",
                binding.IsInherited ? $"Inherited GCP IAM binding from {binding.Scope}" : binding.LastChangedAt.HasValue ? "GCP project IAM binding; Cloud Audit Logs" : "GCP project IAM binding",
                binding.Role.Contains("owner", StringComparison.OrdinalIgnoreCase),
                binding.IsInherited ? "Inherited" : "Direct",
                LastChangedAt: binding.LastChangedAt))
            .ToArray();
    }

    public Task<IReadOnlyList<CloudPrincipal>> GetPrincipalsAsync(Guid accountId, CancellationToken cancellationToken = default) => DiscoverIdentitiesAsync(accountId, cancellationToken);
    public Task<IReadOnlyList<CloudPermission>> GetPermissionsAsync(Guid accountId, CancellationToken cancellationToken = default) => DiscoverPermissionsAsync(accountId, cancellationToken);
    public Task<IReadOnlyList<CloudRole>> GetRolesAsync(Guid accountId, CancellationToken cancellationToken = default) => DiscoverRolesAsync(accountId, cancellationToken);
    public async Task<IReadOnlyList<CloudResource>> GetResourcesAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var project = GetProject(accountId);
        var output = await GetGcloudOutputAsync(new[] { "asset", "search-all-resources", $"--scope=projects/{project}", "--format=json" }, cancellationToken);
        using var document = JsonDocument.Parse(output);
        var results = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray()
            : document.RootElement.TryGetProperty("results", out var resultArray) && resultArray.ValueKind == JsonValueKind.Array
                ? resultArray.EnumerateArray()
                : Enumerable.Empty<JsonElement>();
        return results.Select(item => MapResourceSearchResult(accountId, item, project)).ToArray();
    }

    public async Task<IReadOnlyList<CloudGroup>> DiscoverGroupsAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var bindings = await GetEffectiveBindingsAsync(accountId, cancellationToken);
        return bindings.Where(binding => binding.Member.StartsWith("group:", StringComparison.OrdinalIgnoreCase)).GroupBy(binding => binding.Member).Select(group => new CloudGroup { Id = Guid.NewGuid(), AccountId = accountId, GroupId = group.Key, Name = group.Key[6..], DisplayName = group.Key[6..] }).ToArray();
    }

    public async Task<IReadOnlyList<CloudPrincipal>> DiscoverServiceAccountsAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery) return new[] { new CloudPrincipal { Id = Guid.NewGuid(), AccountId = accountId, CloudProvider = CloudProviderType.Gcp, Name = "demo-service-account", PrincipalId = "demo-service-account", IdentityType = IdentityType.ServiceAccount, IsActive = true } };
        using var document = JsonDocument.Parse(await GetGcloudOutputAsync(new[] { "iam", "service-accounts", "list", "--project", GetProject(accountId), "--format=json" }, cancellationToken));
        return document.RootElement.EnumerateArray().Select(item => new CloudPrincipal { Id = Guid.NewGuid(), AccountId = accountId, CloudProvider = CloudProviderType.Gcp, Name = item.TryGetProperty("displayName", out var name) ? name.GetString() ?? "GCP service account" : "GCP service account", PrincipalId = item.TryGetProperty("uniqueId", out var id) ? id.GetString() : item.TryGetProperty("email", out var email) ? email.GetString() : null, Email = item.TryGetProperty("email", out email) ? email.GetString() : null, IdentityType = IdentityType.ServiceAccount, IsActive = item.TryGetProperty("disabled", out var disabled) && disabled.ValueKind == JsonValueKind.False }).ToArray();
    }

    private async Task<JsonDocument> GetPolicyAsync(Guid accountId, CancellationToken cancellationToken)
        => JsonDocument.Parse(await GetGcloudOutputAsync(new[] { "projects", "get-iam-policy", GetProject(accountId), "--format=json" }, cancellationToken));
    private async Task<IReadOnlyList<GcpBinding>> GetEffectiveBindingsAsync(Guid accountId, CancellationToken cancellationToken)
    {
        if (_bindingsByAccount.TryGetValue(accountId, out var cachedBindings)) return cachedBindings;

        var project = GetProject(accountId);
        using var projectPolicy = await GetPolicyAsync(accountId, cancellationToken);
        var bindings = GcpIamPolicyMapper.MapBindings(projectPolicy.RootElement, $"projects/{project}").ToList();
        try
        {
            var ancestors = await GetProjectAncestorsAsync(project, cancellationToken);
            foreach (var ancestor in ancestors.Where(item => item.Type is "folder" or "organization"))
            {
                var scope = $"{ancestor.Type}s/{ancestor.Id}";
                if (_ancestorBindingsByScope.TryGetValue(scope, out var cachedAncestorBindings))
                {
                    bindings.AddRange(cachedAncestorBindings);
                    continue;
                }

                var query = $"resource:{scope}";
                var result = await RunGcloudAsync(new[] { "asset", "search-all-iam-policies", $"--scope={scope}", $"--query={query}", "--format=json" }, cancellationToken);
                if (!result.IsSuccess)
                {
                    _ancestorBindingsByScope[scope] = Array.Empty<GcpBinding>();
                    _providerMessages.Add($"GCP inherited IAM policy discovery for {scope} was unavailable: {result.StandardError.Trim()}");
                    continue;
                }

                using var document = JsonDocument.Parse(result.StandardOutput);
                var ancestorBindings = new List<GcpBinding>();
                if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                {
                    _ancestorBindingsByScope[scope] = ancestorBindings;
                    continue;
                }
                var expectedResource = $"//cloudresourcemanager.googleapis.com/{scope}";
                foreach (var policyResult in results.EnumerateArray())
                {
                    if (!policyResult.TryGetProperty("resource", out var resource) || resource.GetString() != expectedResource) continue;
                    if (policyResult.TryGetProperty("policy", out var policy) && policy.ValueKind == JsonValueKind.Object)
                    {
                        ancestorBindings.AddRange(GcpIamPolicyMapper.MapBindings(policy, scope, isInherited: true));
                    }
                }
                _ancestorBindingsByScope[scope] = ancestorBindings;
                bindings.AddRange(ancestorBindings);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _providerMessages.Add($"GCP inherited IAM policy discovery for project {project} was incomplete; direct project bindings were retained: {exception.Message}");
        }

        await AddAuditChangeTimesAsync(project, bindings, cancellationToken);
        var normalized = bindings.DistinctBy(binding => (binding.Member, binding.Role, binding.Scope, binding.ConditionExpression)).ToArray();
        _bindingsByAccount[accountId] = normalized;
        return normalized;
    }

    private async Task AddAuditChangeTimesAsync(string project, List<GcpBinding> bindings, CancellationToken cancellationToken)
    {
        var since = DateTime.UtcNow.AddDays(-90).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var filter = $"protoPayload.methodName=\"SetIamPolicy\" AND timestamp >= \"{since}\"";
        var result = await RunGcloudAsync(new[] { "logging", "read", filter, $"--project={project}", "--limit=100", "--format=json" }, cancellationToken);
        if (!result.IsSuccess)
        {
            _providerMessages.Add($"GCP project IAM change history unavailable for {project}: {result.StandardError.Trim()}");
            return;
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return;
        var latestChanges = new Dictionary<(string Member, string Role), DateTime>();
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("timestamp", out var timestamp)
                || !DateTime.TryParse(timestamp.GetString(), out var changedAt)
                || !entry.TryGetProperty("protoPayload", out var payload)
                || !payload.TryGetProperty("request", out var request)
                || !request.TryGetProperty("policy", out var policy)
                || !policy.TryGetProperty("bindings", out var policyBindings)
                || policyBindings.ValueKind != JsonValueKind.Array) continue;

            foreach (var binding in policyBindings.EnumerateArray())
            {
                var role = binding.TryGetProperty("role", out var roleElement) ? roleElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(role) || !binding.TryGetProperty("members", out var members) || members.ValueKind != JsonValueKind.Array) continue;
                foreach (var member in members.EnumerateArray())
                {
                    if (member.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(member.GetString())) continue;
                    var key = (member.GetString()!, role);
                    if (!latestChanges.TryGetValue(key, out var existing) || changedAt > existing) latestChanges[key] = changedAt.ToUniversalTime();
                }
            }
        }

        for (var index = 0; index < bindings.Count; index++)
        {
            var binding = bindings[index];
            if (binding.Scope != $"projects/{project}" || !latestChanges.TryGetValue((binding.Member, binding.Role), out var changedAt)) continue;
            bindings[index] = binding with { LastChangedAt = changedAt };
        }
    }

    private async Task<IReadOnlyList<GcpResourceAncestor>> GetProjectAncestorsAsync(string project, CancellationToken cancellationToken)
    {
        if (_projectAncestors.TryGetValue(project, out var cachedAncestors)) return cachedAncestors;
        var result = await RunGcloudAsync(new[] { "projects", "get-ancestors", project, "--format=json" }, cancellationToken);
        if (!result.IsSuccess) throw new InvalidOperationException(result.StandardError.Trim());
        using var document = JsonDocument.Parse(result.StandardOutput);
        var ancestors = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray()
                .Select(item => new GcpResourceAncestor(
                    item.TryGetProperty("type", out var type) ? (type.GetString() ?? string.Empty).ToLowerInvariant() : string.Empty,
                    item.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty))
                .Where(item => !string.IsNullOrWhiteSpace(item.Type) && !string.IsNullOrWhiteSpace(item.Id))
                .ToArray()
            : Array.Empty<GcpResourceAncestor>();
        _projectAncestors[project] = ancestors;
        return ancestors;
    }

    private static string? GetParentHierarchyId(IReadOnlyList<GcpResourceAncestor> ancestors, GcpResourceAncestor item)
    {
        var index = Array.IndexOf(ancestors.ToArray(), item);
        var parent = ancestors.Skip(index + 1).FirstOrDefault(ancestor => ancestor.Type is "folder" or "organization");
        return parent is null ? null : $"{parent.Type}s/{parent.Id}";
    }
    private static CloudResource MapResourceSearchResult(Guid accountId, JsonElement item, string project)
    {
        var name = item.TryGetProperty("displayName", out var displayName) ? displayName.GetString() : null;
        var resourceName = item.TryGetProperty("name", out var fullName) ? fullName.GetString() : null;
        var resource = new CloudResource
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            ExternalId = resourceName ?? $"projects/{project}",
            Name = string.IsNullOrWhiteSpace(name) ? resourceName ?? project : name,
            Type = item.TryGetProperty("assetType", out var assetType) ? assetType.GetString() ?? "GCP resource" : "GCP resource",
            ResourceType = ResourceType.Unknown,
            Scope = $"projects/{project}",
            ParentResourceId = item.TryGetProperty("parentFullResourceName", out var parent) ? parent.GetString() : null,
            Location = item.TryGetProperty("location", out var location) ? location.GetString() : null
        };
        if (item.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Object)
        {
            foreach (var label in labels.EnumerateObject()) resource.Tags[label.Name] = label.Value.GetString() ?? string.Empty;
        }
        if (item.TryGetProperty("createTime", out var createTime) && DateTime.TryParse(createTime.GetString(), out var createdAt)) resource.CreatedAt = createdAt;
        if (item.TryGetProperty("updateTime", out var updateTime) && DateTime.TryParse(updateTime.GetString(), out var updatedAt)) resource.UpdatedAt = updatedAt;
        return resource;
    }
    private string GetProject(Guid accountId) => _accounts.TryGetValue(accountId, out var account) && !string.IsNullOrWhiteSpace(account.ProjectId) ? account.ProjectId : throw new InvalidOperationException("The GCP account must be discovered before its IAM policy can be loaded.");
    private static IEnumerable<(string Role, string Member)> GetBindings(JsonDocument document) => document.RootElement.GetProperty("bindings").EnumerateArray().SelectMany(binding => binding.GetProperty("members").EnumerateArray().Select(member => (binding.GetProperty("role").GetString() ?? "unknown-role", member.GetString() ?? "unknown-member")));
    private async Task<string> GetGcloudOutputAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await RunGcloudAsync(arguments, cancellationToken);
        if (!result.IsSuccess) throw new InvalidOperationException($"gcloud command failed: {result.StandardError}");
        return result.StandardOutput;
    }
    private Task<CloudCommandResult> RunGcloudAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken) => RunGcloudWithRetryAsync(arguments, cancellationToken);

    public async Task<GcpScanResult> ScanAsync(Guid? accountId = null, CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;
        var accounts = new List<CloudAccount>();
        var resources = new List<CloudResource>();
        var principals = new List<CloudPrincipal>();
        var groups = new List<CloudGroup>();
        var roles = new List<CloudRole>();
        var policies = new List<CloudPolicy>();
        var permissions = new List<CloudPermission>();
        var entitlements = new List<Entitlement>();
        var bindings = new List<GcpBinding>();
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
                    principals.AddRange(await DiscoverServiceAccountsAsync(account.Id, cancellationToken));
                    groups.AddRange(await DiscoverGroupsAsync(account.Id, cancellationToken));
                    roles.AddRange(await DiscoverRolesAsync(account.Id, cancellationToken));
                    var accountBindings = await GetEffectiveBindingsAsync(account.Id, cancellationToken);
                    bindings.AddRange(accountBindings);
                    policies.AddRange(accountBindings.Select(binding => GcpIamPolicyMapper.ToPolicy(account.Id, binding, GetProject(account.Id))));
                    permissions.AddRange(accountBindings.Select(binding => GcpIamPolicyMapper.ToPermission(account.Id, binding, GetProject(account.Id))));
                    entitlements.AddRange(accountBindings.Select((binding, index) => new Entitlement { Id = Guid.NewGuid(), PrincipalId = principals.FirstOrDefault(principal => principal.PrincipalId == binding.Member)?.Id ?? Guid.Empty, PolicyId = policies.ElementAtOrDefault(index)?.Id, PermissionId = permissions.ElementAtOrDefault(index)?.Id ?? Guid.Empty, CloudAccountId = account.Id, Scope = binding.Scope ?? $"projects/{GetProject(account.Id)}", AssignmentType = binding.IsInherited ? AssignmentType.Inherited : binding.ConditionExpression == null ? AssignmentType.Direct : AssignmentType.Policy, GrantedAt = startedAt, Source = binding.IsInherited ? $"Inherited GCP IAM binding from {binding.Scope}" : binding.ConditionExpression == null ? "GCP IAM binding" : $"GCP IAM condition: {binding.ConditionExpression}" }));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    errors.Add($"{account.Name}: {exception.Message}");
                }
            }
            errors.AddRange(_providerMessages);
            _providerMessages.Clear();
            var scan = new Scan { Id = Guid.NewGuid(), CloudAccountId = accounts.FirstOrDefault()?.Id ?? Guid.Empty, StartedAt = startedAt, CompletedAt = DateTime.UtcNow, Status = errors.Count == accounts.Count && accounts.Count > 0 ? ScanStatus.Failed : ScanStatus.Completed, ItemsDiscovered = resources.Count, IdentitiesDiscovered = principals.Count, PermissionsDiscovered = permissions.Count, ErrorMessage = errors.Count == 0 ? null : string.Join(" | ", errors) };
            return new GcpScanResult(scan, accounts, resources, principals, groups, roles, policies, permissions, entitlements, bindings, errors);
        }
        catch (OperationCanceledException)
        {
            return new GcpScanResult(new Scan { Id = Guid.NewGuid(), CloudAccountId = accounts.FirstOrDefault()?.Id ?? Guid.Empty, StartedAt = startedAt, CompletedAt = DateTime.UtcNow, Status = ScanStatus.Cancelled, ErrorMessage = "GCP scan cancelled." }, accounts, resources, principals, groups, roles, policies, permissions, entitlements, bindings, errors);
        }
    }

    private async Task<CloudCommandResult> RunGcloudWithRetryAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        CloudCommandResult? result = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = await _commandRunner!.RunAsync("gcloud", arguments, cancellationToken);
            if (result.IsSuccess || attempt == 2) return result;
            await Task.Delay(TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt)), cancellationToken);
        }
        return result!;
    }
    private static IReadOnlyList<CloudAccount> MockAccounts() => new[] { new CloudAccount { Id = Guid.NewGuid(), Name = "GCP Demo Project", CloudProvider = CloudProviderType.Gcp, ProjectId = "demo-project-123", ExternalAccountId = "gcp-demo", ConnectionStatus = ConnectionStatus.Connected, IsReadOnly = true } };
    private static IReadOnlyList<CloudPrincipal> MockIdentities(Guid accountId) => new[] { new CloudPrincipal { Id = Guid.NewGuid(), AccountId = accountId, CloudProvider = CloudProviderType.Gcp, Name = "project-owner", IdentityType = IdentityType.ServiceAccount, IsPrivileged = true, IsActive = true } };
    private static IReadOnlyList<CloudPermission> MockPermissions(Guid accountId) => new[] { new CloudPermission { Id = Guid.NewGuid(), AccountId = accountId, Name = "Owner", Action = "roles/owner", Resource = "projects/demo-project-123", Scope = "projects/demo-project-123", Source = "IAM Policy Binding" } };
    private static IReadOnlyList<CloudRole> MockRoles(Guid accountId) => new[] { new CloudRole { Id = Guid.NewGuid(), AccountId = accountId, Name = "roles/owner", RoleId = "roles/owner", Description = "Full access to the project", Scope = "projects/demo-project-123" } };
}
