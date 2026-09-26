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
    }

    [Fact]
    public async Task ExplicitDeny_ShouldOverrideAllow()
    {
        var fixture = FixtureData.Create(AccessType.Allow, NormalizedAccessType.Allow, AssignmentType.Direct, "/subscriptions/demo/resource");
        var deny = fixture.Entitlement with { Id = Guid.NewGuid(), AccessType = NormalizedAccessType.Deny, Evidence = fixture.Entitlement.Evidence with { SourceId = "deny-rule" } };
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
        public static FixtureData Create(AccessType permissionAccess, NormalizedAccessType entitlementAccess, AssignmentType assignment, string scope, bool condition = false, bool inherited = false)
        {
            var account = new CloudAccount { Id = Guid.NewGuid(), Name = "Demo", ExternalAccountId = "demo", CloudProvider = CloudProviderType.Azure };
            var principal = new CloudPrincipal { Id = Guid.NewGuid(), AccountId = account.Id, Name = "demo-user", PrincipalId = "demo-user", CloudProvider = CloudProviderType.Azure, IdentityType = IdentityType.User };
            var resource = new CloudResource { Id = Guid.NewGuid(), AccountId = account.Id, Name = "demo-resource", ResourceId = "/subscriptions/demo/resource", Type = "Resource" };
            var permission = new CloudPermission { Id = Guid.NewGuid(), AccountId = account.Id, Name = "Read", Action = "storage.read", Resource = "*", AccessType = permissionAccess };
            var evidence = new ProviderEvidence(CloudProvider.Azure, "RoleAssignment", "assignment", "Demo", "{}", "{}", scope, assignment.ToString(), DateTime.UtcNow, new Dictionary<string, string>());
            var entitlement = new NormalizedEntitlement(Guid.NewGuid(), CloudProvider.Azure, account.Id, principal.Id, null, Guid.NewGuid(), null, permission.Id, new NormalizedScope(CloudProvider.Azure, NormalizedScopeType.Subscription, scope, null, null, inherited), assignment, entitlementAccess, !inherited && assignment == AssignmentType.Direct, inherited, condition, condition ? new NormalizedCondition("condition", "request.time", "Unknown", "request.time") : null, condition ? NormalizationStatus.PartiallyNormalized : NormalizationStatus.Normalized, evidence, DateTime.UtcNow, null, scope);
            var normalized = new NormalizationResult(new[] { principal }, Array.Empty<CloudRole>(), Array.Empty<CloudPolicy>(), new[] { permission }, new[] { entitlement }, Array.Empty<NormalizationWarning>(), Array.Empty<string>(), new(1, 1, condition ? 1 : 0, 0, 0));
            var source = new ProviderDiscoveryResult(CloudProvider.Azure, new[] { account }, new[] { principal }, Array.Empty<CloudRole>(), Array.Empty<CloudPolicy>(), new[] { permission }, Array.Empty<ProviderAuthorizationRecord>(), DateTime.UtcNow);
            return new(account, principal, resource, permission, entitlement, source, normalized, new AccessEvaluationRequest(normalized, new[] { resource }, Array.Empty<GroupMembership>()));
        }
    }
}
