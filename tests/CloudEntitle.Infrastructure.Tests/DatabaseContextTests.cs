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

    [Fact]
    public async Task CloudPrincipal_ShouldPersistWorkloadGovernanceEvidenceThroughMigrations()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CloudEntitleDbContext>().UseSqlite(connection).Options;
        var ownerId = "platform@example.invalid";
        var trust = new WorkloadFederationTrust("https://issuer.example", "repo:org/app:environment:prod", new[] { "sts.amazonaws.com" }, "fixture");
        var principal = new CloudPrincipal
        {
            Id = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            CloudProvider = CloudProviderType.Aws,
            Name = "federated-workload",
            PrincipalId = "arn:aws:iam::123456789012:role/federated-workload",
            IdentityType = IdentityType.ServiceAccount,
            OwnerIds = new List<string> { ownerId },
            OwnerInventoryComplete = true,
            CredentialLastRotatedAtUtc = DateTime.UtcNow.AddDays(-10),
            CredentialInventoryComplete = true,
            FederationTrusts = new List<WorkloadFederationTrust> { trust },
            FederationInventoryComplete = true,
            FederationTrustVerified = true,
            LastObservedWorkloadAtUtc = DateTime.UtcNow
        };

        await using (var context = new CloudEntitleDbContext(options))
        {
            await context.Database.MigrateAsync();
            context.CloudPrincipals.Add(principal);
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var loaded = await context.CloudPrincipals.SingleAsync();
            Assert.Equal(new[] { ownerId }, loaded.OwnerIds);
            Assert.Equal(trust.Issuer, Assert.Single(loaded.FederationTrusts).Issuer);
            Assert.True(loaded.FederationTrustVerified);
            Assert.Equal("platform@example.invalid", loaded.OwnerSummary);
            Assert.Equal(principal.LastObservedWorkloadAtUtc, loaded.LastObservedWorkloadAtUtc);
        }
    }

    [Fact]
    public async Task SqliteReportService_ShouldExportJsonAndCsv()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<CloudEntitleDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var context = new CloudEntitleDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();
            context.RiskFindings.Add(new RiskFinding
            {
                Id = Guid.NewGuid(),
                CloudProvider = CloudProviderType.Azure,
                Severity = RiskSeverity.High,
                Title = "Owner access",
                Principal = "admin@example.com",
                Resource = "subscription-root",
                Permission = "Owner",
                Recommendation = "Use least privilege",
                Evidence = "Subscription assignment"
            });
            await context.SaveChangesAsync();
        }

        var reportDirectory = Path.Combine(Path.GetTempPath(), "yask-ciem-report-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new SqliteReportService(options, reportDirectory);
            var jsonPath = await service.GenerateReportAsync("json");
            var csvPath = await service.GenerateReportAsync("csv");

            Assert.Contains("Owner access", await File.ReadAllTextAsync(jsonPath));
            Assert.Contains("Owner access", await File.ReadAllTextAsync(csvPath));
        }
        finally
        {
            if (Directory.Exists(reportDirectory))
            {
                Directory.Delete(reportDirectory, true);
            }
        }
    }
}
