using System.Text.Json;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Domain.ValueObjects;
using CloudEntitle.Normalization.Models;
using CloudEntitle.Recommendations.Models;
using CloudEntitle.Recommendations.Services;
using CloudEntitle.RiskEngine.Models;
using Xunit;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;
using DomainAccessPath = CloudEntitle.Domain.Entities.AccessPath;

namespace CloudEntitle.Recommendations.Tests;

public sealed class RecommendationEngineTests
{
    [Fact]
    public async Task ConfirmedWildcardFinding_ShouldProduceTraceableRecommendation()
    {
        var fixture = FixtureData.Create(Stable("CIEM-003"), RiskEvaluationState.Confirmed, "*");
        var result = await new RecommendationEngine().EvaluateAsync(fixture.Context);

        var recommendation = Assert.Single(result.Recommendations);
        Assert.Equal(RecommendationType.NarrowWildcardPermission, recommendation.RecommendationType);
        Assert.Equal(RiskEvaluationState.Confirmed, recommendation.EvaluationState);
        Assert.Equal(fixture.Finding.Id, recommendation.RiskFindingId);
        Assert.Equal(fixture.Effective.Id, recommendation.EffectiveAccessId);
        Assert.Equal(fixture.Path.Id, recommendation.AccessPathId);
        Assert.Equal(fixture.Entitlement.Id, recommendation.EntitlementId);
        Assert.DoesNotContain(result.Recommendations, item => item.RecommendationType == RecommendationType.GenerateLeastPrivilegePolicy);
        Assert.False(string.IsNullOrWhiteSpace(recommendation.Evidence));
    }

    [Theory]
    [InlineData(CloudProvider.Azure, "Microsoft.Storage/storageAccounts/read", "Actions")]
    [InlineData(CloudProvider.Aws, "s3:GetObject", "Version")]
    [InlineData(CloudProvider.Gcp, "storage.objects.get", "includedPermissions")]
    public async Task CompleteUsageCoverage_ShouldGenerateReviewOnlyProviderPolicy(CloudProvider provider, string action, string expectedPolicyField)
    {
        var fixture = FixtureData.Create(Stable("CIEM-003"), RiskEvaluationState.Confirmed, "*", provider);
        var evaluatedAt = DateTime.UtcNow;
        var context = fixture.Context with
        {
            EvaluatedAt = evaluatedAt,
            ActivityObservationDays = 30,
            ActivityCoverage = new[] { new ActivityEvidenceCoverage(fixture.Effective.CloudAccountId, provider, evaluatedAt.AddDays(-30), evaluatedAt, ActivityCoverageStatus.Complete, true, true, true, 1, "All configured activity sources queried") },
            ActivityRecords = new[] { new ActivityRecord { PrincipalId = fixture.Effective.PrincipalId, RoleId = fixture.Entitlement.RoleId, CloudAccountId = fixture.Effective.CloudAccountId, ResourceId = fixture.Effective.ResourceId, Action = action, Timestamp = evaluatedAt.AddDays(-1), Success = true } }
        };

        var result = await new RecommendationEngine().EvaluateAsync(context);

        var recommendation = Assert.Single(result.Recommendations, item => item.RecommendationType == RecommendationType.GenerateLeastPrivilegePolicy);
        Assert.Equal(RecommendationImpactState.UnknownImpact, recommendation.ImpactState);
        Assert.Contains(expectedPolicyField, recommendation.SuggestedState, StringComparison.Ordinal);
        Assert.Contains(action, recommendation.SuggestedState, StringComparison.Ordinal);
        Assert.Contains("Unknown", recommendation.SuggestedState, StringComparison.Ordinal);
        Assert.Equal(RecommendationStatus.New, recommendation.Status);
        Assert.False(string.IsNullOrWhiteSpace(recommendation.Evidence));
        var providerProposal = provider switch
        {
            CloudProvider.Azure => new AzureRecommendationProvider().CreateProposal(recommendation),
            CloudProvider.Aws => new AwsRecommendationProvider().CreateProposal(recommendation),
            _ => new GcpRecommendationProvider().CreateProposal(recommendation)
        };
        Assert.False(providerProposal.Executable);
    }

    [Fact]
    public async Task AzureDataPermissionProposal_ShouldUseDataActionsAndPreserveAssignmentCondition()
    {
        const string conditionExpression = "@Resource[Microsoft.Storage/storageAccounts/blobServices/containers:name] StringEquals 'finance'";
        var fixture = FixtureData.Create(Stable("CIEM-003"), RiskEvaluationState.Confirmed, "*");
        var context = CreateUsageContext(fixture, CloudProvider.Azure,
            "Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read", new NormalizedCondition("Container owner", conditionExpression, "Unknown", conditionExpression),
            PermissionCategory.DataRead);

        var result = await new RecommendationEngine().EvaluateAsync(context);

        var recommendation = Assert.Single(result.Recommendations, item => item.RecommendationType == RecommendationType.GenerateLeastPrivilegePolicy);
        using var proposal = JsonDocument.Parse(recommendation.SuggestedState);
        var providerProposal = proposal.RootElement.GetProperty("providerProposal");
        var roleDefinition = providerProposal.GetProperty("RoleDefinition");
        Assert.Empty(roleDefinition.GetProperty("Actions").EnumerateArray());
        Assert.Equal("Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read", roleDefinition.GetProperty("DataActions")[0].GetString());
        Assert.Equal(fixture.Entitlement.Scope.OriginalScope, providerProposal.GetProperty("RoleAssignment").GetProperty("Scope").GetString());
        Assert.Equal(conditionExpression, providerProposal.GetProperty("RoleAssignment").GetProperty("Condition").GetProperty("Expression").GetString());
        Assert.Equal(RecommendationImpactState.UnknownImpact, recommendation.ImpactState);
    }

    [Fact]
    public async Task AwsProposal_ShouldPreserveIamConditionInsideEachStatement()
    {
        const string conditionExpression = "{\"StringEquals\":{\"aws:PrincipalTag/team\":\"finance\"}}";
        var fixture = FixtureData.Create(Stable("CIEM-003"), RiskEvaluationState.Confirmed, "*", CloudProvider.Aws);
        var context = CreateUsageContext(fixture, CloudProvider.Aws, "s3:GetObject",
            new NormalizedCondition("Team condition", conditionExpression, "Unknown", conditionExpression));

        var result = await new RecommendationEngine().EvaluateAsync(context);

        var recommendation = Assert.Single(result.Recommendations, item => item.RecommendationType == RecommendationType.GenerateLeastPrivilegePolicy);
        using var proposal = JsonDocument.Parse(recommendation.SuggestedState);
        var statement = proposal.RootElement.GetProperty("providerProposal").GetProperty("Statement")[0];
        Assert.Equal("Allow", statement.GetProperty("Effect").GetString());
        Assert.Equal("finance", statement.GetProperty("Condition").GetProperty("StringEquals").GetProperty("aws:PrincipalTag/team").GetString());
    }

    [Fact]
    public async Task GcpProposal_ShouldKeepCelConditionOnBindingRatherThanCustomRole()
    {
        const string conditionExpression = "resource.name.startsWith('projects/demo/buckets/finance')";
        var fixture = FixtureData.Create(Stable("CIEM-003"), RiskEvaluationState.Confirmed, "*", CloudProvider.Gcp);
        var context = CreateUsageContext(fixture, CloudProvider.Gcp, "storage.objects.get",
            new NormalizedCondition("Finance bucket condition", conditionExpression, "Unknown", conditionExpression));

        var result = await new RecommendationEngine().EvaluateAsync(context);

        var recommendation = Assert.Single(result.Recommendations, item => item.RecommendationType == RecommendationType.GenerateLeastPrivilegePolicy);
        using var proposal = JsonDocument.Parse(recommendation.SuggestedState);
        var providerProposal = proposal.RootElement.GetProperty("providerProposal");
        Assert.Equal("storage.objects.get", providerProposal.GetProperty("CustomRole").GetProperty("includedPermissions")[0].GetString());
        Assert.Equal(fixture.Entitlement.Scope.OriginalScope, providerProposal.GetProperty("Binding").GetProperty("Scope").GetString());
        Assert.Equal(conditionExpression, providerProposal.GetProperty("Binding").GetProperty("Condition").GetProperty("Expression").GetString());
        Assert.Contains("binding retains the source scope", proposal.RootElement.GetProperty("scopeSemantics").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AzureUnknownActionSemantics_ShouldWithholdPolicyProposal()
    {
        var fixture = FixtureData.Create(Stable("CIEM-003"), RiskEvaluationState.Confirmed, "*");
        var context = CreateUsageContext(fixture, CloudProvider.Azure, "Contoso.CustomResource/objects/read", null);

        var result = await new RecommendationEngine().EvaluateAsync(context);

        Assert.DoesNotContain(result.Recommendations, item => item.RecommendationType == RecommendationType.GenerateLeastPrivilegePolicy);
    }

    [Fact]
    public async Task AwsConditionThatCannotBeParsed_ShouldWithholdPolicyProposal()
    {
        var fixture = FixtureData.Create(Stable("CIEM-003"), RiskEvaluationState.Confirmed, "*", CloudProvider.Aws);
        var context = CreateUsageContext(fixture, CloudProvider.Aws, "s3:GetObject",
            new NormalizedCondition("Malformed condition", "not-json", "Unknown", "not-json"));

        var result = await new RecommendationEngine().EvaluateAsync(context);

        Assert.DoesNotContain(result.Recommendations, item => item.RecommendationType == RecommendationType.GenerateLeastPrivilegePolicy);
    }

    [Fact]
    public async Task AwsSourceStatementCondition_ShouldBeRecoveredWhenNormalizationConditionIsAbsent()
    {
        const string sourceStatement = "{\"Effect\":\"Allow\",\"Action\":\"s3:GetObject\",\"Resource\":\"arn:aws:s3:::demo/object\",\"Condition\":{\"StringEquals\":{\"aws:PrincipalTag/team\":\"finance\"}}}";
        var fixture = FixtureData.Create(Stable("CIEM-003"), RiskEvaluationState.Confirmed, "*", CloudProvider.Aws);
        var sourceEntitlement = fixture.Entitlement with
        {
            Evidence = fixture.Entitlement.Evidence with { OriginalStatement = sourceStatement }
        };
        var context = CreateUsageContext(fixture, CloudProvider.Aws, "s3:GetObject", null) with
        {
            Entitlements = new[] { sourceEntitlement }
        };

        var result = await new RecommendationEngine().EvaluateAsync(context);

        var recommendation = Assert.Single(result.Recommendations, item => item.RecommendationType == RecommendationType.GenerateLeastPrivilegePolicy);
        using var proposal = JsonDocument.Parse(recommendation.SuggestedState);
        Assert.Equal("finance", proposal.RootElement.GetProperty("providerProposal").GetProperty("Statement")[0]
            .GetProperty("Condition").GetProperty("StringEquals").GetProperty("aws:PrincipalTag/team").GetString());
        using var evidence = JsonDocument.Parse(recommendation.Evidence!);
        Assert.Contains("OriginalStatement", evidence.RootElement.GetProperty("OriginalProviderObject").GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyNormalizedCondition_ShouldNotBeDroppedFromPolicyProposal()
    {
        var fixture = FixtureData.Create(Stable("CIEM-003"), RiskEvaluationState.Confirmed, "*", CloudProvider.Gcp);
        var context = CreateUsageContext(fixture, CloudProvider.Gcp, "storage.objects.get",
            new NormalizedCondition("Conditional binding", null, "Unknown", null));

        var result = await new RecommendationEngine().EvaluateAsync(context);

        Assert.DoesNotContain(result.Recommendations, item => item.RecommendationType == RecommendationType.GenerateLeastPrivilegePolicy);
    }

    [Fact]
    public async Task AmbiguousSourceConditions_ShouldWithholdPolicyProposal()
    {
        var fixture = FixtureData.Create(Stable("CIEM-003"), RiskEvaluationState.Confirmed, "*", CloudProvider.Gcp);
        var first = fixture.Entitlement with { Condition = new NormalizedCondition("Condition A", "resource.name.endsWith('/a')", "Unknown", "resource.name.endsWith('/a')") };
        var second = fixture.Entitlement with { Id = Guid.NewGuid(), Condition = new NormalizedCondition("Condition B", "resource.name.endsWith('/b')", "Unknown", "resource.name.endsWith('/b')") };
        var context = CreateUsageContext(fixture, CloudProvider.Gcp, "storage.objects.get", null) with { Entitlements = new[] { first, second } };

        var result = await new RecommendationEngine().EvaluateAsync(context);

        Assert.DoesNotContain(result.Recommendations, item => item.RecommendationType == RecommendationType.GenerateLeastPrivilegePolicy);
    }

    [Fact]
    public async Task MissingActivityCoverage_ShouldNotGenerateLeastPrivilegePolicy()
    {
        var fixture = FixtureData.Create(Stable("CIEM-003"), RiskEvaluationState.Confirmed, "*");
        var context = fixture.Context with
        {
            ActivityRecords = new[] { new ActivityRecord { PrincipalId = fixture.Effective.PrincipalId, CloudAccountId = fixture.Effective.CloudAccountId, ResourceId = fixture.Effective.ResourceId, Action = "Microsoft.Storage/storageAccounts/read", Timestamp = DateTime.UtcNow, Success = true } }
        };

        var result = await new RecommendationEngine().EvaluateAsync(context);

        Assert.DoesNotContain(result.Recommendations, item => item.RecommendationType == RecommendationType.GenerateLeastPrivilegePolicy);
    }

    [Fact]
    public async Task CompleteRoleCoverage_ShouldGenerateUnusedRoleRemovalRecommendation()
    {
        var fixture = FixtureData.Create(Stable("CIEM-016"), RiskEvaluationState.Confirmed, "storage.read");
        var evaluatedAt = DateTime.UtcNow;
        var context = fixture.Context with
        {
            EvaluatedAt = evaluatedAt,
            ActivityCoverage = new[] { new ActivityEvidenceCoverage(fixture.Effective.CloudAccountId, fixture.Effective.Provider, evaluatedAt.AddDays(-30), evaluatedAt, ActivityCoverageStatus.Complete, true, true, true, 0) }
        };

        var result = await new RecommendationEngine().EvaluateAsync(context);

        var recommendation = Assert.Single(result.Recommendations, item => item.RecommendationType == RecommendationType.RemoveUnusedRole);
        Assert.Equal(RecommendationImpactState.RequiresReview, recommendation.ImpactState);
        Assert.Contains("unused role assignment", recommendation.SuggestedState, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownUnusedFinding_ShouldNotCreateConfirmedRemoval()
    {
        var fixture = FixtureData.Create(Stable("CIEM-008"), RiskEvaluationState.Unknown, "storage.read");
        var result = await new RecommendationEngine().EvaluateAsync(fixture.Context);

        Assert.Empty(result.Recommendations);
    }

    [Fact]
    public async Task PotentialBroadScopeFinding_ShouldRemainPotentialAndReviewable()
    {
        var fixture = FixtureData.Create(Stable("CIEM-004"), RiskEvaluationState.Potential, "storage.read");
        var result = await new RecommendationEngine().EvaluateAsync(fixture.Context);

        var recommendation = Assert.Single(result.Recommendations);
        Assert.Equal(RiskEvaluationState.Potential, recommendation.EvaluationState);
        Assert.Equal(RecommendationStatus.New, recommendation.Status);
        Assert.Contains("Current:", recommendation.Changes);
        Assert.Contains("Recommended:", recommendation.Changes);
    }

    [Fact]
    public async Task RecommendationFingerprint_ShouldDeduplicateRepeatedFindings()
    {
        var fixture = FixtureData.Create(Stable("CIEM-003"), RiskEvaluationState.Confirmed, "*");
        var context = fixture.Context with { Findings = new[] { fixture.Finding, fixture.Finding } };
        var result = await new RecommendationEngine().EvaluateAsync(context);

        Assert.Single(result.Recommendations);
        Assert.Single(result.Recommendations.Select(item => item.Fingerprint).Distinct());
    }

    [Fact]
    public void ProviderProposals_ShouldAlwaysBeNonExecutable()
    {
        var provider = new AzureRecommendationProvider();
        var recommendation = new RecommendationModel { Provider = CloudProvider.Azure, CurrentState = "Owner", SuggestedState = "Reader" };

        var proposal = provider.CreateProposal(recommendation);

        Assert.False(proposal.Executable);
    }

    private static Guid Stable(string value) => GuidUtility.Create(GuidUtility.Namespace, value);

    private static RecommendationContext CreateUsageContext(FixtureData fixture, CloudProvider provider, string action, NormalizedCondition? condition, PermissionCategory? category = null)
    {
        var evaluatedAt = DateTime.UtcNow;
        var context = fixture.Context with
        {
            EvaluatedAt = evaluatedAt,
            ActivityObservationDays = 30,
            ActivityCoverage = new[] { new ActivityEvidenceCoverage(fixture.Effective.CloudAccountId, provider, evaluatedAt.AddDays(-30), evaluatedAt, ActivityCoverageStatus.Complete, true, true, true, 1, "All configured activity sources queried") },
            ActivityRecords = new[] { new ActivityRecord { PrincipalId = fixture.Effective.PrincipalId, RoleId = fixture.Entitlement.RoleId, CloudAccountId = fixture.Effective.CloudAccountId, ResourceId = fixture.Effective.ResourceId, Action = action, Timestamp = evaluatedAt.AddDays(-1), Success = true } },
            Entitlements = new[] { fixture.Entitlement with { Condition = condition } }
        };
        if (category.HasValue)
        {
            var permission = fixture.Context.Permissions.Single();
            var evidence = new ProviderEvidence(provider, "ProviderPermission", permission.Id.ToString("D"), permission.Name, null, null,
                permission.Scope, null, evaluatedAt, new Dictionary<string, string>());
            context = context with
            {
                NormalizedPermissions = new[] { new NormalizedPermission(permission.Id, provider, permission.AccountId,
                    permission.Service ?? "storage", permission.Action ?? permission.Name, permission.Resource ?? "*", AccessType.Allow,
                    permission.Name, category.Value, ResourcePatternType.Pattern, new Dictionary<string, string>(), evidence) }
            };
        }
        return context;
    }

    private sealed record FixtureData(RiskFindingModel Finding, DomainEffectiveAccess Effective, DomainAccessPath Path, NormalizedEntitlement Entitlement, RecommendationContext Context)
    {
        public static FixtureData Create(Guid ruleId, RiskEvaluationState state, string action, CloudProvider provider = CloudProvider.Azure)
        {
            var providerType = provider switch { CloudProvider.Azure => CloudProviderType.Azure, CloudProvider.Aws => CloudProviderType.Aws, CloudProvider.Gcp => CloudProviderType.Gcp, _ => CloudProviderType.Kubernetes };
            var scope = provider switch { CloudProvider.Azure => "/subscriptions/demo", CloudProvider.Aws => "arn:aws:iam::123456789012:root", CloudProvider.Gcp => "projects/demo", _ => "cluster/demo" };
            var scopeType = provider switch { CloudProvider.Azure => NormalizedScopeType.Subscription, CloudProvider.Aws => NormalizedScopeType.Account, CloudProvider.Gcp => NormalizedScopeType.Project, _ => NormalizedScopeType.Unknown };
            var resourceId = provider switch { CloudProvider.Azure => "/subscriptions/demo/resourceGroups/rg1/providers/Microsoft.Storage/storageAccounts/demo", CloudProvider.Aws => "arn:aws:s3:::demo/object", CloudProvider.Gcp => "//storage.googleapis.com/projects/demo/buckets/demo/objects/item", _ => "cluster/demo/resource" };
            var account = new CloudAccount { Id = Guid.NewGuid(), Name = "Demo", ExternalAccountId = "demo", CloudProvider = providerType };
            var principal = new CloudPrincipal { Id = Guid.NewGuid(), AccountId = account.Id, Name = "Demo User", PrincipalId = "demo-user", CloudProvider = providerType, IdentityType = IdentityType.User };
            var resource = new CloudResource { Id = Guid.NewGuid(), AccountId = account.Id, Name = "Demo Resource", ResourceId = resourceId, Type = "Resource" };
            var permission = new CloudPermission { Id = Guid.NewGuid(), AccountId = account.Id, Name = action, Action = action, Resource = "*", AccessType = AccessType.Allow, Provider = provider.ToString() };
            var role = new CloudRole { Id = Guid.NewGuid(), AccountId = account.Id, Name = "DemoRole", RoleId = "demo-role" };
            var evidence = new RiskEvidence("Principal=Demo User; Source=demo evidence");
            var providerEvidence = new ProviderEvidence(provider, "RoleAssignment", "assignment", "Demo", "{}", "{}", scope, "Direct", DateTime.UtcNow, new Dictionary<string, string>());
            var entitlement = new NormalizedEntitlement(Guid.NewGuid(), provider, account.Id, principal.Id, resource.Id, role.Id, null, permission.Id, new NormalizedScope(provider, scopeType, scope, null, null, false), AssignmentType.Direct, NormalizedAccessType.Allow, true, false, false, null, NormalizationStatus.Normalized, providerEvidence, DateTime.UtcNow, null, "*");
            var effective = new DomainEffectiveAccess { Id = Guid.NewGuid(), Provider = provider, CloudAccountId = account.Id, PrincipalId = principal.Id, ResourceId = resource.Id, PermissionId = permission.Id, RoleId = role.Id, Decision = AccessDecision.Allow, EvaluationStatus = EvaluationStatus.FullyEvaluated, AssignmentType = AssignmentType.Direct, IsDirect = true };
            var path = new DomainAccessPath { Id = Guid.NewGuid(), Provider = provider, CloudAccountId = account.Id, PrincipalId = principal.Id, ResourceId = resource.Id, EffectiveAccessId = effective.Id, PathStatus = AccessPathStatus.Complete, PathType = AccessPathType.Direct };
            var finding = new RiskFindingModel { Id = Guid.NewGuid(), RuleId = ruleId, CloudProvider = provider, CloudAccountId = account.Id, PrincipalId = principal.Id, ResourceId = resource.Id, PermissionId = permission.Id, EffectiveAccessId = effective.Id, AccessPathId = path.Id, EvaluationState = state, Category = RiskCategory.ExcessiveAccess, Title = "Demo risk", Description = "Evidence-backed demo risk", Evidence = evidence };
            var context = new RecommendationContext(new[] { finding }, new[] { effective }, new[] { path }, new[] { entitlement }, new[] { principal }, new[] { resource }, new[] { permission }, Array.Empty<ActivityRecord>(), DateTime.UtcNow);
            return new(finding, effective, path, entitlement, context);
        }
    }

    private static class GuidUtility { public static readonly Guid Namespace = Guid.Parse("7c31cbf1-3b9f-4cc2-b9d2-0f3f0f02c6af"); public static Guid Create(Guid ns, string name) { var h = System.Security.Cryptography.SHA1.HashData(ns.ToByteArray().Concat(System.Text.Encoding.UTF8.GetBytes(name)).ToArray()); var b = h.Take(16).ToArray(); b[6] = (byte)((b[6] & 15) | 80); b[8] = (byte)((b[8] & 63) | 128); return new Guid(b); } }
}
