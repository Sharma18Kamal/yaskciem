using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.EffectiveAccess.Models;
using CloudEntitle.EffectiveAccess.Services;
using CloudEntitle.Normalization.Models;
using Xunit;

namespace CloudEntitle.EffectiveAccess.Tests;

public sealed class WhatIfPermissionSimulatorTests
{
    [Theory]
    [InlineData(CloudProvider.Azure, "Microsoft.Storage/storageAccounts/listKeys/action")]
    [InlineData(CloudProvider.Aws, "s3:GetObject")]
    [InlineData(CloudProvider.Gcp, "storage.objects.get")]
    public async Task ExactActionRemoval_ReevaluatesSameTupleWithoutMutatingInput(CloudProvider provider, string action)
    {
        var fixture = Create(provider, action);
        var original = JsonSerializer.Serialize(fixture.Input);
        var report = await Simulator().SimulateAsync(fixture.Input, fixture.Change);
        var row = Assert.Single(report.AccessChanges);
        Assert.Equal(AccessDecision.Allow, row.Before);
        Assert.Equal(AccessDecision.NotApplicable, row.After);
        Assert.Equal(WhatIfImpact.ModeledAccessLost, row.Impact);
        Assert.Equal(100m, report.ModeledExposureReductionPercent);
        Assert.Equal(1, report.ModeledLostPairs);
        Assert.Equal(64, report.InputSha256.Length);
        Assert.Equal(64, Assert.Single(row.RemovedOrigins).EvidenceSha256.Length);
        Assert.False(report.MutatesCloud);
        Assert.False(report.IsProviderVerified);
        Assert.Equal(original, JsonSerializer.Serialize(fixture.Input));
    }

    [Theory]
    [InlineData(CloudProvider.Azure)]
    [InlineData(CloudProvider.Aws)]
    [InlineData(CloudProvider.Gcp)]
    public async Task IndependentGrant_RetainsSameActionAndShowsAlternativeEvidence(CloudProvider provider)
    {
        var fixture = Create(provider, Action(provider));
        var normalized = fixture.Input.Evaluation.Normalized;
        var alternativeRole = new CloudRole { Id = Guid.NewGuid(), AccountId = fixture.Change.AccountId, Name = "Alternative" };
        var alternative = normalized.Entitlements[0] with
        {
            Id = Guid.NewGuid(), RoleId = alternativeRole.Id,
            Evidence = normalized.Entitlements[0].Evidence with { SourceId = "independent-grant" }
        };
        var input = fixture.Input with { Evaluation = fixture.Input.Evaluation with
        { Normalized = normalized with { Roles = normalized.Roles.Append(alternativeRole).ToArray(),
            Entitlements = normalized.Entitlements.Append(alternative).ToArray() } } };
        var report = await Simulator().SimulateAsync(input, fixture.Change);
        var row = Assert.Single(report.AccessChanges);
        Assert.Equal(WhatIfImpact.RetainedByAlternative, row.Impact);
        Assert.Equal(AccessDecision.Allow, row.After);
        Assert.Equal("independent-grant", Assert.Single(row.AlternativeOrigins).SourceId);
        Assert.Equal(0m, report.ModeledExposureReductionPercent);
    }

    [Fact]
    public async Task AzureListKeys_ShowsNestedGroupAndSubscriptionInheritanceOnlyForStorage()
    {
        var fixture = Create(CloudProvider.Azure, Action(CloudProvider.Azure));
        var normalized = fixture.Input.Evaluation.Normalized;
        var group = new CloudPrincipal { Id = Guid.NewGuid(), AccountId = fixture.Change.AccountId,
            CloudProvider = CloudProviderType.Azure, Name = "Storage admins", IdentityType = IdentityType.Group };
        var grant = normalized.Entitlements[0] with { PrincipalId = group.Id };
        var vm = new CloudResource { Id = Guid.NewGuid(), AccountId = fixture.Change.AccountId, Name = "VM",
            Type = "Microsoft.Compute/virtualMachines", ResourceId = "/subscriptions/demo/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm" };
        var input = fixture.Input with { Evaluation = fixture.Input.Evaluation with
        {
            Normalized = normalized with { Principals = normalized.Principals.Append(group).ToArray(), Entitlements = new[] { grant } },
            Resources = fixture.Input.Evaluation.Resources.Append(vm).ToArray(),
            GroupMemberships = new[] { new GroupMembership { Id = Guid.NewGuid(), CloudAccountId = fixture.Change.AccountId,
                GroupId = group.Id, PrincipalId = fixture.PrincipalId, MembershipType = MembershipType.Direct } }
        } };
        var report = await Simulator().SimulateAsync(input, fixture.Change);
        Assert.Equal(2, report.AccessChanges.Count);
        var user = Assert.Single(report.AccessChanges, item => item.PrincipalId == fixture.PrincipalId);
        Assert.True(user.IsInherited);
        Assert.Contains(user.BeforePaths.SelectMany(item => item.Steps), item => item.Type == "Group");
        Assert.DoesNotContain(report.AccessChanges, item => item.ResourceId == vm.Id);
        Assert.Contains("does not invalidate existing keys", report.RiskExplanation);
    }

    [Theory]
    [InlineData(CloudProvider.Azure, "AzureDenyAssignment")]
    [InlineData(CloudProvider.Aws, "AwsPolicyStatement")]
    [InlineData(CloudProvider.Gcp, "GcpDenyPolicy")]
    public async Task ExplicitDeny_IsPreservedAndNeverReportedAsRiskReduction(CloudProvider provider, string denyType)
    {
        var fixture = Create(provider, Action(provider));
        var normalized = fixture.Input.Evaluation.Normalized;
        var deny = normalized.Entitlements[0] with { Id = Guid.NewGuid(), RoleId = null, AccessType = NormalizedAccessType.Deny,
            Evidence = normalized.Entitlements[0].Evidence with { SourceId = "deny", SourceType = denyType } };
        var input = fixture.Input with { Evaluation = fixture.Input.Evaluation with
            { Normalized = normalized with { Entitlements = normalized.Entitlements.Append(deny).ToArray() } } };
        var report = await Simulator().SimulateAsync(input, fixture.Change);
        var row = Assert.Single(report.AccessChanges);
        Assert.Equal(AccessDecision.Deny, row.Before);
        Assert.Equal(AccessDecision.Deny, row.After);
        Assert.Equal(WhatIfImpact.AlreadyNotAllowed, row.Impact);
        Assert.Null(report.ModeledExposureReductionPercent);
    }

    [Fact]
    public async Task AzureWildcard_ModelsOnlySelectedActionWithoutRemovingOtherPermissions()
    {
        var fixture = Create(CloudProvider.Azure, "Microsoft.Storage/*");
        var change = fixture.Change with { Action = Action(CloudProvider.Azure) };
        var report = await Simulator().SimulateAsync(fixture.Input, change);
        Assert.Equal(WhatIfImpact.ModeledAccessLost, Assert.Single(report.AccessChanges).Impact);
        Assert.Equal("Microsoft.Storage/*", fixture.Input.Evaluation.Normalized.Permissions[0].Action);
    }

    [Theory]
    [InlineData(CloudProvider.Aws)]
    [InlineData(CloudProvider.Gcp)]
    public async Task WildcardRemoval_NeverCreatesUnsafeGlobalDeny(CloudProvider provider)
    {
        var fixture = Create(provider, "*");
        await Assert.ThrowsAsync<NotSupportedException>(() => Simulator().SimulateAsync(fixture.Input,
            fixture.Change with { Action = Action(provider) }));
    }

    [Fact]
    public async Task PartialCoverage_DoesNotClaimLostPermissionOrNumericRiskReduction()
    {
        var fixture = Create(CloudProvider.Azure, Action(CloudProvider.Azure));
        var input = fixture.Input with { Limitations = new[] { "Missing deny inventory" } };
        var report = await Simulator().SimulateAsync(input, fixture.Change);
        Assert.Equal(WhatIfImpact.PotentialAccessLoss, Assert.Single(report.AccessChanges).Impact);
        Assert.Equal(0, report.ModeledLostPairs);
        Assert.Null(report.ModeledExposureReductionPercent);
    }

    [Fact]
    public async Task UnresolvedCondition_IsNotAnAllowedOrConfirmedLostPermission()
    {
        var fixture = Create(CloudProvider.Gcp, Action(CloudProvider.Gcp));
        var normalized = fixture.Input.Evaluation.Normalized;
        var conditional = normalized.Entitlements[0] with { AccessType = NormalizedAccessType.Conditional, IsConditional = true,
            Condition = new("Time", "request.time < timestamp('2027-01-01T00:00:00Z')", "Unknown", null) };
        var input = fixture.Input with { Evaluation = fixture.Input.Evaluation with { Normalized = normalized with { Entitlements = new[] { conditional } } } };
        var report = await Simulator().SimulateAsync(input, fixture.Change);
        Assert.Equal(AccessDecision.Conditional, Assert.Single(report.AccessChanges).Before);
        Assert.Equal(WhatIfImpact.PotentialAccessLoss, Assert.Single(report.AccessChanges).Impact);
        Assert.Null(report.ModeledExposureReductionPercent);
    }

    [Fact]
    public async Task ForeignScopeAndMissingTargets_RejectRatherThanReturnSuccess()
    {
        var fixture = Create(CloudProvider.Azure, Action(CloudProvider.Azure));
        await Assert.ThrowsAsync<ArgumentException>(() => Simulator().SimulateAsync(fixture.Input,
            fixture.Change with { AccountId = Guid.NewGuid() }));
        await Assert.ThrowsAsync<ArgumentException>(() => Simulator().SimulateAsync(fixture.Input,
            fixture.Change with { TargetId = Guid.NewGuid() }));
        await Assert.ThrowsAsync<ArgumentException>(() => Simulator().SimulateAsync(fixture.Input,
            fixture.Change with { Action = "*" }));
    }

    [Fact]
    public async Task ExpiredGrant_DoesNotBecomeActiveInOfflineScenario()
    {
        var fixture = Create(CloudProvider.Azure, Action(CloudProvider.Azure));
        var normalized = fixture.Input.Evaluation.Normalized;
        var input = fixture.Input with { Evaluation = fixture.Input.Evaluation with { Normalized = normalized with
            { Entitlements = new[] { normalized.Entitlements[0] with { ExpiresAt = DateTime.UtcNow.AddDays(-1) } } } } };
        await Assert.ThrowsAsync<ArgumentException>(() => Simulator().SimulateAsync(input, fixture.Change));
    }

    [Fact]
    public async Task BoundedScenarios_RejectBeforeTruncatingPairs()
    {
        var fixture = Create(CloudProvider.Aws, Action(CloudProvider.Aws));
        var resources = Enumerable.Range(0, 10001).Select(index => new CloudResource { Id = Guid.NewGuid(),
            AccountId = fixture.Change.AccountId, Name = index.ToString(), ResourceId = $"arn:aws:s3:::data/{index}" }).ToArray();
        var input = fixture.Input with { Evaluation = fixture.Input.Evaluation with { Resources = resources } };
        await Assert.ThrowsAsync<ArgumentException>(() => Simulator().SimulateAsync(input, fixture.Change));
    }

    [Fact]
    public async Task OfflineBundle_RejectsDuplicateFieldsUnknownSchemaAndOversize()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => WhatIfPermissionSimulator.ImportAsync(
            """{"SchemaVersion":"1.0","schemaversion":"2.0"}"""));
        await Assert.ThrowsAsync<ArgumentException>(() => WhatIfPermissionSimulator.ImportAsync(new string(' ', 8 * 1024 * 1024 + 1)));
        await Assert.ThrowsAsync<ArgumentException>(() => WhatIfPermissionSimulator.ImportAsync(
            """{"SchemaVersion":"2.0","SourceLabel":"test","Discovery":null,"Resources":[],"GroupMemberships":[],"Limitations":[]}"""));
    }

    [Theory]
    [InlineData(CloudProvider.Azure)]
    [InlineData(CloudProvider.Aws)]
    [InlineData(CloudProvider.Gcp)]
    public async Task OfflineProviderExportProjection_NormalizesAndSimulatesWithoutCredentials(CloudProvider provider)
    {
        var fixture = Create(provider, Action(provider));
        var normalized = fixture.Input.Evaluation.Normalized;
        var grant = normalized.Entitlements[0];
        var discovery = new ProviderDiscoveryResult(provider, new[] { new CloudAccount { Id = fixture.Change.AccountId,
            CloudProvider = (CloudProviderType)provider } }, normalized.Principals, normalized.Roles, normalized.Policies,
            normalized.Permissions, new[] { new ProviderAuthorizationRecord("export-policy", "Role", fixture.Change.AccountId,
                fixture.PrincipalId, fixture.Change.TargetId, null, grant.PermissionId, grant.Scope.OriginalScope, "*",
                AssignmentType.Role, AccessType.Allow, "ExportedProviderPolicy", "native document", "native statement",
                null, null, true, grant.Scope.OriginalScope, null) }, DateTime.UtcNow);
        var bundle = new OfflineAuthorizationBundle("1.0", "Client offline export", discovery,
            fixture.Input.Evaluation.Resources, Array.Empty<GroupMembership>(), Array.Empty<string>());
        var json = JsonSerializer.Serialize(bundle, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });
        var input = await WhatIfPermissionSimulator.ImportAsync(json);
        var report = await Simulator().SimulateAsync(input, fixture.Change);
        Assert.Equal(WhatIfImpact.ModeledAccessLost, Assert.Single(report.AccessChanges).Impact);
        Assert.Equal("native statement", Assert.Single(report.AccessChanges[0].RemovedOrigins).OriginalStatement);
    }

    private static WhatIfPermissionSimulator Simulator() => new(new EffectiveAccessEngine());
    [Fact]
    public async Task DynamicMembership_PreventsDefinitiveExposureReduction()
    {
        var fixture = Create(CloudProvider.Azure, Action(CloudProvider.Azure));
        var input = fixture.Input with { Evaluation = fixture.Input.Evaluation with { GroupMemberships = new[]
        {
            new GroupMembership { CloudAccountId = fixture.Change.AccountId, GroupId = fixture.PrincipalId,
                PrincipalId = fixture.PrincipalId, MembershipType = MembershipType.Dynamic }
        } } };
        var report = await Simulator().SimulateAsync(input, fixture.Change);
        Assert.Equal(WhatIfImpact.PotentialAccessLoss, Assert.Single(report.AccessChanges).Impact);
        Assert.Null(report.ModeledExposureReductionPercent);
        Assert.Contains(report.Limitations, item => item.Contains("Dynamic or unknown"));
    }

    [Fact]
    public async Task GcpActionNames_AreNotCaseInsensitive()
    {
        var fixture = Create(CloudProvider.Gcp, Action(CloudProvider.Gcp));
        await Assert.ThrowsAsync<ArgumentException>(() => Simulator().SimulateAsync(fixture.Input,
            fixture.Change with { Action = "storage.Objects.get" }));
    }

    [Theory]
    [InlineData("Resources")]
    [InlineData("Limitations")]
    [InlineData("Discovery")]
    public async Task NullRequiredBundleCollections_AreExplicitErrors(string field)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var text = await File.ReadAllTextAsync(Path.Combine(root, "docs", "architecture", "what-if-simulator.md"));
        var json = text.Split("```json", StringSplitOptions.None)[1].Split("```", StringSplitOptions.None)[0];
        var bundle = JsonNode.Parse(json)!;
        bundle[field] = null;
        await Assert.ThrowsAsync<ArgumentException>(() => WhatIfPermissionSimulator.ImportAsync(
            bundle.ToJsonString()));
    }

    [Fact]
    public async Task NormalizationWorkBudget_RejectsBeforeExpensiveReferenceScans()
    {
        var fixture = Create(CloudProvider.Azure, Action(CloudProvider.Azure));
        var normalized = fixture.Input.Evaluation.Normalized;
        var grant = normalized.Entitlements[0];
        var record = new ProviderAuthorizationRecord("export-policy", "Role", fixture.Change.AccountId,
            fixture.PrincipalId, fixture.Change.TargetId, null, grant.PermissionId, grant.Scope.OriginalScope, "*",
            AssignmentType.Role, AccessType.Allow, "ExportedProviderPolicy", null, null, null, null,
            true, grant.Scope.OriginalScope, null);
        var discovery = new ProviderDiscoveryResult(CloudProvider.Azure,
            new[] { new CloudAccount { Id = fixture.Change.AccountId, CloudProvider = CloudProviderType.Azure } },
            normalized.Principals, normalized.Roles, normalized.Policies,
            Enumerable.Repeat(normalized.Permissions[0], 2000).ToArray(),
            Enumerable.Repeat(record, 5000).ToArray(), DateTime.UtcNow);
        var bundle = new OfflineAuthorizationBundle("1.0", "Bound test", discovery,
            fixture.Input.Evaluation.Resources, Array.Empty<GroupMembership>(), Array.Empty<string>());
        var json = JsonSerializer.Serialize(bundle, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });
        var error = await Assert.ThrowsAsync<ArgumentException>(() => WhatIfPermissionSimulator.ImportAsync(json));
        Assert.Contains("normalization/reference work budget", error.Message);
    }
    [Fact]
    public async Task AzureNotActions_IsPreservedWhenSelectedGrantIsRemoved()
    {
        var fixture = Create(CloudProvider.Azure, Action(CloudProvider.Azure));
        var normalized = fixture.Input.Evaluation.Normalized;
        var exclusion = normalized.Entitlements[0] with { Id = Guid.NewGuid(), IsExclusion = true,
            Evidence = normalized.Entitlements[0].Evidence with { SourceId = "role-exclusion", OriginalAssignment = "NotActions" } };
        var input = fixture.Input with { Evaluation = fixture.Input.Evaluation with { Normalized = normalized with
            { Entitlements = normalized.Entitlements.Append(exclusion).ToArray() } } };
        var report = await Simulator().SimulateAsync(input, fixture.Change);
        var row = Assert.Single(report.AccessChanges);
        Assert.NotEqual(AccessDecision.Allow, row.Before);
        Assert.Equal(AccessDecision.NotApplicable, row.After);
        Assert.Equal(0, report.ModeledLostPairs);
        Assert.Null(report.ModeledExposureReductionPercent);
        Assert.Contains(input.Evaluation.Normalized.Entitlements, item => item.IsExclusion);
    }
    [Fact]
    public async Task DocumentedOfflineExample_ImportsAndReportsOnlyPotentialImpact()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var text = await File.ReadAllTextAsync(Path.Combine(root, "docs", "architecture", "what-if-simulator.md"));
        var json = text.Split("```json", StringSplitOptions.None)[1].Split("```", StringSplitOptions.None)[0];
        var input = await WhatIfPermissionSimulator.ImportAsync(json);
        var normalized = input.Evaluation.Normalized;
        var report = await Simulator().SimulateAsync(input, new(CloudProvider.Azure,
            normalized.Principals[0].AccountId, WhatIfTargetKind.Role, normalized.Roles[0].Id, Action(CloudProvider.Azure)));
        Assert.Equal(WhatIfImpact.PotentialAccessLoss, Assert.Single(report.AccessChanges).Impact);
        Assert.Null(report.ModeledExposureReductionPercent);
    }
    [Fact]
    public async Task PolicyTarget_ChangesOnlyTheChosenPolicyAndNotOtherActions()
    {
        var fixture = Create(CloudProvider.Aws, Action(CloudProvider.Aws));
        var normalized = fixture.Input.Evaluation.Normalized;
        var policy = new CloudPolicy { Id = Guid.NewGuid(), AccountId = fixture.Change.AccountId, Name = "Inline policy" };
        var grant = normalized.Entitlements[0] with { RoleId = null, PolicyId = policy.Id };
        var input = fixture.Input with { Evaluation = fixture.Input.Evaluation with { Normalized = normalized with
            { Policies = new[] { policy }, Entitlements = new[] { grant } } } };
        var report = await Simulator().SimulateAsync(input, fixture.Change with { TargetKind = WhatIfTargetKind.Policy, TargetId = policy.Id });
        Assert.Equal(WhatIfImpact.ModeledAccessLost, Assert.Single(report.AccessChanges).Impact);
        Assert.Equal(policy.Id, report.AccessChanges[0].RemovedOrigins[0].PolicyId);
    }

    [Fact]
    public async Task DistinctKeyAction_IsNotAutomaticallyAnEquivalentPermission()
    {
        var fixture = Create(CloudProvider.Azure, Action(CloudProvider.Azure));
        var normalized = fixture.Input.Evaluation.Normalized;
        var permission = new CloudPermission { Id = Guid.NewGuid(), AccountId = fixture.Change.AccountId,
            Action = "Microsoft.Storage/storageAccounts/regenerateKey/action" };
        var alternate = normalized.Entitlements[0] with { Id = Guid.NewGuid(), RoleId = null, PermissionId = permission.Id };
        var input = fixture.Input with { Evaluation = fixture.Input.Evaluation with { Normalized = normalized with
            { Permissions = normalized.Permissions.Append(permission).ToArray(), Entitlements = normalized.Entitlements.Append(alternate).ToArray() } } };
        var report = await Simulator().SimulateAsync(input, fixture.Change);
        Assert.Equal(WhatIfImpact.ModeledAccessLost, Assert.Single(report.AccessChanges).Impact);
        Assert.Empty(report.AccessChanges[0].AlternativeOrigins);
    }
    private static string Action(CloudProvider provider) => provider switch
    {
        CloudProvider.Azure => "Microsoft.Storage/storageAccounts/listKeys/action",
        CloudProvider.Aws => "s3:GetObject", _ => "storage.objects.get"
    };
    private static Fixture Create(CloudProvider provider, string action)
    {
        var account = Guid.NewGuid();
        var principal = new CloudPrincipal { Id = Guid.NewGuid(), AccountId = account, CloudProvider = (CloudProviderType)provider, Name = "Analyst" };
        var role = new CloudRole { Id = Guid.NewGuid(), AccountId = account, Name = "Selected role" };
        var permission = new CloudPermission { Id = Guid.NewGuid(), AccountId = account, Provider = provider.ToString(), Action = action, Name = action };
        var resource = new CloudResource { Id = Guid.NewGuid(), AccountId = account, Name = "Data",
            Type = "Microsoft.Storage/storageAccounts", ResourceId = provider == CloudProvider.Azure
                ? "/subscriptions/demo/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/data"
                : provider == CloudProvider.Aws ? "arn:aws:s3:::data/object" : "projects/demo/buckets/data" };
        var scope = provider == CloudProvider.Azure ? "/subscriptions/demo" : provider == CloudProvider.Gcp ? "projects/demo" : "*";
        var now = DateTime.UtcNow;
        var evidence = new ProviderEvidence(provider, "ExportedProviderPolicy", "selected-role", "Role", "document", "statement",
            scope, "Role", now, new Dictionary<string, string>());
        var grant = new NormalizedEntitlement(Guid.NewGuid(), provider, account, principal.Id, null, role.Id, null, permission.Id,
            new(provider, NormalizedScopeType.Account, scope, scope, null, true), AssignmentType.Role, NormalizedAccessType.Allow,
            false, true, false, null, NormalizationStatus.Normalized, evidence, now.AddDays(-1), null, "*");
        var normalized = new NormalizationResult(new[] { principal }, new[] { role }, Array.Empty<CloudPolicy>(),
            new[] { permission }, new[] { grant }, Array.Empty<NormalizationWarning>(), Array.Empty<string>(), new(1, 1, 0, 0, 0));
        return new(new(new(normalized, new[] { resource }, Array.Empty<GroupMembership>(), EvaluationTime: now),
            "Offline test", Array.Empty<string>()), new(provider, account, WhatIfTargetKind.Role, role.Id, action), principal.Id);
    }
    private sealed record Fixture(WhatIfInput Input, WhatIfChange Change, Guid PrincipalId);
}
