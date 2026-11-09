using CloudEntitle.Desktop.Services;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CloudEntitle.Desktop.Tests;

public sealed class WhatIfSavedDataTests
{
    [Fact]
    public async Task LoadAsync_RejectsInventoryWithoutCollectionTimestamp()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CloudEntitleDbContext>().UseSqlite(connection).Options;
        var factory = new Factory(options);
        await using (var setup = factory.CreateDbContext())
        {
            await setup.Database.EnsureCreatedAsync();
            setup.CloudAccounts.Add(new CloudAccount
            {
                Id = Guid.NewGuid(),
                Name = "Unscanned account",
                CloudProvider = CloudProviderType.Aws,
                ExternalAccountId = "123456789012"
            });
            await setup.SaveChangesAsync();
        }

        await using var db = factory.CreateDbContext();
        var accountId = await db.CloudAccounts.Select(account => account.Id).SingleAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new WhatIfSavedData(factory).LoadAsync(accountId, CancellationToken.None));
    }

    private sealed class Factory(DbContextOptions<CloudEntitleDbContext> options) : IDbContextFactory<CloudEntitleDbContext>
    {
        public CloudEntitleDbContext CreateDbContext() => new(options);
    }
}
