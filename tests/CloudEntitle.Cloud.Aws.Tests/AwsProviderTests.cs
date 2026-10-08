using CloudEntitle.Cloud.Aws;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;
using Xunit;

namespace CloudEntitle.Cloud.Aws.Tests;

public class AwsProviderTests
{
    [Fact]
    public async Task AwsProvider_ShouldDiscoverConnectedAccount()
    {
        var provider = new AwsProvider();

        var accounts = await provider.DiscoverAccountsAsync();

        Assert.NotEmpty(accounts);
        Assert.Equal(CloudProviderType.Aws, accounts[0].CloudProvider);
        Assert.True(accounts[0].IsReadOnly);
    }

    [Fact]
    public async Task AwsProvider_ShouldExposePrivilegedRole()
    {
        var provider = new AwsProvider();

        var roles = await provider.DiscoverRolesAsync(Guid.NewGuid());

        Assert.NotEmpty(roles);
        Assert.Equal("AdministratorAccess", roles[0].Name);
    }

    [Fact]
    public async Task AwsProvider_ShouldDiscoverAndScanMultipleConfiguredProfiles()
    {
        var provider = new AwsProvider(new MultiProfileAwsCommandRunner(), useLiveDiscovery: true);

        var accounts = await provider.DiscoverAccountsAsync();
            var firstAccount = accounts.Single(account => account.ExternalAccountId == "111111111111");
            var secondAccountProfiles = accounts.Where(account => account.ExternalAccountId == "222222222222").ToArray();
            var firstAccountUsers = await provider.DiscoverIdentitiesAsync(firstAccount.Id);
            var secondAccountUsers = await provider.DiscoverIdentitiesAsync(secondAccountProfiles[0].Id);
            var secondPermissionSetUsers = await provider.DiscoverIdentitiesAsync(secondAccountProfiles[1].Id);

            Assert.Equal(3, accounts.Count);
        Assert.Equal("dev-user", Assert.Single(firstAccountUsers).Name);
        Assert.Equal("prod-user", Assert.Single(secondAccountUsers).Name);
            Assert.Equal("prod-audit-user", Assert.Single(secondPermissionSetUsers).Name);
            Assert.Contains("permission set: prod-audit", secondAccountProfiles[1].Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AwsProvider_ShouldListOrganizationMembersWithoutClaimingUnconfiguredAccess()
    {
        var provider = new AwsProvider(new MultiProfileAwsCommandRunner(), useLiveDiscovery: true);
        await provider.DiscoverAccountsAsync();

        var result = await provider.DiscoverOrganizationAccountsAsync();

        Assert.Equal(3, result.Accounts.Count);
        Assert.Equal(ConnectionStatus.Connected, result.Accounts.Single(account => account.ExternalAccountId == "111111111111").ConnectionStatus);
        Assert.Equal(ConnectionStatus.Disconnected, result.Accounts.Single(account => account.ExternalAccountId == "333333333333").ConnectionStatus);
    }

        [Fact]
        public async Task AwsProvider_ShouldNormalizeReadOnlyScanAndTrustRelationships()
        {
            var provider = new AwsProvider(new FakeAwsCommandRunner(), useLiveDiscovery: true);

            var result = await provider.ScanAsync();

            Assert.Equal(ScanStatus.Completed, result.Scan.Status);
            Assert.NotEmpty(result.Accounts);
            Assert.NotEmpty(result.Principals);
            Assert.NotEmpty(result.Groups);
            Assert.NotEmpty(result.Policies);
            Assert.NotEmpty(result.Permissions);
            Assert.NotEmpty(result.TrustRelationships);
        }

        [Fact]
        public async Task AwsProvider_ShouldExposeObservedIamPolicyAttachmentsAsEffectiveAccess()
        {
            var provider = new AwsProvider(new FakeAwsCommandRunner(), useLiveDiscovery: true);
            var account = Assert.Single(await provider.DiscoverAccountsAsync());

            var access = await provider.DiscoverEffectiveAccessAsync(account.Id);

            Assert.Contains(access, assignment => assignment.Principal == "demo-user" && assignment.Role == "ReadOnlyAccess");
            Assert.Contains(access, assignment => assignment.Principal == "demo-user" && assignment.Role == "InlineRead");
        }

        [Fact]
        public async Task AwsProvider_ShouldExposeAccessAnalyzerFindingsAndRoleUsage()
        {
            var provider = new AwsProvider(new FakeAwsCommandRunner(), useLiveDiscovery: true);
            var account = Assert.Single(await provider.DiscoverAccountsAsync());

            var findings = await provider.DiscoverAccessAnalyzerFindingsAsync(account.Id);
            var assignments = await provider.DiscoverEffectiveAccessAsync(account.Id);
            var users = await provider.DiscoverIdentitiesAsync(account.Id);

            Assert.Contains(findings, finding => finding.Title.Contains("ExternalAccess", StringComparison.Ordinal));
            Assert.Contains(assignments, assignment => assignment.AssignmentType == "Role usage" && assignment.LastUsedAt.HasValue);
            var unusedRole = Assert.Single(assignments, assignment => assignment.AssignmentType == "Role usage" && assignment.Principal == "UnusedDemoRole");
            Assert.Null(unusedRole.LastUsedAt);
            Assert.True(unusedRole.UsageHistoryComplete);
            Assert.Equal(400, unusedRole.UsageHistoryDays);
            Assert.Contains("no role use", unusedRole.Source, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(assignments, assignment => assignment.Principal == "demo-user" && assignment.Role == "ReadOnlyAccess" && assignment.LastChangedAt.HasValue && assignment.LastChangedBy == "arn:aws:iam::123456789012:user/auditor" && assignment.Source.Contains("CloudTrail", StringComparison.Ordinal));
            Assert.Contains("Console password used", Assert.Single(users).LastActivity!);
        }

        [Fact]
        public async Task AwsProvider_ShouldDiscoverTaggedResourcesForSecuritySignalMapping()
        {
            var provider = new AwsProvider(new FakeAwsCommandRunner(), useLiveDiscovery: true);
            var account = Assert.Single(await provider.DiscoverAccountsAsync());

            var resource = Assert.Single(await provider.GetResourcesAsync(account.Id));

            Assert.Equal("customer-records", resource.Name);
            Assert.Equal("true", resource.Tags["CriticalAsset"]);
            Assert.Equal("restricted", resource.Tags["DataClassification"]);
        }

        [Fact]
        public async Task AwsProvider_ShouldHonorCancellation()
        {
            var provider = new AwsProvider(new FakeAwsCommandRunner(), useLiveDiscovery: true);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var result = await provider.ScanAsync(cancellationToken: cancellation.Token);

            Assert.Equal(ScanStatus.Cancelled, result.Scan.Status);
        }

        private sealed class FakeAwsCommandRunner : ICloudCommandRunner
        {
            public Task<CloudCommandResult> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
            {
                var output = arguments.Contains("get-resources")
                    ? "{\"ResourceTagMappingList\":[{\"ResourceARN\":\"arn:aws:s3:::customer-records\",\"Tags\":[{\"Key\":\"CriticalAsset\",\"Value\":\"true\"},{\"Key\":\"DataClassification\",\"Value\":\"restricted\"}]}]}"
                    : arguments.Contains("lookup-events")
                    ? "{\"Events\":[{\"EventName\":\"PutUserPolicy\",\"EventTime\":\"2026-10-01T11:00:00Z\",\"CloudTrailEvent\":\"{\\\"userIdentity\\\":{\\\"arn\\\":\\\"arn:aws:iam::123456789012:user/auditor\\\"},\\\"requestParameters\\\":{\\\"userName\\\":\\\"demo-user\\\",\\\"policyName\\\":\\\"ReadOnlyAccess\\\"}}\"}]}"
                    : arguments.Contains("list-analyzers")
                    ? "{\"analyzers\":[{\"arn\":\"arn:aws:access-analyzer:us-east-1:123456789012:analyzer/demo\",\"status\":\"ACTIVE\"}]}"
                    : arguments.Contains("list-findings-v2")
                        ? "{\"findings\":[{\"status\":\"ACTIVE\",\"findingType\":\"ExternalAccess\",\"resource\":\"arn:aws:s3:::private-bucket\",\"resourceType\":\"AWS::S3::Bucket\",\"principal\":{\"AWS\":\"*\"},\"createdAt\":\"2026-01-01T00:00:00Z\"}]}"
                        : arguments.Contains("get-account-authorization-details")
                    ? "{\"UserDetailList\":[{\"UserName\":\"demo-user\",\"AttachedManagedPolicies\":[{\"PolicyName\":\"ReadOnlyAccess\"}],\"UserPolicyList\":[{\"UserPolicyName\":\"InlineRead\"}]}],\"GroupDetailList\":[],\"RoleDetailList\":[{\"RoleName\":\"DemoRole\",\"AttachedManagedPolicies\":[{\"PolicyName\":\"ReadOnlyAccess\"}],\"RolePolicyList\":[],\"RoleLastUsed\":{\"LastUsedDate\":\"2026-10-01T12:00:00Z\",\"Region\":\"us-east-1\"}},{\"RoleName\":\"UnusedDemoRole\",\"AttachedManagedPolicies\":[{\"PolicyName\":\"ReadOnlyAccess\"}],\"RolePolicyList\":[],\"RoleLastUsed\":{}}]}"
                    : arguments.Contains("sts")
                    ? "{\"Account\":\"123456789012\",\"Arn\":\"arn:aws:iam::123456789012:user/demo\"}"
                    : arguments.Contains("list-users")
                        ? "{\"Users\":[{\"UserName\":\"demo-user\",\"UserId\":\"AIDEMO\",\"PasswordLastUsed\":\"2026-10-01T10:00:00Z\"}]}"
                        : arguments.Contains("list-groups")
                            ? "{\"Groups\":[{\"GroupName\":\"demo-group\",\"GroupId\":\"AGDEMO\"}]}"
                            : arguments.Contains("list-roles")
                                ? "{\"Roles\":[{\"RoleName\":\"DemoRole\",\"Arn\":\"arn:aws:iam::123456789012:role/DemoRole\"}]}"
                                : arguments.Contains("list-policies")
                                    ? "{\"Policies\":[{\"PolicyName\":\"DemoPolicy\",\"Arn\":\"arn:aws:iam::123456789012:policy/DemoPolicy\",\"Document\":\"{\\\"Statement\\\":[{\\\"Effect\\\":\\\"Allow\\\",\\\"Action\\\":\\\"s3:*\\\",\\\"Resource\\\":\\\"arn:aws:s3:::demo/*\\\"}]}\"}]}"
                                    : "{\"Role\":{\"AssumeRolePolicyDocument\":\"{\\\"Statement\\\":[{\\\"Effect\\\":\\\"Allow\\\",\\\"Principal\\\":{\\\"AWS\\\":\\\"arn:aws:iam::123456789012:user/demo-user\\\"}}]}\"}}";
                return Task.FromResult(new CloudCommandResult(0, output, string.Empty));
            }
        }

        private sealed class MultiProfileAwsCommandRunner : ICloudCommandRunner
        {
            public Task<CloudCommandResult> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
            {
                if (arguments.SequenceEqual(new[] { "configure", "list-profiles" }))
                    return Task.FromResult(new CloudCommandResult(0, "dev\nprod\nprod-audit\n", string.Empty));

                if (arguments.Contains("list-accounts"))
                    return Task.FromResult(new CloudCommandResult(0, "{\"Accounts\":[{\"Id\":\"111111111111\",\"Name\":\"Development\",\"Status\":\"ACTIVE\"},{\"Id\":\"222222222222\",\"Name\":\"Production\",\"Status\":\"ACTIVE\"},{\"Id\":\"333333333333\",\"Name\":\"Unconfigured\",\"Status\":\"ACTIVE\"}]}", string.Empty));

                var profileIndex = Array.IndexOf(arguments.ToArray(), "--profile");
                var profile = profileIndex >= 0 ? arguments[profileIndex + 1] : "default";
                var accountId = profile == "dev" ? "111111111111" : "222222222222";
                var output = arguments.Contains("sts")
                    ? $"{{\"Account\":\"{accountId}\",\"Arn\":\"arn:aws:sts::{accountId}:assumed-role/{profile}/aws-user\"}}"
                    : arguments.Contains("list-users")
                        ? $"{{\"Users\":[{{\"UserName\":\"{profile}-user\",\"UserId\":\"{profile}-id\"}}]}}"
                        : "{}";
                return Task.FromResult(new CloudCommandResult(0, output, string.Empty));
            }
        }
}
