using CloudEntitle.Cloud.Azure;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;
using Xunit;

namespace CloudEntitle.Cloud.Azure.Tests;

public class AzureProviderTests
{
    [Fact]
    public async Task AzureProvider_ShouldDiscoverConnectedAccount()
    {
        var provider = new AzureProvider();

        var accounts = await provider.DiscoverAccountsAsync();

        Assert.NotEmpty(accounts);
        Assert.Equal(CloudProviderType.Azure, accounts[0].CloudProvider);
        Assert.True(accounts[0].IsReadOnly);
    }

    [Fact]
    public async Task AzureProvider_ShouldExposePrivilegedIdentity()
    {
        var provider = new AzureProvider();

        var identities = await provider.DiscoverIdentitiesAsync(Guid.NewGuid());

        Assert.NotEmpty(identities);
        Assert.True(identities[0].IsPrivileged);
    }

    [Fact]
    public async Task AzureProvider_ShouldMapReadOnlyCliJson()
    {
        var runner = new FakeCommandRunner();
        var provider = new AzureProvider(runner, useLiveDiscovery: true);

        var accounts = await provider.DiscoverAccountsAsync();
        var permissions = await provider.DiscoverPermissionsAsync(accounts[0].Id);

        Assert.Equal("subscription-001", accounts[0].SubscriptionId);
        Assert.Equal("Owner", permissions[0].Name);
        Assert.Equal("/subscriptions/subscription-001", permissions[0].Scope);
    }

    [Fact]
    public async Task AzureProvider_ShouldDiscoverEveryEnabledCliSubscription()
    {
        var provider = new AzureProvider(new FakeCommandRunner(multipleSubscriptions: true), useLiveDiscovery: true);

        var accounts = await provider.DiscoverAccountsAsync();

        Assert.Equal(2, accounts.Count);
        Assert.Contains(accounts, account => account.SubscriptionId == "subscription-001");
        Assert.Contains(accounts, account => account.SubscriptionId == "subscription-002");
        Assert.All(accounts, account => Assert.True(account.IsReadOnly));
    }

    [Fact]
    public async Task AzureProvider_ShouldExposeObservedRoleAssignmentsAsEffectiveAccess()
    {
        var provider = new AzureProvider(new FakeCommandRunner(), useLiveDiscovery: true);
        var account = Assert.Single(await provider.DiscoverAccountsAsync());

        var access = await provider.DiscoverEffectiveAccessAsync(account.Id);

        var assignment = Assert.Single(access);
        Assert.Equal("owner@example.com", assignment.Principal);
        Assert.Equal("Owner", assignment.Role);
        Assert.True(assignment.IsPrivileged);
        Assert.True(assignment.LastChangedAt.HasValue);
        Assert.Contains("Activity Log", assignment.Source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AzureProvider_ShouldReportWhenActivityLogAccessIsDenied()
    {
        var provider = new AzureProvider(new FakeCommandRunner(denyActivityLog: true), useLiveDiscovery: true);
        var account = Assert.Single(await provider.DiscoverAccountsAsync());

        await provider.DiscoverEffectiveAccessAsync(account.Id);

        Assert.Contains(((ICloudProviderDiagnostics)provider).DrainProviderMessages(), message => message.Contains("change history", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AzureProvider_ShouldDiscoverEntraDirectoryAndActiveAndEligiblePim()
    {
        var provider = new AzureProvider(new FakeCommandRunner(), useLiveDiscovery: true);
        var account = Assert.Single(await provider.DiscoverAccountsAsync());

        var result = await provider.DiscoverEntraDirectoryAsync(account.Id);

        Assert.Contains(result.Principals, principal => principal.Name == "Entra User" && principal.IdentityType == IdentityType.User);
        Assert.Contains(result.Principals, principal => principal.Name == "Security Group" && principal.IdentityType == IdentityType.Group);
        Assert.Contains(result.Principals, principal => principal.Name == "Workload Identity" && principal.IdentityType == IdentityType.ManagedIdentity);
        Assert.Contains(result.AccessAssignments, assignment => assignment.AssignmentType == "Active (PIM)" && assignment.Role == "Global Administrator");
        Assert.Contains(result.AccessAssignments, assignment => assignment.AssignmentType == "Eligible (PIM)" && assignment.Role == "Security Administrator");
    }

    [Fact]
    public async Task AzureProvider_ShouldExposePartialEntraResultsWhenPimConsentIsMissing()
    {
        var provider = new AzureProvider(new FakeCommandRunner(denyPim: true), useLiveDiscovery: true);
        var account = Assert.Single(await provider.DiscoverAccountsAsync());

        var result = await provider.DiscoverEntraDirectoryAsync(account.Id);

        Assert.NotEmpty(result.Principals);
        Assert.Empty(result.AccessAssignments);
        Assert.Contains(result.ProviderMessages, message => message.Contains("PIM", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.ProviderMessages, message => message.Contains("RoleAssignmentSchedule.Read.Directory", StringComparison.Ordinal) && message.Contains("admin consent", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.ProviderMessages, message => message.Contains("RoleEligibilitySchedule.Read.Directory", StringComparison.Ordinal) && message.Contains("admin consent", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.ProviderMessages, message => message.Contains("PermissionScopeNotGranted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AzureProvider_ShouldReturnNormalizedCompletedScan()
    {
        var provider = new AzureProvider(new FakeCommandRunner(), useLiveDiscovery: true);

        var result = await provider.ScanAsync();

        Assert.Equal(ScanStatus.Completed, result.Scan.Status);
        Assert.NotEmpty(result.Accounts);
        Assert.NotEmpty(result.Resources);
        Assert.NotEmpty(result.Principals);
        Assert.NotEmpty(result.RoleAssignments);
        Assert.NotEmpty(result.Entitlements);
    }

    [Fact]
    public async Task AzureProvider_ShouldPreserveResourceTagsForSecuritySignalMapping()
    {
        var provider = new AzureProvider(new FakeCommandRunner(), useLiveDiscovery: true);
        var account = Assert.Single(await provider.DiscoverAccountsAsync());

        var resource = Assert.Single(await provider.GetResourcesAsync(account.Id));

        Assert.Equal("true", resource.Tags["PubliclyAccessible"]);
        Assert.Equal("confidential", resource.Tags["DataClassification"]);
        Assert.Equal("Enabled", resource.Tags["PublicNetworkAccess"]);
        Assert.Equal("Allow", resource.Tags["NetworkDefaultAction"]);
    }

    [Fact]
    public async Task AzureProvider_ShouldHonorCancellation()
    {
        var provider = new AzureProvider(new FakeCommandRunner(), useLiveDiscovery: true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await provider.ScanAsync(cancellationToken: cancellation.Token);

        Assert.Equal(ScanStatus.Cancelled, result.Scan.Status);
    }

    private sealed class FakeCommandRunner(bool denyPim = false, bool denyActivityLog = false, bool multipleSubscriptions = false) : ICloudCommandRunner
    {
        public Task<CloudCommandResult> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            if (arguments[0] == "resource" && arguments.Contains("list"))
                return Task.FromResult(new CloudCommandResult(0, "[{\"id\":\"/subscriptions/subscription-001/resourceGroups/rg-prod/providers/Microsoft.Storage/storageAccounts/data\",\"name\":\"data\",\"type\":\"Microsoft.Storage/storageAccounts\",\"tags\":{\"PubliclyAccessible\":\"true\",\"DataClassification\":\"confidential\"},\"properties\":{\"publicNetworkAccess\":\"Enabled\",\"allowBlobPublicAccess\":true,\"networkAcls\":{\"defaultAction\":\"Allow\"}}}]", string.Empty));
            if (arguments.Contains("activity-log"))
            {
                if (denyActivityLog) return Task.FromResult(new CloudCommandResult(1, string.Empty, "Authorization failed."));
                var responseBody = System.Text.Json.JsonSerializer.Serialize(new
                {
                    properties = new { principalId = "principal-001", roleDefinitionId = "role-001", scope = "/subscriptions/subscription-001" }
                });
                var activityLog = System.Text.Json.JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        eventTimestamp = "2026-10-01T08:00:00Z",
                        authorization = new { action = "Microsoft.Authorization/roleAssignments/write", scope = "/subscriptions/subscription-001" },
                        properties = new { responseBody }
                    }
                });
                return Task.FromResult(new CloudCommandResult(0, activityLog, string.Empty));
            }

            if (arguments.Contains("rest"))
            {
                var urlIndex = Array.IndexOf(arguments.ToArray(), "--url");
                var url = urlIndex >= 0 && urlIndex + 1 < arguments.Count ? arguments[urlIndex + 1] : string.Empty;
                if (denyPim && url.Contains("roleManagement", StringComparison.Ordinal))
                {
                    return Task.FromResult(new CloudCommandResult(1, string.Empty, "ERROR: Forbidden({\"error\":{\"code\":\"UnknownError\",\"message\":\"PermissionScopeNotGranted: missing RoleAssignmentSchedule.Read.Directory and RoleEligibilitySchedule.Read.Directory\"}})"));
                }
                var graphOutput = url.Contains("/users?", StringComparison.Ordinal)
                    ? "{\"value\":[{\"id\":\"user-1\",\"displayName\":\"Entra User\",\"userPrincipalName\":\"entra@example.com\",\"accountEnabled\":true}]}"
                    : url.Contains("/groups?", StringComparison.Ordinal)
                        ? "{\"value\":[{\"id\":\"group-1\",\"displayName\":\"Security Group\",\"securityEnabled\":true}]}"
                        : url.Contains("/servicePrincipals?", StringComparison.Ordinal)
                            ? "{\"value\":[{\"id\":\"sp-1\",\"displayName\":\"Workload Identity\",\"servicePrincipalType\":\"ManagedIdentity\",\"accountEnabled\":true}]}"
                            : url.Contains("roleAssignmentScheduleInstances", StringComparison.Ordinal)
                                ? "{\"value\":[{\"principalId\":\"user-1\",\"principal\":{\"displayName\":\"Entra User\"},\"roleDefinition\":{\"displayName\":\"Global Administrator\"},\"directoryScopeId\":\"/\"}]}"
                                : "{\"value\":[{\"principalId\":\"user-1\",\"principal\":{\"displayName\":\"Entra User\"},\"roleDefinition\":{\"displayName\":\"Security Administrator\"},\"directoryScopeId\":\"/\"}]}";
                return Task.FromResult(new CloudCommandResult(0, graphOutput, string.Empty));
            }

            var output = arguments[0] == "account" && arguments.Contains("list")
                ? multipleSubscriptions
                    ? "[{\"name\":\"Production\",\"id\":\"subscription-001\",\"tenantId\":\"tenant-001\",\"state\":\"Enabled\"},{\"name\":\"Development\",\"id\":\"subscription-002\",\"tenantId\":\"tenant-001\",\"state\":\"Enabled\"},{\"name\":\"Disabled\",\"id\":\"subscription-disabled\",\"tenantId\":\"tenant-001\",\"state\":\"Disabled\"}]"
                    : "[{\"name\":\"Production\",\"id\":\"subscription-001\",\"tenantId\":\"tenant-001\",\"state\":\"Enabled\"}]"
                : arguments[0] == "account"
                ? "{\"name\":\"Production\",\"id\":\"subscription-001\",\"tenantId\":\"tenant-001\"}"
                : "[{\"principalId\":\"principal-001\",\"principalName\":\"owner@example.com\",\"principalType\":\"User\",\"roleDefinitionId\":\"role-001\",\"roleDefinitionName\":\"Owner\",\"scope\":\"/subscriptions/subscription-001\"}]";
            return Task.FromResult(new CloudCommandResult(0, output, string.Empty));
        }
    }
}
