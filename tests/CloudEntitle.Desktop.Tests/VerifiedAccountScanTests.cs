using CloudEntitle.Application.Interfaces;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using Xunit;

namespace CloudEntitle.Desktop.Tests;

public sealed class VerifiedAccountScanTests
{
    [Fact]
    public async Task ShouldCollectOnlyVerifiedAccountAndSkipBroaderSources()
    {
        var target = new Provider();
        var other = new Provider(CloudProviderType.Gcp);
        var authentication = new Authentication();
        var clusters = new Clusters();
        var scanner = new LiveCloudScanService([target, other], kubernetesRbacProvider: clusters, authenticationLauncher: authentication);

        var result = await scanner.ScanVerifiedAccountAsync(Account(), Identity());

        Assert.Equal(2, authentication.Checks);
        Assert.Equal(0, target.ConnectionChecks);
        Assert.Equal(1, target.Discoveries);
        Assert.Equal(0, other.Discoveries);
        Assert.Equal(0, other.ConnectionChecks);
        Assert.Equal(1, result.AccountCount);
        Assert.Equal("account-a", Assert.Single(result.IdentityRecords).Name);
        Assert.Equal("account-a", Assert.Single(result.AccountRecords, item => item.ScanAccess == "Scanned").AccountId);
        Assert.Equal(0, target.DirectoryCalls);
        Assert.Equal(0, target.OrganizationCalls);
        Assert.Equal(0, clusters.Calls);
        foreach (var source in new[] { "Entra directory and PIM", "Organization hierarchy", "Cluster accounts" })
            Assert.Contains(result.Coverage, item => item.DataSource == source && item.Status == ScanCoverageStatus.Skipped);
        Assert.Same(result, scanner.CurrentResult);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("identity")]
    [InlineData("account")]
    public async Task ShouldRejectChangedIdentityBeforeDiscovery(string changed)
    {
        var provider = new Provider();
        var expected = Identity();
        var actual = changed switch
        {
            "tenant" => expected with { TenantId = "other" },
            "identity" => expected with { Identity = "other" },
            _ => expected with { AccountId = "other" }
        };
        var scanner = new LiveCloudScanService([provider], authenticationLauncher: new Authentication(actual));

        await Assert.ThrowsAsync<InvalidOperationException>(() => scanner.ScanVerifiedAccountAsync(Account(), expected));

        Assert.Equal(0, provider.Discoveries);
        Assert.Empty(provider.InventoryAccounts);
        Assert.Null(scanner.CurrentResult);
    }

    [Fact]
    public async Task ShouldRecheckAfterDiscoveryBeforeReadingAccountInventory()
    {
        var provider = new Provider();
        var expected = Identity();
        var scanner = new LiveCloudScanService([provider], authenticationLauncher:
            new Authentication(expected, expected with { Identity = "changed-during-discovery" }));

        var result = await scanner.ScanVerifiedAccountAsync(Account(), expected);

        Assert.Equal(1, provider.Discoveries);
        Assert.Empty(provider.InventoryAccounts);
        Assert.Equal(0, result.AccountCount);
        Assert.Contains(result.Coverage, item => item.DataSource == "Provider scan" && item.Status == ScanCoverageStatus.Unavailable);
        Assert.Contains(result.ProviderMessages, item => item.Contains("changed since verification"));
    }

    [Fact]
    public async Task ShouldNotFallBackWhenVerifiedAccountIsMissing()
    {
        var provider = new Provider();
        var expected = Identity() with { AccountId = "missing" };
        var scanner = new LiveCloudScanService([provider], authenticationLauncher: new Authentication(expected));
        var result = await scanner.ScanVerifiedAccountAsync(Account() with { AccountId = "missing" }, expected);
        Assert.Equal(0, result.AccountCount);
        Assert.Empty(provider.InventoryAccounts);
        Assert.Contains(result.ProviderMessages, item => item.Contains("not uniquely discovered"));
    }

    [Theory]
    [InlineData("Audit")]
    [InlineData("audit")]
    [InlineData("default")]
    public async Task AwsShouldMatchExactProfileWithoutScanningSiblingProfiles(string profile)
    {
        var provider = new Provider(CloudProviderType.Aws);
        var expected = Identity() with { Provider = "Aws", TenantId = "" };
        var scanner = new LiveCloudScanService([provider], authenticationLauncher: new Authentication(expected));
        var result = await scanner.ScanVerifiedAccountAsync(Account() with { Provider = "Aws", Profile = profile }, expected);

        var scanned = Assert.Single(result.AccountRecords, item => item.ScanAccess == "Scanned");
        Assert.Equal(profile == "default" ? "" : profile, scanned.Profile);
        Assert.Single(provider.InventoryAccounts);
        Assert.Equal(scanned.CloudAccountId, provider.InventoryAccounts.Single());
    }

    [Fact]
    public async Task ShouldPreservePartialCoverageForSelectedAccount()
    {
        var provider = new Provider { DenyIdentities = true };
        var scanner = new LiveCloudScanService([provider], authenticationLauncher: new Authentication());
        var result = await scanner.ScanVerifiedAccountAsync(Account(), Identity());
        Assert.Equal(1, result.AccountCount);
        Assert.Contains(result.Coverage, item => item.DataSource.EndsWith(": Identities") && item.Status == ScanCoverageStatus.Denied);
        Assert.Empty(result.IdentityRecords);
    }

    [Fact]
    public async Task ShouldReverifyAfterWaitingForAnotherScanRatherThanBeforeTheLock()
    {
        var provider = new Provider();
        var identity = Identity();
        var pending = new TaskCompletionSource<CloudAccountVerification>(TaskCreationOptions.RunContinuationsAsynchronously);
        var authentication = new Authentication(identity, identity, identity with { Identity = "new-cli-user" }) { PendingFirst = pending };
        var scanner = new LiveCloudScanService([provider], authenticationLauncher: authentication);
        var first = scanner.ScanVerifiedAccountAsync(Account(), identity);
        var queued = scanner.ScanVerifiedAccountAsync(Account(), identity);
        try
        {
            Assert.Equal(1, authentication.Checks);
        }
        finally
        {
            pending.TrySetResult(identity);
        }
        await first;
        await Assert.ThrowsAsync<InvalidOperationException>(() => queued);
        Assert.Equal(1, provider.Discoveries);
        Assert.Equal(3, authentication.Checks);
    }

    [Fact]
    public async Task ShouldRejectDisconnectedProviderWithoutVerifyingOrDiscovering()
    {
        var provider = new Provider();
        var state = new CloudEntitle.Desktop.ViewModels.CloudConnectionState();
        state.DisconnectProvider(CloudProviderType.Azure);
        var authentication = new Authentication();
        var scanner = new LiveCloudScanService([provider], cloudConnectionState: state, authenticationLauncher: authentication);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scanner.ScanVerifiedAccountAsync(Account(), Identity()));
        Assert.Equal(0, authentication.Checks);
        Assert.Equal(0, provider.Discoveries);
    }

    [Fact]
    public async Task ShouldFailClosedWithoutVerifierAndHonorCancellation()
    {
        var provider = new Provider();
        var scanner = new LiveCloudScanService([provider]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scanner.ScanVerifiedAccountAsync(Account(), Identity()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        scanner = new LiveCloudScanService([provider], authenticationLauncher: new Authentication());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scanner.ScanVerifiedAccountAsync(Account(), Identity(), cancellation.Token));
        Assert.Equal(0, provider.Discoveries);
    }

    private static LiveScanAccountRecord Account() => new("Azure", "Approved", "account-a", "", "");
    private static CloudAccountVerification Identity() => new("Azure", "account-a", "approved-user", "approved-tenant", DateTime.UtcNow);

    private sealed class Authentication(params CloudAccountVerification[] responses) : ICloudAuthenticationLauncher
    {
        public int Checks { get; private set; }
        public TaskCompletionSource<CloudAccountVerification>? PendingFirst { get; init; }
        public void LaunchAuthentication(CloudProviderType provider) => throw new NotSupportedException();
        public void LaunchDisconnection(CloudProviderType provider) => throw new NotSupportedException();
        public Task<bool> IsConnectedAsync(CloudProviderType provider, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DisconnectAsync(CloudProviderType provider, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async Task<CloudAccountVerification> VerifyAccountAsync(LiveScanAccountRecord account, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Checks++;
            if (Checks == 1 && PendingFirst is not null) return await PendingFirst.Task.WaitAsync(cancellationToken);
            return responses.Length == 0 ? Identity() : responses[Math.Min(Checks - 1, responses.Length - 1)];
        }
    }

    private sealed class Clusters : IKubernetesRbacProvider
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<CloudAccount>> DiscoverClustersAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Cluster discovery must not run.");
        }
        public Task<KubernetesRbacScanResult> ScanKubernetesRbacAsync(CancellationToken cancellationToken = default, CloudProviderType? providerFilter = null)
        {
            Calls++;
            throw new InvalidOperationException("Cluster scanning must not run.");
        }
    }

    private sealed class Provider(CloudProviderType type = CloudProviderType.Azure) : ICloudProvider, IEntraDirectoryProvider, ICloudOrganizationProvider
    {
        private readonly Dictionary<Guid, string> _accounts = [];
        public int Discoveries { get; private set; }
        public int ConnectionChecks { get; private set; }
        public int DirectoryCalls { get; private set; }
        public int OrganizationCalls { get; private set; }
        public bool DenyIdentities { get; init; }
        public List<Guid> InventoryAccounts { get; } = [];
        public CloudProviderType ProviderType => type;
        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
        {
            ConnectionChecks++;
            return Task.FromResult(true);
        }
        public Task<IReadOnlyList<CloudAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken = default)
        {
            Discoveries++;
            var accounts = type == CloudProviderType.Aws
                ? new[] { Create("account-a", "Audit"), Create("account-a", "audit"), Create("account-a", "") }
                : new[] { Create("account-a", ""), Create("account-b", "") };
            return Task.FromResult<IReadOnlyList<CloudAccount>>(accounts);
        }
        private CloudAccount Create(string id, string profile)
        {
            var account = new CloudAccount
            {
                Id = Guid.NewGuid(), Name = id, ExternalAccountId = id, CloudProvider = type,
                Description = string.IsNullOrEmpty(profile) ? "" : $"CLI profile: {profile}; permission set: ReadOnly"
            };
            _accounts.Add(account.Id, id);
            return account;
        }
        public Task<IReadOnlyList<CloudPrincipal>> DiscoverIdentitiesAsync(Guid accountId, CancellationToken cancellationToken = default)
        {
            InventoryAccounts.Add(accountId);
            if (DenyIdentities) throw new InvalidOperationException("Forbidden: permission denied.");
            return Task.FromResult<IReadOnlyList<CloudPrincipal>>([new CloudPrincipal { Id = Guid.NewGuid(), AccountId = accountId, Name = _accounts[accountId], CloudProvider = type }]);
        }
        public Task<IReadOnlyList<CloudRole>> DiscoverRolesAsync(Guid accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudRole>>([]);
        public Task<IReadOnlyList<CloudPermission>> DiscoverPermissionsAsync(Guid accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudPermission>>([]);
        public Task<EntraDirectoryScanResult> DiscoverEntraDirectoryAsync(Guid accountId, CancellationToken cancellationToken = default)
        {
            DirectoryCalls++;
            return Task.FromResult(new EntraDirectoryScanResult([], [], []));
        }
        public Task<CloudOrganizationScanResult> DiscoverOrganizationAccountsAsync(CancellationToken cancellationToken = default)
        {
            OrganizationCalls++;
            return Task.FromResult(new CloudOrganizationScanResult([], []));
        }
    }
}
