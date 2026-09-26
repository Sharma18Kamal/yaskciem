namespace CloudEntitle.Cloud.Aws;

using System.Text.Json;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Cloud.Aws.Mappers;
using CloudEntitle.Cloud.Aws.Services;

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

    public async Task<IReadOnlyList<CloudGroup>> DiscoverGroupsAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery) return new[] { new CloudGroup { Id = Guid.NewGuid(), AccountId = accountId, Name = "AWS Demo Security Group", GroupId = "demo-group" } };
        using var document = JsonDocument.Parse(await GetAwsOutputAsync(new[] { "iam", "list-groups", "--output", "json" }, cancellationToken));
        return document.RootElement.GetProperty("Groups").EnumerateArray().Select(group => new CloudGroup
        {
            Id = Guid.NewGuid(), AccountId = accountId, Name = group.TryGetProperty("GroupName", out var name) ? name.GetString() ?? "AWS group" : "AWS group", GroupId = group.TryGetProperty("GroupId", out var id) ? id.GetString() : null, ProviderPath = group.TryGetProperty("Path", out var path) ? path.GetString() : null
        }).ToArray();
    }

    public async Task<IReadOnlyList<CloudPolicy>> DiscoverPoliciesAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery) return new[] { new CloudPolicy { Id = Guid.NewGuid(), AccountId = accountId, Name = "AWS Demo Policy", ExternalId = "demo-policy", PolicyType = "CustomerManaged", Document = "{\"Version\":\"2012-10-17\",\"Statement\":[{\"Effect\":\"Allow\",\"Action\":\"*\",\"Resource\":\"*\"}]}" } };
        using var document = JsonDocument.Parse(await GetAwsOutputAsync(new[] { "iam", "list-policies", "--scope", "Local", "--output", "json" }, cancellationToken));
        return document.RootElement.GetProperty("Policies").EnumerateArray().Select(policy => new CloudPolicy
        {
            Id = Guid.NewGuid(), AccountId = accountId, Name = policy.TryGetProperty("PolicyName", out var name) ? name.GetString() ?? "AWS policy" : "AWS policy", ExternalId = policy.TryGetProperty("Arn", out var arn) ? arn.GetString() : null, PolicyType = policy.TryGetProperty("PolicyType", out var type) ? type.GetString() : "CustomerManaged", Document = policy.TryGetProperty("Document", out var document) ? document.GetString() : null, DocumentVersion = policy.TryGetProperty("DefaultVersionId", out var version) ? version.GetString() : null, IsManaged = true
        }).ToArray();
    }

    private async Task<string> GetAwsOutputAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await RunAwsAsync(arguments, cancellationToken);
        if (!result.IsSuccess) throw new InvalidOperationException($"AWS CLI command failed: {result.StandardError}");
        return result.StandardOutput;
    }

    private Task<CloudCommandResult> RunAwsAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        => RunAwsWithRetryAsync(arguments, cancellationToken);

    public async Task<AwsScanResult> ScanAsync(Guid? accountId = null, CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;
        var accounts = new List<CloudAccount>();
        var principals = new List<CloudPrincipal>();
        var groups = new List<CloudGroup>();
        var roles = new List<CloudRole>();
        var policies = new List<CloudPolicy>();
        var permissions = new List<CloudPermission>();
        var entitlements = new List<Entitlement>();
        var trusts = new List<AwsTrustRelationship>();
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
                    principals.AddRange(await DiscoverIdentitiesAsync(account.Id, cancellationToken));
                    groups.AddRange(await DiscoverGroupsAsync(account.Id, cancellationToken));
                    roles.AddRange(await DiscoverRolesAsync(account.Id, cancellationToken));
                    policies.AddRange(await DiscoverPoliciesAsync(account.Id, cancellationToken));
                    foreach (var policy in policies.Where(policy => policy.AccountId == account.Id && !string.IsNullOrWhiteSpace(policy.Document) && policy.Document!.TrimStart().StartsWith("{"))) permissions.AddRange(AwsPolicyMapper.MapPolicy(account.Id, policy.Name, policy.ExternalId, policy.Document!));
                    foreach (var role in roles.Where(role => role.AccountId == account.Id))
                    {
                        var trust = await GetRoleTrustPolicyAsync(account, role, cancellationToken);
                        trusts.AddRange(AwsPolicyMapper.MapTrustPrincipals(trust).Select(principal => new AwsTrustRelationship(principals.FirstOrDefault(item => item.PrincipalId == principal)?.Id ?? Guid.Empty, role.Id, principal, "AWS IAM AssumeRole trust policy")));
                    }
                    entitlements.AddRange(permissions.Where(permission => permission.AccountId == account.Id).Select(permission => new Entitlement { Id = Guid.NewGuid(), PermissionId = permission.Id, CloudAccountId = account.Id, AssignmentType = AssignmentType.Policy, GrantedAt = startedAt, Source = "AWS IAM policy" }));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    errors.Add($"{account.Name}: {exception.Message}");
                }
            }

            var scan = new Scan { Id = Guid.NewGuid(), CloudAccountId = accounts.FirstOrDefault()?.Id ?? Guid.Empty, StartedAt = startedAt, CompletedAt = DateTime.UtcNow, Status = errors.Count == accounts.Count && accounts.Count > 0 ? ScanStatus.Failed : ScanStatus.Completed, ItemsDiscovered = policies.Count + groups.Count, IdentitiesDiscovered = principals.Count, PermissionsDiscovered = permissions.Count, ErrorMessage = errors.Count == 0 ? null : string.Join(" | ", errors) };
            return new AwsScanResult(scan, accounts, principals, groups, roles, policies, permissions, entitlements, trusts, errors);
        }
        catch (OperationCanceledException)
        {
            return new AwsScanResult(new Scan { Id = Guid.NewGuid(), CloudAccountId = accounts.FirstOrDefault()?.Id ?? Guid.Empty, StartedAt = startedAt, CompletedAt = DateTime.UtcNow, Status = ScanStatus.Cancelled, ErrorMessage = "AWS scan cancelled." }, accounts, principals, groups, roles, policies, permissions, entitlements, trusts, errors);
        }
    }

    private async Task<string> GetRoleTrustPolicyAsync(CloudAccount account, CloudRole role, CancellationToken cancellationToken)
    {
        var result = await GetAwsOutputAsync(new[] { "iam", "get-role", "--role-name", role.Name, "--output", "json" }, cancellationToken);
        using var document = JsonDocument.Parse(result);
        return document.RootElement.TryGetProperty("Role", out var roleElement) && roleElement.TryGetProperty("AssumeRolePolicyDocument", out var policy) ? policy.GetString() ?? "{}" : "{}";
    }

    private async Task<CloudCommandResult> RunAwsWithRetryAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        CloudCommandResult? result = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = await _commandRunner!.RunAsync("aws", arguments, cancellationToken);
            if (result.IsSuccess || attempt == 2) return result;
            await Task.Delay(TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt)), cancellationToken);
        }
        return result!;
    }

    private static IReadOnlyList<CloudAccount> MockAccounts() => new[] { new CloudAccount { Id = Guid.NewGuid(), Name = "AWS Demo Account", CloudProvider = CloudProviderType.Aws, ExternalAccountId = "123456789012", ConnectionStatus = ConnectionStatus.Connected, IsReadOnly = true } };
    private static IReadOnlyList<CloudPrincipal> MockIdentities(Guid accountId) => new[] { new CloudPrincipal { Id = Guid.NewGuid(), AccountId = accountId, CloudProvider = CloudProviderType.Aws, Name = "admin-user", IdentityType = IdentityType.User, IsPrivileged = true, IsActive = true } };
    private static IReadOnlyList<CloudPermission> MockPermissions(Guid accountId) => new[] { new CloudPermission { Id = Guid.NewGuid(), AccountId = accountId, Name = "AdministratorAccess", Action = "*", Resource = "*", Scope = "account:123456789012", Source = "AWS IAM Managed Policy" } };
    private static IReadOnlyList<CloudRole> MockRoles(Guid accountId) => new[] { new CloudRole { Id = Guid.NewGuid(), AccountId = accountId, Name = "AdministratorAccess", RoleId = "arn:aws:iam::123456789012:role/Admin", Description = "Full administrative access", Scope = "account:123456789012" } };
}
