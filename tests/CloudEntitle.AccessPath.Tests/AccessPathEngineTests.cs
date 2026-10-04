using CloudEntitle.AccessPath.Models;
using CloudEntitle.AccessPath.Services;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Normalization.Models;
using Xunit;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;

namespace CloudEntitle.AccessPath.Tests;

public sealed class AccessPathEngineTests
{
    [Fact]
    public async Task DirectPath_ShouldContainEvidenceBackedNodesAndEdges()
    {
        var fixture = FixtureData.Create(AssignmentType.Direct, inherited: false);
        var result = await new AccessPathEngine().BuildAsync(fixture.Request);

        var path = Assert.Single(result.Paths);
        Assert.Equal(AccessPathStatus.Complete, path.PathStatus);
        Assert.Equal(AccessPathType.Direct, path.PathType);
        Assert.True(path.Nodes.Count >= 5);
        Assert.Equal(path.Nodes.Count - 1, path.Edges.Count);
        Assert.All(path.Nodes, node => Assert.False(string.IsNullOrWhiteSpace(node.EvidenceReference)));
        Assert.All(path.Edges, edge => Assert.False(string.IsNullOrWhiteSpace(edge.EvidenceReference)));
    }

    [Fact]
    public async Task SecuritySignals_ShouldExtendPathFromIdentityToRiskyWorkload()
    {
        var fixture = FixtureData.Create(AssignmentType.Direct, inherited: false);
        var signals = new[]
        {
            new ResourceSecuritySignal(fixture.Resource.Id, ResourceSecuritySignalType.PublicExposure, "Internet-facing", "Cloud inventory", RiskSeverity.High),
            new ResourceSecuritySignal(fixture.Resource.Id, ResourceSecuritySignalType.WorkloadVulnerability, "CVE-2026-1234", "Vulnerability scanner", RiskSeverity.Critical, IsExploitable: true, CvssScore: 9.8)
        };

        var result = await new AccessPathEngine().BuildAsync(fixture.Request with { ResourceSecuritySignals = signals });

        var path = Assert.Single(result.Paths);
        Assert.Contains(path.Nodes, node => node.NodeType == "PublicExposure");
        Assert.Contains(path.Nodes, node => node.NodeType == "WorkloadVulnerability");
        Assert.Equal(path.Nodes.Count - 1, path.Edges.Count);
    }

    [Fact]
    public async Task NestedGroupPath_ShouldPreserveAllGroupsAndAvoidCircularTraversal()
    {
        var fixture = FixtureData.Create(AssignmentType.Group, inherited: false);
        var groupA = Guid.NewGuid();
        var groupB = fixture.Entitlement.PrincipalId;
        var memberships = new[]
        {
            new GroupMembership { Id = Guid.NewGuid(), PrincipalId = fixture.Principal.Id, GroupId = groupA, CloudAccountId = fixture.Account.Id },
            new GroupMembership { Id = Guid.NewGuid(), PrincipalId = groupA, GroupId = groupB, CloudAccountId = fixture.Account.Id },
            new GroupMembership { Id = Guid.NewGuid(), PrincipalId = groupB, GroupId = groupA, CloudAccountId = fixture.Account.Id }
        };
        var request = fixture.Request with { GroupMemberships = memberships };

        var result = await new AccessPathEngine().BuildAsync(request);

        var path = Assert.Single(result.Paths);
        Assert.Equal(AccessPathType.NestedGroup, path.PathType);
        Assert.Contains(path.Nodes, node => node.NodeType == "NestedGroup");
        Assert.True(path.Nodes.Count < 20);
    }

    [Fact]
    public async Task ConditionalDeniedPath_ShouldTrustEffectiveAccessAndRemainConditional()
    {
        var fixture = FixtureData.Create(AssignmentType.Policy, inherited: false, condition: true);
        fixture.Effective.Decision = AccessDecision.Deny;
        var result = await new AccessPathEngine().BuildAsync(fixture.Request);

        var path = Assert.Single(result.Paths);
        Assert.Equal(AccessPathStatus.Partial, path.PathStatus);
        Assert.Equal(AccessPathType.Conditional, path.PathType);
        Assert.Contains(path.Nodes, node => node.NodeType == "Condition");
    }

    [Fact]
    public async Task AllMode_ShouldReturnMultipleDistinctPathsAndShortestModeOnePath()
    {
        var fixture = FixtureData.Create(AssignmentType.Direct, inherited: false);
        var second = fixture.Entitlement with { Id = Guid.NewGuid(), AssignmentType = AssignmentType.Role, RoleId = Guid.NewGuid(), Evidence = fixture.Entitlement.Evidence with { SourceId = "second-path" } };
        var request = fixture.Request with { Normalized = fixture.Normalized with { Entitlements = new[] { fixture.Entitlement, second } } };

        var all = await new AccessPathEngine().BuildAsync(request with { Mode = PathMode.All });
        var shortest = await new AccessPathEngine().BuildAsync(request with { Mode = PathMode.Shortest });

        Assert.Equal(2, all.Paths.Count);
        Assert.Single(shortest.Paths);
    }

    [Fact]
    public async Task PathLimit_ShouldReturnPartialDiagnostic()
    {
        var fixture = FixtureData.Create(AssignmentType.Direct, inherited: false);
        var result = await new AccessPathEngine().BuildAsync(fixture.Request with { MaxNodesPerPath = 2 });

        Assert.Equal(AccessPathStatus.Partial, Assert.Single(result.Paths).PathStatus);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "AP-002");
    }

    private sealed record FixtureData(CloudAccount Account, CloudPrincipal Principal, CloudResource Resource, CloudPermission Permission, NormalizedEntitlement Entitlement, DomainEffectiveAccess Effective, NormalizationResult Normalized, AccessPathRequest Request)
    {
        public static FixtureData Create(AssignmentType assignment, bool inherited, bool condition = false)
        {
            var account = new CloudAccount { Id = Guid.NewGuid(), Name = "Demo", ExternalAccountId = "demo", CloudProvider = CloudProviderType.Azure };
            var principal = new CloudPrincipal { Id = Guid.NewGuid(), AccountId = account.Id, Name = "Alice", PrincipalId = "alice", CloudProvider = CloudProviderType.Azure, IdentityType = IdentityType.User };
            var resource = new CloudResource { Id = Guid.NewGuid(), AccountId = account.Id, Name = "Production Storage", ResourceId = "/subscriptions/demo/resource", Type = "Resource" };
            var permission = new CloudPermission { Id = Guid.NewGuid(), AccountId = account.Id, Name = "Storage Read", Action = "storage.read", Resource = "*", AccessType = AccessType.Allow };
            var roleId = Guid.NewGuid();
            var evidence = new ProviderEvidence(CloudProvider.Azure, "RoleAssignment", "assignment-1", "Demo", "{}", "statement", "/subscriptions/demo", assignment.ToString(), DateTime.UtcNow, new Dictionary<string, string>());
            var entitlement = new NormalizedEntitlement(Guid.NewGuid(), CloudProvider.Azure, account.Id, assignment == AssignmentType.Group ? Guid.NewGuid() : principal.Id, resource.Id, roleId, null, permission.Id, new NormalizedScope(CloudProvider.Azure, NormalizedScopeType.Subscription, "/subscriptions/demo", null, null, inherited), assignment, condition ? NormalizedAccessType.Conditional : NormalizedAccessType.Allow, !inherited && assignment == AssignmentType.Direct, inherited, condition, condition ? new NormalizedCondition("Condition", "request.time", "Unknown", "request.time") : null, condition ? NormalizationStatus.PartiallyNormalized : NormalizationStatus.Normalized, evidence, DateTime.UtcNow, null, "/subscriptions/demo");
            var effective = new DomainEffectiveAccess { Id = Guid.NewGuid(), Provider = CloudProvider.Azure, CloudAccountId = account.Id, PrincipalId = principal.Id, ResourceId = resource.Id, PermissionId = permission.Id, RoleId = roleId, Decision = condition ? AccessDecision.Deny : AccessDecision.Allow, EvaluationStatus = condition ? EvaluationStatus.ConditionRequired : EvaluationStatus.FullyEvaluated, AssignmentType = assignment, IsInherited = inherited, IsConditional = condition };
            var normalized = new NormalizationResult(new[] { principal }, new[] { new CloudRole { Id = roleId, AccountId = account.Id, Name = "Reader", RoleId = "reader" } }, Array.Empty<CloudPolicy>(), new[] { permission }, new[] { entitlement }, Array.Empty<NormalizationWarning>(), Array.Empty<string>(), new(1, 1, 0, 0, 0));
            var membership = assignment == AssignmentType.Group ? new[] { new GroupMembership { Id = Guid.NewGuid(), PrincipalId = principal.Id, GroupId = entitlement.PrincipalId, CloudAccountId = account.Id } } : Array.Empty<GroupMembership>();
            return new(account, principal, resource, permission, entitlement, effective, normalized, new AccessPathRequest(new[] { effective }, normalized, new[] { resource }, membership));
        }
    }
}
