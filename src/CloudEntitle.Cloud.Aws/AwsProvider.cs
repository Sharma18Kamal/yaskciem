namespace CloudEntitle.Cloud.Aws;

using System.Text.Json;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;

public class AwsProvider : ICloudProvider, IIdentityProvider, IResourceProvider, IRoleProvider, IPermissionProvider
{
    private readonly ICloudCommandRunner? _commandRunner;
    private readonly bool _useLiveDiscovery;
    private readonly Dictionary<Guid, CloudAccount> _accounts = new();

    public AwsProvider(ICloudCommandRunner? commandRunner = null, bool useLiveDiscovery = false)
    {
        _commandRunner = commandRunner;
        _useLiveDiscovery = useLiveDiscovery && commandRunner is not null;
    }

    public CloudProviderType ProviderType => CloudProviderType.Aws;

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
        => !_useLiveDiscovery || (await RunAwsAsync(new[] { "sts", "get-caller-identity", "--output", "json" }, cancellationToken)).IsSuccess;

    public async Task<IReadOnlyList<CloudAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery) return MockAccounts();
        using var document = JsonDocument.Parse(await GetAwsOutputAsync(new[] { "sts", "get-caller-identity", "--output", "json" }, cancellationToken));
        var root = document.RootElement;
        var account = new CloudAccount
        {
            Id = Guid.NewGuid(),
            Name = root.TryGetProperty("Arn", out var arn) ? arn.GetString() ?? "AWS account" : "AWS account",
            CloudProvider = CloudProviderType.Aws,
            ExternalAccountId = root.GetProperty("Account").GetString(),
            ConnectionStatus = ConnectionStatus.Connected,
            IsReadOnly = true,
            LastScanAt = DateTime.UtcNow
        };
        _accounts[account.Id] = account;
        return new[] { account };
    }

    public async Task<IReadOnlyList<CloudPrincipal>> DiscoverIdentitiesAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery) return MockIdentities(accountId);
        using var document = JsonDocument.Parse(await GetAwsOutputAsync(new[] { "iam", "list-users", "--output", "json" }, cancellationToken));
        return document.RootElement.GetProperty("Users").EnumerateArray().Select(user => new CloudPrincipal
        {
            Id = Guid.NewGuid(), AccountId = accountId, CloudProvider = CloudProviderType.Aws,
            Name = user.GetProperty("UserName").GetString() ?? "unknown-user",
            PrincipalId = user.TryGetProperty("UserId", out var userId) ? userId.GetString() : null,
            IdentityType = IdentityType.User, IsActive = true
        }).ToArray();
    }

    public async Task<IReadOnlyList<CloudPermission>> DiscoverPermissionsAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery) return MockPermissions(accountId);
        using var document = JsonDocument.Parse(await GetAwsOutputAsync(new[] { "iam", "list-policies", "--scope", "Local", "--only-attached", "--output", "json" }, cancellationToken));
        return document.RootElement.GetProperty("Policies").EnumerateArray().Select(policy => new CloudPermission
        {
            Id = Guid.NewGuid(), AccountId = accountId,
            Name = policy.GetProperty("PolicyName").GetString() ?? "Unknown policy",
            Action = policy.TryGetProperty("PolicyArn", out var arn) ? arn.GetString() : null,
            Resource = policy.TryGetProperty("PolicyArn", out arn) ? arn.GetString() : null,
            Scope = "AWS account", Source = "AWS IAM managed policy"
        }).ToArray();
    }

    public async Task<IReadOnlyList<CloudRole>> DiscoverRolesAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery) return MockRoles(accountId);
        using var document = JsonDocument.Parse(await GetAwsOutputAsync(new[] { "iam", "list-roles", "--output", "json" }, cancellationToken));
        return document.RootElement.GetProperty("Roles").EnumerateArray().Select(role => new CloudRole
        {
            Id = Guid.NewGuid(), AccountId = accountId,
            Name = role.GetProperty("RoleName").GetString() ?? "Unknown role",
            RoleId = role.TryGetProperty("Arn", out var arn) ? arn.GetString() ?? string.Empty : string.Empty,
            Description = role.TryGetProperty("Description", out var description) ? description.GetString() : null,
            Scope = "AWS account"
        }).ToArray();
    }

    public Task<IReadOnlyList<CloudPrincipal>> GetPrincipalsAsync(Guid accountId, CancellationToken cancellationToken = default) => DiscoverIdentitiesAsync(accountId, cancellationToken);
    public Task<IReadOnlyList<CloudPermission>> GetPermissionsAsync(Guid accountId, CancellationToken cancellationToken = default) => DiscoverPermissionsAsync(accountId, cancellationToken);
    public Task<IReadOnlyList<CloudRole>> GetRolesAsync(Guid accountId, CancellationToken cancellationToken = default) => DiscoverRolesAsync(accountId, cancellationToken);
    public Task<IReadOnlyList<CloudResource>> GetResourcesAsync(Guid accountId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CloudResource>>(Array.Empty<CloudResource>());

    private async Task<string> GetAwsOutputAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await RunAwsAsync(arguments, cancellationToken);
        if (!result.IsSuccess) throw new InvalidOperationException($"AWS CLI command failed: {result.StandardError}");
        return result.StandardOutput;
    }

    private Task<CloudCommandResult> RunAwsAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        => _commandRunner!.RunAsync("aws", arguments, cancellationToken);

    private static IReadOnlyList<CloudAccount> MockAccounts() => new[] { new CloudAccount { Id = Guid.NewGuid(), Name = "AWS Demo Account", CloudProvider = CloudProviderType.Aws, ExternalAccountId = "123456789012", ConnectionStatus = ConnectionStatus.Connected, IsReadOnly = true } };
    private static IReadOnlyList<CloudPrincipal> MockIdentities(Guid accountId) => new[] { new CloudPrincipal { Id = Guid.NewGuid(), AccountId = accountId, CloudProvider = CloudProviderType.Aws, Name = "admin-user", IdentityType = IdentityType.User, IsPrivileged = true, IsActive = true } };
    private static IReadOnlyList<CloudPermission> MockPermissions(Guid accountId) => new[] { new CloudPermission { Id = Guid.NewGuid(), AccountId = accountId, Name = "AdministratorAccess", Action = "*", Resource = "*", Scope = "account:123456789012", Source = "AWS IAM Managed Policy" } };
    private static IReadOnlyList<CloudRole> MockRoles(Guid accountId) => new[] { new CloudRole { Id = Guid.NewGuid(), AccountId = accountId, Name = "AdministratorAccess", RoleId = "arn:aws:iam::123456789012:role/Admin", Description = "Full administrative access", Scope = "account:123456789012" } };
}
