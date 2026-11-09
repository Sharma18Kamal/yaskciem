using CloudEntitle.Desktop.Services;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Infrastructure.Persistence;
using CloudEntitle.RiskEngine.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;

namespace CloudEntitle.Desktop.Tests;

public sealed class ToxicCombinationWorkspaceServiceTests
{
    [Fact]
    public async Task Rules_AreSeededConfigurableAndPersisted()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = CreateFactory(connection);
        await using (var setup = await factory.CreateDbContextAsync()) await setup.Database.EnsureCreatedAsync();
        var service = CreateService(factory);

        var rules = await service.GetRulesAsync(CancellationToken.None);
        Assert.Equal(13, rules.Count);
        Assert.Contains(rules, item => item.Provider == CloudProvider.Aws);
        Assert.Contains(rules, item => item.Provider == CloudProvider.Azure);
        Assert.Contains(rules, item => item.Provider == CloudProvider.Gcp);
        var target = rules.First();
        target.Enabled = false;
        target.RiskScore = 47;
        await service.SaveRulesAsync(rules, CancellationToken.None);

        var reloaded = await service.GetRulesAsync(CancellationToken.None);
        var saved = Assert.Single(reloaded, item => item.Key == target.Key);
        Assert.False(saved.Enabled);
        Assert.Equal(47, saved.RiskScore);
    }

    [Fact]
    public async Task Evaluation_PersistsFullEvidenceRecommendationsAndIsIdempotent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = CreateFactory(connection);
        await using (var setup = await factory.CreateDbContextAsync()) await setup.Database.EnsureCreatedAsync();
        var service = CreateService(factory);
        var rules = await service.GetEnabledRulesAsync(CancellationToken.None);
        var request = CreateSensitiveStorageRequest(rules);
        var findings = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(request);

        await service.RecordEvaluationAsync(request, findings.Findings, CancellationToken.None);
        await service.RecordEvaluationAsync(request, findings.Findings, CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        Assert.Single(await verify.ToxicCombinationFindings.ToArrayAsync());
        Assert.Equal(2, await verify.ToxicCombinationEvidence.CountAsync());
        Assert.Single(await verify.PermissionCombinations.ToArrayAsync());
        var recommendation = await verify.ToxicCombinationRecommendations.SingleAsync();
        Assert.False(recommendation.Executable);
        Assert.Equal("ReviewOnly", recommendation.Status);
        var rows = await service.GetEvidenceAsync(CancellationToken.None);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, item => item.IsDirect);
        Assert.Contains(rows, item => item.IsInherited);
    }

    [Fact]
    public async Task CustomRulePreviewAndCreation_AreAuditedAndRejectDuplicateKeys()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = CreateFactory(connection);
        await using (var setup = await factory.CreateDbContextAsync()) await setup.Database.EnsureCreatedAsync();
        var accountId = Guid.NewGuid();
        var principalId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var readPermission = new CloudPermission { Id = Guid.NewGuid(), AccountId = accountId, Name = "Read", Action = "s3:GetObject" };
        var writePermission = new CloudPermission { Id = Guid.NewGuid(), AccountId = accountId, Name = "Write policy", Action = "s3:PutBucketPolicy" };
        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.CloudAccounts.Add(new CloudAccount
            {
                Id = accountId, Name = "preview-account", ExternalAccountId = "preview-account",
                CloudProvider = CloudProviderType.Aws
            });
            seed.CloudPrincipals.Add(new CloudPrincipal
            {
                Id = principalId, AccountId = accountId, Name = "preview-principal",
                CloudProvider = CloudProviderType.Aws, IdentityType = IdentityType.User
            });
            seed.CloudResources.Add(new CloudResource
            {
                Id = resourceId, AccountId = accountId, Name = "preview-bucket",
                ResourceId = "arn:aws:s3:::preview-bucket", Type = "S3 Bucket"
            });
            seed.CloudPermissions.AddRange(readPermission, writePermission);
            seed.EffectiveAccesses.AddRange(
                new DomainEffectiveAccess
                {
                    Id = Guid.NewGuid(), CloudAccountId = accountId, Provider = CloudProvider.Aws,
                    PrincipalId = principalId, ResourceId = resourceId, PermissionId = readPermission.Id,
                    Decision = AccessDecision.Allow, EvaluationStatus = EvaluationStatus.FullyEvaluated,
                    AccessType = AccessType.Allow, IsDirect = true
                },
                new DomainEffectiveAccess
                {
                    Id = Guid.NewGuid(), CloudAccountId = accountId, Provider = CloudProvider.Aws,
                    PrincipalId = principalId, ResourceId = resourceId, PermissionId = writePermission.Id,
                    Decision = AccessDecision.Allow, EvaluationStatus = EvaluationStatus.FullyEvaluated,
                    AccessType = AccessType.Allow, IsDirect = true
                });
            await seed.SaveChangesAsync();
        }

        var service = CreateService(factory);
        var rule = new ToxicCombinationRule
        {
            Key = "custom.preview-policy-pair",
            Name = "Preview policy pair",
            Description = "A test-only custom combination.",
            Provider = CloudProvider.Aws,
            FirstPermissionPattern = "s3:GetObject",
            SecondPermissionPattern = "s3:PutBucketPolicy",
            ResourceRequirement = ToxicResourceRequirement.Any,
            Severity = RiskSeverity.High,
            RiskScore = 72,
            Recommendation = "Review the pair."
        };
        var preview = await service.PreviewRuleAsync(rule, CancellationToken.None);
        Assert.Single(preview);

        var userId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        await service.AddRuleAsync(rule, CancellationToken.None, userId, organizationId, "admin@example.invalid");

        var saved = Assert.Single(await service.GetRulesAsync(CancellationToken.None), item => item.Key == rule.Key);
        Assert.Equal("1.0", saved.Version);
        var audit = Assert.Single(await service.GetRuleAuditHistoryAsync(CancellationToken.None));
        Assert.Equal("ToxicCombination.RuleCreated", audit.Action);
        Assert.Equal("admin@example.invalid", audit.Actor);
        Assert.Contains(rule.Key, audit.Details, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.AddRuleAsync(rule, CancellationToken.None, userId, organizationId, "admin@example.invalid"));
    }

    [Fact]
    public async Task CrossCloudTrustFinding_PersistsEachTrustPathEvidenceLink()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = CreateFactory(connection);
        await using (var setup = await factory.CreateDbContextAsync()) await setup.Database.EnsureCreatedAsync();
        var service = CreateService(factory);
        var accounts = new[]
        {
            new CloudAccount { Id = Guid.NewGuid(), Name = "AWS source", ExternalAccountId = "aws-source", CloudProvider = CloudProviderType.Aws },
            new CloudAccount { Id = Guid.NewGuid(), Name = "GCP target", ExternalAccountId = "gcp-target", CloudProvider = CloudProviderType.Gcp }
        };
        var principals = new[]
        {
            new CloudPrincipal { Id = Guid.NewGuid(), AccountId = accounts[0].Id, Name = "source", CloudProvider = CloudProviderType.Aws },
            new CloudPrincipal { Id = Guid.NewGuid(), AccountId = accounts[1].Id, Name = "target", CloudProvider = CloudProviderType.Gcp, IsPrivileged = true }
        };
        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.CloudAccounts.AddRange(accounts);
            seed.CloudPrincipals.AddRange(principals);
            await seed.SaveChangesAsync();
        }
        var permissions = new[]
        {
            new CloudPermission { Id = Guid.NewGuid(), AccountId = accounts[0].Id, Name = "Assume", Action = "sts:AssumeRole" },
            new CloudPermission { Id = Guid.NewGuid(), AccountId = accounts[1].Id, Name = "Owner", Action = "roles/owner" }
        };
        var access = new[]
        {
            new DomainEffectiveAccess
            {
                Id = Guid.NewGuid(), CloudAccountId = accounts[0].Id, Provider = CloudProvider.Aws,
                PrincipalId = principals[0].Id, PermissionId = permissions[0].Id, Decision = AccessDecision.Allow,
                EvaluationStatus = EvaluationStatus.FullyEvaluated, AccessType = AccessType.Allow, IsDirect = true
            },
            new DomainEffectiveAccess
            {
                Id = Guid.NewGuid(), CloudAccountId = accounts[1].Id, Provider = CloudProvider.Gcp,
                PrincipalId = principals[1].Id, PermissionId = permissions[1].Id, Decision = AccessDecision.Allow,
                EvaluationStatus = EvaluationStatus.FullyEvaluated, AccessType = AccessType.Allow,
                IsDirect = true, IsPrivileged = true
            }
        };
        var trust = new ToxicTrustRelationshipEvidence(principals[0].Id, accounts[0].Id, CloudProvider.Aws,
            principals[1].Id, accounts[1].Id, CloudProvider.Gcp, "Federated workload identity",
            TrustRelationshipStatus.Confirmed, "Provider verifier", "evidence-link-1", DateTime.UtcNow,
            RequiresSourcePermission: false, ConditionState: AuthorizationConditionState.Satisfied);
        var rule = (await service.GetEnabledRulesAsync(CancellationToken.None)).Single(item => item.RequireTrustPath);
        var request = new RiskEvaluationRequest(accounts, principals, [], [], [], permissions, [], access, [], [],
            Options: new(EnabledRules: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CIEM-021" }),
            ToxicCombinationRules: [rule], ToxicTrustRelationships: [trust]);
        var findings = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(request);

        await service.RecordEvaluationAsync(request, findings.Findings, CancellationToken.None);

        var savedTrustPath = Assert.Single(await service.GetTrustPathEvidenceAsync(CancellationToken.None));
        Assert.Equal("source", savedTrustPath.SourceIdentity);
        Assert.Equal("target", savedTrustPath.TargetIdentity);
        Assert.Equal("evidence-link-1", savedTrustPath.EvidenceReference);
        Assert.False(savedTrustPath.RequiresSourcePermission);
    }

    [Fact]
    public async Task DemoMode_CreatesSyntheticFindingAndResetRemovesItsEvidence()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = CreateFactory(connection);
        await using (var setup = await factory.CreateDbContextAsync()) await setup.Database.EnsureCreatedAsync();
        var demo = new DemoDataService(factory);
        await demo.LoadAsync();
        var service = CreateService(factory);

        await service.LoadDemoAsync(CancellationToken.None);

        Assert.Contains(await service.GetEvidenceAsync(CancellationToken.None), item => item.Provider == "Aws"
            && item.SourceEvidence.Contains("Synthetic Demo Mode", StringComparison.Ordinal));
        await demo.ResetAsync();
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Empty(await verify.ToxicCombinationFindings.ToArrayAsync());
        Assert.Empty(await verify.ToxicCombinationEvidence.ToArrayAsync());
        Assert.Empty(await verify.PermissionCombinations.ToArrayAsync());
        Assert.Empty(await verify.ToxicCombinationRecommendations.ToArrayAsync());
    }

    private static RiskEvaluationRequest CreateSensitiveStorageRequest(IReadOnlyList<ToxicCombinationRule> rules)
    {
        var accountId = Guid.NewGuid();
        var principalId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var reader = new CloudPermission { Id = Guid.NewGuid(), AccountId = accountId, Name = "Read objects", Action = "s3:GetObject" };
        var writer = new CloudPermission { Id = Guid.NewGuid(), AccountId = accountId, Name = "Change bucket policy", Action = "s3:PutBucketPolicy" };
        var firstAccess = new DomainEffectiveAccess
        {
            Id = Guid.NewGuid(), Provider = CloudProvider.Aws, CloudAccountId = accountId, PrincipalId = principalId,
            ResourceId = resourceId, PermissionId = reader.Id, Decision = AccessDecision.Allow,
            EvaluationStatus = EvaluationStatus.FullyEvaluated, AccessType = AccessType.Allow,
            AssignmentType = AssignmentType.Direct, IsDirect = true, AccessSource = "AWS policy", Evidence = "source-statement-1"
        };
        var secondAccess = new DomainEffectiveAccess
        {
            Id = Guid.NewGuid(), Provider = CloudProvider.Aws, CloudAccountId = accountId, PrincipalId = principalId,
            ResourceId = resourceId, PermissionId = writer.Id, Decision = AccessDecision.Allow,
            EvaluationStatus = EvaluationStatus.FullyEvaluated, AccessType = AccessType.Allow,
            AssignmentType = AssignmentType.Inherited, IsInherited = true, AccessSource = "AWS role", Evidence = "source-statement-2"
        };
        return new RiskEvaluationRequest(
            [new CloudAccount { Id = accountId, Name = "Example", CloudProvider = CloudProviderType.Aws, ExternalAccountId = "123456789012" }],
            [new CloudPrincipal { Id = principalId, AccountId = accountId, Name = "reader", PrincipalId = "arn:aws:iam::123456789012:user/reader", CloudProvider = CloudProviderType.Aws }],
            [new CloudResource { Id = resourceId, AccountId = accountId, Name = "customer-data", Type = "S3 Bucket",
                ResourceId = "arn:aws:s3:::customer-data", Tags = new() { ["classification"] = "sensitive" } }],
            [], [], [reader, writer], [], [firstAccess, secondAccess], [], [],
            Options: new(EnabledRules: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CIEM-021" }),
            ResourceSecuritySignals: [new ResourceSecuritySignal(resourceId, ResourceSecuritySignalType.SensitiveData,
                "Sensitive data", "Synthetic provider classification", RiskSeverity.High)],
            ToxicCombinationRules: rules);
    }

    private static ToxicCombinationWorkspaceService CreateService(IDbContextFactory<CloudEntitleDbContext> factory)
        => new(factory, new CloudEntitle.RiskEngine.Services.RiskEngine());

    private static TestContextFactory CreateFactory(SqliteConnection connection)
        => new(new DbContextOptionsBuilder<CloudEntitleDbContext>().UseSqlite(connection).Options);

    private sealed class TestContextFactory(DbContextOptions<CloudEntitleDbContext> options)
        : IDbContextFactory<CloudEntitleDbContext>
    {
        public CloudEntitleDbContext CreateDbContext() => new(options);
        public Task<CloudEntitleDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}
