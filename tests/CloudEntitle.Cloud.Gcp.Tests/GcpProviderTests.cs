using CloudEntitle.Cloud.Gcp;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;
using Xunit;

namespace CloudEntitle.Cloud.Gcp.Tests;

public class GcpProviderTests
{
    [Fact]
    public async Task GcpProvider_ShouldDiscoverConnectedAccount()
    {
        var provider = new GcpProvider();

        var accounts = await provider.DiscoverAccountsAsync();

        Assert.NotEmpty(accounts);
        Assert.Equal(CloudProviderType.Gcp, accounts[0].CloudProvider);
        Assert.True(accounts[0].IsReadOnly);
    }

    [Fact]
    public async Task GcpProvider_ShouldExposeOwnerRole()
    {
        var provider = new GcpProvider();

        var roles = await provider.DiscoverRolesAsync(Guid.NewGuid());

        Assert.NotEmpty(roles);
        Assert.Equal("roles/owner", roles[0].Name);
    }

    [Fact]
    public async Task GcpProvider_ShouldNormalizeReadOnlyScan()
    {
        var provider = new GcpProvider(new FakeGcloudRunner(), useLiveDiscovery: true);

        var result = await provider.ScanAsync();

        Assert.Equal(ScanStatus.Completed, result.Scan.Status);
        Assert.NotEmpty(result.Accounts);
        Assert.NotEmpty(result.Resources);
        Assert.NotEmpty(result.Principals);
        Assert.NotEmpty(result.Groups);
        Assert.NotEmpty(result.Policies);
        Assert.NotEmpty(result.Permissions);
        Assert.Contains(result.Bindings, binding => binding.ConditionExpression != null);
        Assert.Contains(result.Bindings, binding => binding.IsInherited && binding.Scope == "folders/456");
        Assert.Contains(result.Entitlements, entitlement => entitlement.AssignmentType == AssignmentType.Inherited && entitlement.Scope == "folders/456");
    }

    [Fact]
    public async Task GcpProvider_ShouldExposeObservedProjectIamBindingsAsEffectiveAccess()
    {
        var provider = new GcpProvider(new FakeGcloudRunner(), useLiveDiscovery: true);
        var account = Assert.Single(await provider.DiscoverAccountsAsync());

        var access = await provider.DiscoverEffectiveAccessAsync(account.Id);

        Assert.Contains(access, assignment => assignment.Principal == "user:demo@example.invalid" && assignment.Role == "roles/owner");
        Assert.Contains(access, assignment => assignment.Principal == "user:demo@example.invalid" && assignment.LastChangedAt.HasValue && assignment.Source.Contains("Cloud Audit Logs", StringComparison.Ordinal));
        Assert.Contains(access, assignment => assignment.Principal == "user:folder@example.invalid" && assignment.Scope == "folders/456" && assignment.AssignmentType == "Inherited");
        Assert.Contains(access, assignment => assignment.Principal == "user:organization@example.invalid" && assignment.Scope == "organizations/789" && assignment.AssignmentType == "Inherited");
    }

    [Fact]
    public async Task GcpProvider_ShouldDiscoverOrganizationAndFolderAncestors()
    {
        var provider = new GcpProvider(new FakeGcloudRunner(), useLiveDiscovery: true);
        await provider.DiscoverAccountsAsync();

        var hierarchy = await provider.DiscoverOrganizationAccountsAsync();

        Assert.Equal(2, hierarchy.Accounts.Count);
        Assert.Contains(hierarchy.Accounts, account => account.AccountType == AccountType.GcpOrganization && account.ExternalAccountId == "organizations/789");
        Assert.Contains(hierarchy.Accounts, account => account.AccountType == AccountType.Folder && account.ParentAccountId == "organizations/789");
    }

    [Fact]
    public async Task GcpProvider_ShouldMapCloudAssetInventoryResourceMetadata()
    {
        var provider = new GcpProvider(new FakeGcloudRunner(), useLiveDiscovery: true);
        var account = Assert.Single(await provider.DiscoverAccountsAsync());

        var resources = await provider.GetResourcesAsync(account.Id);

        var bucket = Assert.Single(resources, resource => resource.Type == "storage.googleapis.com/Bucket");
        Assert.Equal("demo-bucket", bucket.Name);
        Assert.Equal("us-central1", bucket.Location);
        Assert.Equal("production", bucket.Tags["environment"]);
        Assert.Equal("//cloudresourcemanager.googleapis.com/projects/123", bucket.ParentResourceId);
    }

    [Fact]
    public async Task GcpProvider_ShouldRetainDirectBindingsWhenInheritedPolicySearchIsDenied()
    {
        var provider = new GcpProvider(new FakeGcloudRunner(denyInheritedPolicySearch: true), useLiveDiscovery: true);
        var account = Assert.Single(await provider.DiscoverAccountsAsync());

        var access = await provider.DiscoverEffectiveAccessAsync(account.Id);
        var hierarchy = await provider.DiscoverOrganizationAccountsAsync();

        Assert.Contains(access, assignment => assignment.Principal == "user:demo@example.invalid" && assignment.AssignmentType == "Direct");
        Assert.Contains(hierarchy.ProviderMessages, message => message.Contains("inherited IAM policy discovery", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GcpProvider_ShouldHonorCancellation()
    {
        var provider = new GcpProvider(new FakeGcloudRunner(), useLiveDiscovery: true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await provider.ScanAsync(cancellationToken: cancellation.Token);

        Assert.Equal(ScanStatus.Cancelled, result.Scan.Status);
    }

    private sealed class FakeGcloudRunner : ICloudCommandRunner
    {
        private readonly bool _denyInheritedPolicySearch;

        public FakeGcloudRunner(bool denyInheritedPolicySearch = false) => _denyInheritedPolicySearch = denyInheritedPolicySearch;

        public Task<CloudCommandResult> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            if (arguments.Contains("search-all-iam-policies"))
            {
                if (_denyInheritedPolicySearch) return Task.FromResult(new CloudCommandResult(1, string.Empty, "permission denied"));
                var scope = arguments.First(argument => argument.StartsWith("--scope=", StringComparison.Ordinal))["--scope=".Length..];
                var policy = scope.StartsWith("folders/", StringComparison.Ordinal)
                    ? "{\"role\":\"roles/viewer\",\"members\":[\"user:folder@example.invalid\"]}"
                    : "{\"role\":\"roles/editor\",\"members\":[\"user:organization@example.invalid\"]}";
                return Task.FromResult(new CloudCommandResult(0, $"{{\"results\":[{{\"resource\":\"//cloudresourcemanager.googleapis.com/{scope}\",\"policy\":{{\"bindings\":[{policy}]}}}}]}}", string.Empty));
            }

            if (arguments.Contains("logging") && arguments.Contains("read"))
                return Task.FromResult(new CloudCommandResult(0, "[{\"timestamp\":\"2026-10-01T09:00:00Z\",\"protoPayload\":{\"methodName\":\"SetIamPolicy\",\"request\":{\"policy\":{\"bindings\":[{\"role\":\"roles/owner\",\"members\":[\"user:demo@example.invalid\"]}]}}}}]", string.Empty));

            var output = arguments.Contains("projects") && arguments.Contains("list")
                ? "[{\"projectId\":\"demo-production\",\"name\":\"Demo Production\",\"projectNumber\":\"123\"}]"
                : arguments.Contains("get-ancestors")
                    ? "[{\"type\":\"project\",\"id\":\"123\"},{\"type\":\"folder\",\"id\":\"456\"},{\"type\":\"organization\",\"id\":\"789\"}]"
                    : arguments.Contains("asset") && arguments.Contains("search-all-resources")
                        ? "{\"results\":[{\"name\":\"//storage.googleapis.com/projects/demo-production/buckets/demo-bucket\",\"assetType\":\"storage.googleapis.com/Bucket\",\"displayName\":\"demo-bucket\",\"location\":\"us-central1\",\"labels\":{\"environment\":\"production\"},\"parentFullResourceName\":\"//cloudresourcemanager.googleapis.com/projects/123\"}]}"
                : arguments.Contains("service-accounts")
                    ? "[{\"email\":\"demo-sa@demo-production.iam.gserviceaccount.com\",\"uniqueId\":\"sa-123\",\"displayName\":\"Demo Service Account\",\"disabled\":false}]"
                    : "{\"bindings\":[{\"role\":\"roles/owner\",\"members\":[\"user:demo@example.invalid\",\"group:demo@example.invalid\",\"serviceAccount:demo-sa@demo-production.iam.gserviceaccount.com\"],\"condition\":{\"title\":\"DemoCondition\",\"expression\":\"resource.name.startsWith('projects/demo-production')\"}}]}";
            return Task.FromResult(new CloudCommandResult(0, output, string.Empty));
        }
    }
}
