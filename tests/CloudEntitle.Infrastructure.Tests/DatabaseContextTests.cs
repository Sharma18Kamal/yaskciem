using CloudEntitle.Domain;
using CloudEntitle.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CloudEntitle.Infrastructure.Tests;

public class DatabaseContextTests
{
    [Fact]
    public async Task CloudEntitleDbContext_ShouldPersistNormalizedEntities()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<CloudEntitleDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var context = new CloudEntitleDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();

            context.CloudAccounts.Add(new CloudAccount
            {
                Id = Guid.NewGuid(),
                Name = "Azure Production",
                CloudProvider = CloudProviderType.Azure,
                ConnectionStatus = ConnectionStatus.Connected,
                SubscriptionId = "sub-001",
                IsReadOnly = true
            });

            await context.SaveChangesAsync();

            var count = await context.CloudAccounts.CountAsync();
            Assert.Equal(1, count);

            context.RiskRules.Add(new RiskRule
            {
                Id = Guid.NewGuid(),
                CloudProvider = CloudProviderType.Azure,
                Name = "Azure Owner permission",
                Description = "Detects full Azure ownership.",
                Severity = RiskSeverity.High,
                RuleExpression = "permission-name:Owner"
            });

            await context.SaveChangesAsync();

            Assert.Equal(1, await context.RiskRules.CountAsync());
        }
    }
}
