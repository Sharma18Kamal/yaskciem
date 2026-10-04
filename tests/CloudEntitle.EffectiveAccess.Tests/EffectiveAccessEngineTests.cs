using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.EffectiveAccess.Models;
using CloudEntitle.EffectiveAccess.Services;
using CloudEntitle.Normalization.Models;
using Xunit;

namespace CloudEntitle.EffectiveAccess.Tests;

public sealed class EffectiveAccessEngineTests
{
    [Fact]
    public async Task DirectAllow_ShouldProduceFullyEvaluatedAllow()
    {
        var fixture = FixtureData.Create(AccessType.Allow, NormalizedAccessType.Allow, AssignmentType.Direct, "/subscriptions/demo/resource");
        var result = await new EffectiveAccessEngine().EvaluateAsync(fixture.Request);

        var access = Assert.Single(result.EffectiveAccess);
        Assert.Equal(AccessDecision.Allow, access.Decision);
        Assert.Equal(EvaluationStatus.FullyEvaluated, access.EvaluationStatus);
        Assert.True(access.IsDirect);
        Assert.Contains(result.AccessPaths[access.Id].Single().Steps, step => step.Type == "Resource");
    }

    [Fact]
    public async Task ExplicitDeny_ShouldOverrideAllow()
    {
        var fixture = FixtureData.Create(AccessType.Allow, NormalizedAccessType.Allow, AssignmentType.Direct, "/subscriptions/demo/resource");
        var deny = fixture.Entitlement with { Id = Guid.NewGuid(), AccessType = NormalizedAccessType.Deny, Evidence = fixture.Entitlement.Evidence with { SourceId = "deny-rule", SourceType = "AzureDenyAssignment" } };
        var request = fixture.Request with { Normalized = fixture.Normalized with { Entitlements = new[] { fixture.Entitlement, deny } } };

        var result = await new EffectiveAccessEngine().EvaluateAsync(request);

        Assert.Equal(AccessDecision.Deny, Assert.Single(result.EffectiveAccess).Decision);
        Assert.Equal(1, result.DenyCount);
    }

    [Fact]
    public async Task ConditionalAccess_ShouldNeverBecomeAllowWithoutRuntimeContext()
    {
        var fixture = FixtureData.Create(AccessType.Allow, NormalizedAccessType.Conditional, AssignmentType.Policy, "/subscriptions/demo/resource", condition: true);

        var result = await new EffectiveAccessEngine().EvaluateAsync(fixture.Request);

        var access = Assert.Single(result.EffectiveAccess);
        Assert.Equal(AccessDecision.Conditional, access.Decision);
        Assert.Equal(EvaluationStatus.ConditionRequired, access.EvaluationStatus);
    }

    [Fact]
    public async Task SupportedAwsCondition_ShouldUseSuppliedRuntimeContext()
    {
        var fixture = FixtureData.Create(AccessType.Allow, NormalizedAccessType.Conditional, AssignmentType.Policy, "*", condition: true, provider: CloudProvider.Aws);
        var conditional = fixture.Entitlement with
        {
            Condition = new NormalizedCondition("Region", "aws:RequestedRegion == us-east-1", "Unknown", "aws:RequestedRegion == us-east-1"),
            ConditionalEffect = AccessType.Allow
        };
        var request = fixture.Request with
        {
            Normalized = fixture.Normalized with { Entitlements = new[] { conditional } },
            RuntimeConditionContext = new Dictionary<string, string> { ["aws:RequestedRegion"] = "us-east-1" }
        };

        var result = await new EffectiveAccessEngine().ExplainAsync(request, fixture.Principal.Id, fixture.Resource.Id, fixture.Permission.Id);

        Assert.Equal(AccessDecision.Allow, result.Decision);
        Assert.Equal(EvaluationStatus.FullyEvaluated, result.EvaluationStatus);
        Assert.False(result.IsConditional);
    }

    [Fact]
    public async Task AwsExplicitDeny_ShouldOverrideApplicableAllow()
    {
        var fixture = FixtureData.Create(AccessType.Allow, NormalizedAccessType.Allow, AssignmentType.Policy, "*", provider: CloudProvider.Aws);
        var deny = fixture.Entitlement with
        {
            Id = Guid.NewGuid(),
            AccessType = NormalizedAccessType.Deny,
            Evidence = fixture.Entitlement.Evidence with { SourceType = "AwsPolicyStatement", SourceId = "explicit-deny" }
        };
        var request = fixture.Request with { Normalized = fixture.Normalized with { Entitlements = new[] { fixture.Entitlement, deny } } };

        var result = await new EffectiveAccessEngine().ExplainAsync(request, fixture.Principal.Id, fixture.Resource.Id, fixture.Permission.Id);

        Assert.Equal(AccessDecision.Deny, result.Decision);
    }

    [Fact]
    public async Task GcpInheritedGrant_ShouldApplyToDescendantResource()
    {
        var fixture = FixtureData.Create(AccessType.Allow, NormalizedAccessType.Allow, AssignmentType.Inherited, "projects/demo", inherited: true, provider: CloudProvider.Gcp);
        fixture.Resource.ResourceId = "projects/demo/buckets/data";
        fixture.Resource.Scope = "projects/demo";

        var result = await new EffectiveAccessEngine().ExplainAsync(fixture.Request, fixture.Principal.Id, fixture.Resource.Id, fixture.Permission.Id);

        Assert.Equal(AccessDecision.Allow, result.Decision);
        Assert.True(result.IsInherited);
    }

    [Fact]
    public async Task AzureNotActions_ShouldNotSuppressAnotherRoleGrant()
    {
        var fixture = FixtureData.Create(AccessType.Allow, NormalizedAccessType.Allow, AssignmentType.Role, "/subscriptions/demo/resource");
        var exclusionPermission = new CloudPermission { Id = Guid.NewGuid(), AccountId = fixture.Account.Id, Name = "Role exclusion", Action = fixture.Permission.Action, Resource = "*", IsExclusion = true };
        var exclusion = fixture.Entitlement with
        {
            Id = Guid.NewGuid(),
            PermissionId = exclusionPermission.Id,
            RoleId = Guid.NewGuid(),
            IsExclusion = true,
            Evidence = fixture.Entitlement.Evidence with { SourceType = "AzureRoleDefinition", SourceId = "not-actions" }
        };
        var request = fixture.Request with
        {
            Normalized = fixture.Normalized with
            {
                Permissions = new[] { fixture.Permission, exclusionPermission },
                Entitlements = new[] { fixture.Entitlement, exclusion }
            }
        };

        var result = await new EffectiveAccessEngine().ExplainAsync(request, fixture.Principal.Id, fixture.Resource.Id, fixture.Permission.Id);

        Assert.Equal(AccessDecision.Allow, result.Decision);
    }

    [Fact]
    public async Task AzureNotActions_ShouldSuppressOnlyItsOwnRoleGrant()
    {
        var fixture = FixtureData.Create(AccessType.Allow, NormalizedAccessType.Allow, AssignmentType.Role, "/subscriptions/demo/resource");
        var exclusionPermission = new CloudPermission { Id = Guid.NewGuid(), AccountId = fixture.Account.Id, Name = "Role exclusion", Action = fixture.Permission.Action, Resource = "*", IsExclusion = true };
        var exclusion = fixture.Entitlement with { Id = Guid.NewGuid(), PermissionId = exclusionPermission.Id, IsExclusion = true };
        var request = fixture.Request with
        {
            Normalized = fixture.Normalized with
            {
                Permissions = new[] { fixture.Permission, exclusionPermission },
                Entitlements = new[] { fixture.Entitlement, exclusion }
            }
        };

        var result = await new EffectiveAccessEngine().ExplainAsync(request, fixture.Principal.Id, fixture.Resource.Id, fixture.Permission.Id);

        Assert.Equal(AccessDecision.NotApplicable, result.Decision);
        Assert.Contains(result.AccessPaths, path => path.Decision == "Excluded");
    }

    [Fact]
    public async Task ScopeResolution_ShouldNotMatchPartialResourceIdPrefix()
    {
        var fixture = FixtureData.Create(AccessType.Allow, NormalizedAccessType.Allow, AssignmentType.Direct, "/subscriptions/demo");
        var unrelated = new CloudResource { Id = Guid.NewGuid(), AccountId = fixture.Account.Id, Name = "neighbor", ResourceId = "/subscriptions/demonstration/resource", Type = "Resource" };

        var result = await new EffectiveAccessEngine().ExplainAsync(fixture.Request with { Resources = new[] { unrelated } }, fixture.Principal.Id, unrelated.Id, fixture.Permission.Id);

        Assert.Equal(AccessDecision.NotApplicable, result.Decision);
    }

    [Fact]
    public async Task ExpiredGroupMembership_ShouldNotGrantAccess()
    {
        var fixture = FixtureData.Create(AccessType.Allow, NormalizedAccessType.Allow, AssignmentType.Group, "/subscriptions/demo/resource");
        var groupId = Guid.NewGuid();
        var groupGrant = fixture.Entitlement with { PrincipalId = groupId, IsDirect = false };
        var request = fixture.Request with
        {
            Normalized = fixture.Normalized with { Entitlements = new[] { groupGrant } },
            GroupMemberships = new[] { new GroupMembership { PrincipalId = fixture.Principal.Id, GroupId = groupId, CloudAccountId = fixture.Account.Id, ExpiresAt = DateTime.UtcNow.AddDays(-1) } }
        };

        var result = await new EffectiveAccessEngine().ExplainAsync(request, fixture.Principal.Id, fixture.Resource.Id, fixture.Permission.Id);

        Assert.Equal(AccessDecision.NotApplicable, result.Decision);
    }

    [Fact]
    public async Task AwsRoleTrust_ShouldAppearInAccessExplanation()
    {
        var fixture = FixtureData.Create(AccessType.Allow, NormalizedAccessType.Allow, AssignmentType.AssumedRole, "*", provider: CloudProvider.Aws);
        var rolePrincipal = new CloudPrincipal { Id = Guid.NewGuid(), AccountId = fixture.Account.Id, CloudProvider = CloudProviderType.Aws, Name = "app-role", PrincipalId = "arn:aws:iam::123456789012:role/app", IdentityType = IdentityType.Role };
        var roleGrant = fixture.Entitlement with { PrincipalId = rolePrincipal.Id };
        var request = fixture.Request with
        {
            Normalized = fixture.Normalized with { Principals = new[] { fixture.Principal, rolePrincipal }, Entitlements = new[] { roleGrant } },
            RoleTrustRelationships = new[] { new RoleTrustRelationship(CloudProvider.Aws, fixture.Account.Id, fixture.Principal.Id, null, rolePrincipal.Id, roleGrant.RoleId!.Value, "trust-policy", "AssumeRole trust policy") }
        };

        var result = await new EffectiveAccessEngine().ExplainAsync(request, fixture.Principal.Id, fixture.Resource.Id, fixture.Permission.Id);

        Assert.Equal(AccessDecision.Allow, result.Decision);
        Assert.Contains(result.AccessPaths.SelectMany(path => path.Steps), step => step.Type == "AssumeRole");
    }

    [Fact]
    public async Task GroupAndInheritedAccess_ShouldResolveNestedMembershipAndScope()
    {
        var fixture = FixtureData.Create(AccessType.Allow, NormalizedAccessType.Allow, AssignmentType.Group, "/subscriptions/demo", inherited: true);
        var groupA = Guid.NewGuid();
        var groupB = fixture.Entitlement.PrincipalId;
        var memberships = new[]
        {
            new GroupMembership { Id = Guid.NewGuid(), PrincipalId = fixture.Principal.Id, GroupId = groupA, CloudAccountId = fixture.Account.Id },
            new GroupMembership { Id = Guid.NewGuid(), PrincipalId = groupA, GroupId = groupB, CloudAccountId = fixture.Account.Id },
            new GroupMembership { Id = Guid.NewGuid(), PrincipalId = groupB, GroupId = groupA, CloudAccountId = fixture.Account.Id }
        };
        var entitlement = fixture.Entitlement with { PrincipalId = groupB };
        var request = fixture.Request with { GroupMemberships = memberships, Normalized = fixture.Normalized with { Entitlements = new[] { entitlement } } };

        var result = await new EffectiveAccessEngine().EvaluateAsync(request);

        var access = Assert.Single(result.EffectiveAccess);
        Assert.Equal(AccessDecision.Allow, access.Decision);
        Assert.True(access.IsInherited);
        Assert.Equal(AssignmentType.Group, access.AssignmentType);
    }

    [Fact]
    public async Task UnrelatedResource_ShouldBeNotApplicableAndDeterministic()
    {
        var fixture = FixtureData.Create(AccessType.Allow, NormalizedAccessType.Allow, AssignmentType.Direct, "/subscriptions/demo/one");
        var other = new CloudResource { Id = Guid.NewGuid(), AccountId = fixture.Account.Id, Name = "other-resource", ResourceId = "/subscriptions/demo/two", Type = "Resource" };
        var request = fixture.Request with { Resources = new[] { other } };

        var result = await new EffectiveAccessEngine().EvaluateAsync(request);

        Assert.Empty(result.EffectiveAccess);
    }

    private sealed record FixtureData(CloudAccount Account, CloudPrincipal Principal, CloudResource Resource, CloudPermission Permission, NormalizedEntitlement Entitlement, ProviderDiscoveryResult NormalizedSource, NormalizationResult Normalized, AccessEvaluationRequest Request)
    {
        public static FixtureData Create(AccessType permissionAccess, NormalizedAccessType entitlementAccess, AssignmentType assignment, string scope, bool condition = false, bool inherited = false, CloudProvider provider = CloudProvider.Azure)
        {
            var providerType = provider switch { CloudProvider.Azure => CloudProviderType.Azure, CloudProvider.Aws => CloudProviderType.Aws, CloudProvider.Gcp => CloudProviderType.Gcp, _ => CloudProviderType.Kubernetes };
            var account = new CloudAccount { Id = Guid.NewGuid(), Name = "Demo", ExternalAccountId = "demo", CloudProvider = providerType };
            var principal = new CloudPrincipal { Id = Guid.NewGuid(), AccountId = account.Id, Name = "demo-user", PrincipalId = "demo-user", CloudProvider = providerType, IdentityType = IdentityType.User };
            var resource = new CloudResource { Id = Guid.NewGuid(), AccountId = account.Id, Name = "demo-resource", ResourceId = "/subscriptions/demo/resource", Type = "Resource" };
            var permission = new CloudPermission { Id = Guid.NewGuid(), AccountId = account.Id, Name = "Read", Action = "storage.read", Resource = "*", AccessType = permissionAccess, Provider = provider.ToString() };
            var evidence = new ProviderEvidence(provider, "RoleAssignment", "assignment", "Demo", "{}", "{}", scope, assignment.ToString(), DateTime.UtcNow, new Dictionary<string, string>());
            var entitlement = new NormalizedEntitlement(Guid.NewGuid(), provider, account.Id, principal.Id, null, Guid.NewGuid(), null, permission.Id, new NormalizedScope(provider, NormalizedScopeType.Subscription, scope, null, null, inherited), assignment, entitlementAccess, !inherited && assignment == AssignmentType.Direct, inherited, condition, condition ? new NormalizedCondition("condition", "request.time", "Unknown", "request.time") : null, condition ? NormalizationStatus.PartiallyNormalized : NormalizationStatus.Normalized, evidence, DateTime.UtcNow, null, scope);
            var normalized = new NormalizationResult(new[] { principal }, Array.Empty<CloudRole>(), Array.Empty<CloudPolicy>(), new[] { permission }, new[] { entitlement }, Array.Empty<NormalizationWarning>(), Array.Empty<string>(), new(1, 1, condition ? 1 : 0, 0, 0));
            var source = new ProviderDiscoveryResult(provider, new[] { account }, new[] { principal }, Array.Empty<CloudRole>(), Array.Empty<CloudPolicy>(), new[] { permission }, Array.Empty<ProviderAuthorizationRecord>(), DateTime.UtcNow);
            return new(account, principal, resource, permission, entitlement, source, normalized, new AccessEvaluationRequest(normalized, new[] { resource }, Array.Empty<GroupMembership>()));
        }
    }
}
