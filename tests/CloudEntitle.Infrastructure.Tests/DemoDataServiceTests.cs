using CloudEntitle.Domain;
using CloudEntitle.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CloudEntitle.Infrastructure.Tests;

public sealed class DemoDataServiceTests
{
    [Fact]
    public async Task LoadAsync_ShouldPersistAzureAwsAndGcpDataThroughNormalizedEntities()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = CreateFactory(connection);
        await using var setup = await factory.CreateDbContextAsync();
        await setup.Database.EnsureCreatedAsync();
        setup.CloudAccounts.Add(new CloudAccount { Id = Guid.NewGuid(), Name = "Real account", ExternalAccountId = "real-account", CloudProvider = CloudProviderType.Azure });
        await setup.SaveChangesAsync();
        var service = new DemoDataService(factory);

        var statistics = await service.LoadAsync();
        var snapshot = await service.GetSnapshotAsync();

        Assert.Equal(3, statistics.CloudAccounts);
        Assert.Equal(CloudProviderType.Azure, snapshot.Accounts.Single(account => account.ExternalAccountId == "demo-azure-subscription").CloudProvider);
        Assert.Contains(snapshot.Accounts, account => account.CloudProvider == CloudProviderType.Aws);
        Assert.Contains(snapshot.Accounts, account => account.CloudProvider == CloudProviderType.Gcp);
        Assert.NotEmpty(snapshot.Principals);
        Assert.NotEmpty(snapshot.Entitlements);
        Assert.NotEmpty(snapshot.EffectiveAccess);
        Assert.NotEmpty(snapshot.RiskFindings);
        Assert.NotEmpty(snapshot.Recommendations);
        Assert.NotEmpty(snapshot.Scans);
        Assert.Equal(4, await setup.CloudAccounts.CountAsync());
    }

    [Fact]
    public async Task LoadAsync_ShouldBeDeterministicAndResetOnlyDemoRecords()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = CreateFactory(connection);
        await using var setup = await factory.CreateDbContextAsync();
        await setup.Database.EnsureCreatedAsync();
        var realId = Guid.NewGuid();
        setup.CloudAccounts.Add(new CloudAccount { Id = realId, Name = "Real account", ExternalAccountId = "real-account", CloudProvider = CloudProviderType.Azure });
        await setup.SaveChangesAsync();
        var service = new DemoDataService(factory);

        await service.LoadAsync();
        var firstCount = await setup.CloudAccounts.CountAsync();
        await service.LoadAsync();
        var secondCount = await setup.CloudAccounts.CountAsync();
        await service.ResetAsync();

        Assert.Equal(firstCount, secondCount);
        Assert.Equal(1, await setup.CloudAccounts.CountAsync());
        Assert.Equal(realId, (await setup.CloudAccounts.SingleAsync()).Id);
        Assert.False(service.IsDemoMode);
    }

    private static TestContextFactory CreateFactory(SqliteConnection connection) => new(new DbContextOptionsBuilder<CloudEntitleDbContext>().UseSqlite(connection).Options);

    private sealed class TestContextFactory : IDbContextFactory<CloudEntitleDbContext>
    {
        private readonly DbContextOptions<CloudEntitleDbContext> _options;
        public TestContextFactory(DbContextOptions<CloudEntitleDbContext> options) => _options = options;
        public CloudEntitleDbContext CreateDbContext() => new(_options);
        public Task<CloudEntitleDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(new CloudEntitleDbContext(_options));
    }
}
