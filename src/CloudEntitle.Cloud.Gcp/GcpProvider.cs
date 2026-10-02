namespace CloudEntitle.Cloud.Gcp;

using System.Text.Json;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Cloud.Gcp.Mappers;
using CloudEntitle.Cloud.Gcp.Services;

public class GcpProvider : ICloudProvider, IIdentityProvider, IResourceProvider, IRoleProvider, IPermissionProvider, ICloudEffectiveAccessProvider
{
    private readonly ICloudCommandRunner? _commandRunner;
    private readonly bool _useLiveDiscovery;
    private readonly Dictionary<Guid, CloudAccount> _accounts = new();

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

    public async Task<IReadOnlyList<CloudPrincipal>> DiscoverIdentitiesAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery) return MockIdentities(accountId);
        return GetBindings(await GetPolicyAsync(accountId, cancellationToken)).Select(binding => new CloudPrincipal
        {
            Id = Guid.NewGuid(), AccountId = accountId, CloudProvider = CloudProviderType.Gcp,
            Name = binding.Member, PrincipalId = binding.Member,
            IdentityType = binding.Member.StartsWith("serviceAccount:", StringComparison.OrdinalIgnoreCase) ? IdentityType.ServiceAccount : IdentityType.User,
            IsPrivileged = binding.Role.Contains("owner", StringComparison.OrdinalIgnoreCase), IsActive = true
        }).ToArray();
    }

    public async Task<IReadOnlyList<CloudPermission>> DiscoverPermissionsAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery) return MockPermissions(accountId);
        var project = GetProject(accountId);
        return GetBindings(await GetPolicyAsync(accountId, cancellationToken)).Select(binding => new CloudPermission
        {
            Id = Guid.NewGuid(), AccountId = accountId, Name = binding.Role, Action = binding.Role,
            Resource = project, Scope = project, Source = "GCP IAM policy"
        }).ToArray();
    }

    public async Task<IReadOnlyList<CloudRole>> DiscoverRolesAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery) return MockRoles(accountId);
        var project = GetProject(accountId);
        return GetBindings(await GetPolicyAsync(accountId, cancellationToken)).GroupBy(binding => binding.Role).Select(group => new CloudRole
        {
            Id = Guid.NewGuid(), AccountId = accountId, Name = group.Key, RoleId = group.Key, Scope = project
        }).ToArray();
    }

    public async Task<IReadOnlyList<CloudEffectiveAccessAssignment>> DiscoverEffectiveAccessAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery)
        {
            return Array.Empty<CloudEffectiveAccessAssignment>();
        }

        var project = GetProject(accountId);
        return GetBindings(await GetPolicyAsync(accountId, cancellationToken))
            .Select(binding => new CloudEffectiveAccessAssignment(
                binding.Member,
                binding.Role,
                $"projects/{project}",
                "GCP project IAM binding",
                binding.Role.Contains("owner", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    public Task<IReadOnlyList<CloudPrincipal>> GetPrincipalsAsync(Guid accountId, CancellationToken cancellationToken = default) => DiscoverIdentitiesAsync(accountId, cancellationToken);
    public Task<IReadOnlyList<CloudPermission>> GetPermissionsAsync(Guid accountId, CancellationToken cancellationToken = default) => DiscoverPermissionsAsync(accountId, cancellationToken);
    public Task<IReadOnlyList<CloudRole>> GetRolesAsync(Guid accountId, CancellationToken cancellationToken = default) => DiscoverRolesAsync(accountId, cancellationToken);
    public async Task<IReadOnlyList<CloudResource>> GetResourcesAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var project = GetProject(accountId);
        return await Task.FromResult<IReadOnlyList<CloudResource>>(new[] { new CloudResource { Id = Guid.NewGuid(), AccountId = accountId, ExternalId = $"projects/{project}", Name = project, Type = "cloudresourcemanager.googleapis.com/Project", ResourceType = ResourceType.Project, Scope = $"projects/{project}", Location = "global" } });
    }

    public async Task<IReadOnlyList<CloudGroup>> DiscoverGroupsAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var bindings = GetBindings(await GetPolicyAsync(accountId, cancellationToken));
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
                    var policy = await GetPolicyAsync(account.Id, cancellationToken);
                    var accountBindings = GcpIamPolicyMapper.MapBindings(policy.RootElement);
                    bindings.AddRange(accountBindings);
                    policies.AddRange(accountBindings.Select(binding => GcpIamPolicyMapper.ToPolicy(account.Id, binding, GetProject(account.Id))));
                    permissions.AddRange(accountBindings.Select(binding => GcpIamPolicyMapper.ToPermission(account.Id, binding, GetProject(account.Id))));
                    entitlements.AddRange(accountBindings.Select((binding, index) => new Entitlement { Id = Guid.NewGuid(), PrincipalId = principals.FirstOrDefault(principal => principal.PrincipalId == binding.Member)?.Id ?? Guid.Empty, PolicyId = policies.ElementAtOrDefault(index)?.Id, PermissionId = permissions.ElementAtOrDefault(index)?.Id ?? Guid.Empty, CloudAccountId = account.Id, Scope = $"projects/{GetProject(account.Id)}", AssignmentType = binding.ConditionExpression == null ? AssignmentType.Direct : AssignmentType.Policy, GrantedAt = startedAt, Source = binding.ConditionExpression == null ? "GCP IAM binding" : $"GCP IAM condition: {binding.ConditionExpression}" }));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    errors.Add($"{account.Name}: {exception.Message}");
                }
            }
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
