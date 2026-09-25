namespace CloudEntitle.Cloud.Gcp;

using System.Text.Json;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;

public class GcpProvider : ICloudProvider, IIdentityProvider, IResourceProvider, IRoleProvider, IPermissionProvider
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

    public Task<IReadOnlyList<CloudPrincipal>> GetPrincipalsAsync(Guid accountId, CancellationToken cancellationToken = default) => DiscoverIdentitiesAsync(accountId, cancellationToken);
    public Task<IReadOnlyList<CloudPermission>> GetPermissionsAsync(Guid accountId, CancellationToken cancellationToken = default) => DiscoverPermissionsAsync(accountId, cancellationToken);
    public Task<IReadOnlyList<CloudRole>> GetRolesAsync(Guid accountId, CancellationToken cancellationToken = default) => DiscoverRolesAsync(accountId, cancellationToken);
    public Task<IReadOnlyList<CloudResource>> GetResourcesAsync(Guid accountId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CloudResource>>(Array.Empty<CloudResource>());

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
    private Task<CloudCommandResult> RunGcloudAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken) => _commandRunner!.RunAsync("gcloud", arguments, cancellationToken);
    private static IReadOnlyList<CloudAccount> MockAccounts() => new[] { new CloudAccount { Id = Guid.NewGuid(), Name = "GCP Demo Project", CloudProvider = CloudProviderType.Gcp, ProjectId = "demo-project-123", ExternalAccountId = "gcp-demo", ConnectionStatus = ConnectionStatus.Connected, IsReadOnly = true } };
    private static IReadOnlyList<CloudPrincipal> MockIdentities(Guid accountId) => new[] { new CloudPrincipal { Id = Guid.NewGuid(), AccountId = accountId, CloudProvider = CloudProviderType.Gcp, Name = "project-owner", IdentityType = IdentityType.ServiceAccount, IsPrivileged = true, IsActive = true } };
    private static IReadOnlyList<CloudPermission> MockPermissions(Guid accountId) => new[] { new CloudPermission { Id = Guid.NewGuid(), AccountId = accountId, Name = "Owner", Action = "roles/owner", Resource = "projects/demo-project-123", Scope = "projects/demo-project-123", Source = "IAM Policy Binding" } };
    private static IReadOnlyList<CloudRole> MockRoles(Guid accountId) => new[] { new CloudRole { Id = Guid.NewGuid(), AccountId = accountId, Name = "roles/owner", RoleId = "roles/owner", Description = "Full access to the project", Scope = "projects/demo-project-123" } };
}
