using Xunit;
using CloudEntitle.Application.Connectors;
using CloudEntitle.Cloud.Aws;

namespace CloudEntitle.Cloud.Aws.Tests;

public class AwsConnectorTests
{
    private sealed class FakeApi(string account, IReadOnlyList<AwsRole> roles) : IAwsAccountApi
    {
        public Task<string> GetAccountIdAsync(CancellationToken ct = default) => Task.FromResult(account);

        public Task<IReadOnlyList<AwsRole>> ListRolesAsync(CancellationToken ct = default) => Task.FromResult(roles);

        public void Dispose() { }
    }

    private sealed class FakeFactory(IAwsAccountApi api) : IAwsAccountApiFactory
    {
        public Task<IAwsAccountApi> CreateAsync(string roleArn, string externalId, CancellationToken ct = default) => Task.FromResult(api);
    }

    private static ConnectorScope Scope(string? role = "arn:aws:iam::111111111111:role/r", string? secret = "ext")
        => new(Guid.NewGuid(), "aws", "111111111111", role, secret);

    [Fact]
    public async Task Missing_role_is_unavailable()
    {
        var result = await new AwsConnector(new FakeFactory(new FakeApi("111111111111", []))).DiscoverAsync(Scope(role: null));

        Assert.Equal(CoverageStatus.Unavailable, Assert.Single(result.Coverage).Status);
    }

    [Fact]
    public async Task Account_mismatch_is_denied_and_collects_nothing()
    {
        var result = await new AwsConnector(new FakeFactory(new FakeApi("999999999999", [new AwsRole("a", "n", "/", "{}")]))).DiscoverAsync(Scope());

        Assert.Equal(CoverageStatus.Denied, Assert.Single(result.Coverage).Status);
        Assert.Empty(result.Identities);
    }

    [Fact]
    public async Task Roles_are_collected()
    {
        var api = new FakeApi("111111111111", [new AwsRole("arn:aws:iam::111111111111:role/x", "x", "/", "{}")]);

        var result = await new AwsConnector(new FakeFactory(api)).DiscoverAsync(Scope());

        Assert.Equal(CoverageStatus.Collected, result.Coverage.Single(c => c.Area == "iam.roles").Status);
        Assert.Equal("x", Assert.Single(result.Identities).Name);
    }

    [Fact]
    public async Task No_roles_is_no_data()
    {
        var result = await new AwsConnector(new FakeFactory(new FakeApi("111111111111", []))).DiscoverAsync(Scope());

        Assert.Equal(CoverageStatus.NoData, result.Coverage.Single(c => c.Area == "iam.roles").Status);
    }
}
