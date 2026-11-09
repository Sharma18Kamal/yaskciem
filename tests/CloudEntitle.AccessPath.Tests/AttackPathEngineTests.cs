using CloudEntitle.AccessPath.Models;
using CloudEntitle.AccessPath.Services;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using Xunit;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;

namespace CloudEntitle.AccessPath.Tests;

public sealed class AttackPathEngineTests
{
    [Theory]
    [InlineData(CloudProviderType.Aws, IdentityType.User, "AWS AssumeRole")]
    [InlineData(CloudProviderType.Azure, IdentityType.ManagedIdentity, "Azure managed identity federation")]
    [InlineData(CloudProviderType.Gcp, IdentityType.ServiceAccount, "GCP service account impersonation")]
    public async Task ConfirmedProviderTrustAndEffectiveAccess_CreateVerifiedEvidenceBackedPath(
        CloudProviderType provider, IdentityType targetType, string relationship)
    {
        var fixture = CreateFixture(provider, targetType, relationship);

        var graph = await new AttackPathEngine().BuildAsync(fixture.Request);

        var path = Assert.Single(graph.Paths);
        Assert.Equal(AttackPathReachability.VerifiedReachable, path.Reachability);
        Assert.Equal(AttackPathCategory.SensitiveResourceExposure, path.Category);
        Assert.Contains("provider trust evidence", path.EvidenceSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(graph.Findings, finding => finding.AttackPathId == path.Id);
    }

    [Fact]
    public async Task UnverifiedFederation_RemainsPotentialAndNeverVerifiedReachable()
    {
        var fixture = CreateFixture(CloudProviderType.Gcp, IdentityType.ServiceAccount, "Federated workload identity",
            trustStatus: "Potential", condition: "Unknown");

        var graph = await new AttackPathEngine().BuildAsync(fixture.Request);

        var path = Assert.Single(graph.Paths);
        Assert.Equal(AttackPathReachability.Theoretical, path.Reachability);
        Assert.DoesNotContain(graph.Paths, item => item.Reachability == AttackPathReachability.VerifiedReachable);
    }

    [Fact]
    public async Task DeniedEffectiveAccess_IsVisibleAsDeniedEvidenceButDoesNotFormPath()
    {
        var fixture = CreateFixture(CloudProviderType.Aws, IdentityType.User, "AWS AssumeRole");
        var denied = WithAccessDecision(fixture.Access[0], AccessDecision.Deny);

        var graph = await new AttackPathEngine().BuildAsync(fixture.Request with { EffectiveAccess = [denied] });

        Assert.Empty(graph.Paths);
        Assert.Contains(graph.Edges, edge => edge.Feasibility == AttackEdgeFeasibility.Denied);
    }

    [Fact]
    public async Task ConditionalAccess_IsPotentialUntilContextIsEvaluated()
    {
        var fixture = CreateFixture(CloudProviderType.Azure, IdentityType.ManagedIdentity, "Managed identity access");
        var conditional = WithAccessDecision(fixture.Access[0], AccessDecision.Conditional, EvaluationStatus.ConditionRequired, true);
        conditional.PrincipalId = fixture.Source.Id;
        conditional.CloudAccountId = fixture.Source.AccountId;
        conditional.Provider = CloudProvider.Azure;

        var graph = await new AttackPathEngine().BuildAsync(fixture.Request with
        {
            TrustRelationships = [],
            EffectiveAccess = [conditional]
        });

        var path = Assert.Single(graph.Paths);
        Assert.Equal(AttackPathReachability.Potential, path.Reachability);
        Assert.Contains(graph.Edges, edge => edge.ConditionEvidence is not null);
    }

    [Fact]
    public async Task CyclicTrustGraph_TerminatesAndReportsSuppressedCycle()
    {
        var fixture = CreateFixture(CloudProviderType.Gcp, IdentityType.ServiceAccount, "Service account impersonation");
        var target = fixture.Target;
        var cycle = fixture.Trust with
        {
            SourcePrincipalId = target.Id,
            SourceCloudAccountId = target.AccountId,
            SourceProvider = CloudProvider.Gcp,
            TargetPrincipalId = fixture.Source.Id,
            TargetCloudAccountId = fixture.Source.AccountId,
            TargetProvider = CloudProvider.Azure,
            EvidenceReference = "reverse-trust-evidence"
        };

        var graph = await new AttackPathEngine().BuildAsync(fixture.Request with
        {
            TrustRelationships = [fixture.Trust, cycle]
        });

        Assert.Single(graph.Paths);
        Assert.Contains(graph.Diagnostics, diagnostic => diagnostic.Contains("Cycle detected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SimilarIdentityNames_DoNotCreateImplicitTrust()
    {
        var fixture = CreateFixture(CloudProviderType.Aws, IdentityType.User, "AWS AssumeRole");
        var unrelatedTarget = new CloudPrincipal
        {
            Id = fixture.Target.Id, AccountId = fixture.Target.AccountId,
            CloudProvider = fixture.Target.CloudProvider, Name = fixture.Source.Name + "-target",
            PrincipalId = fixture.Target.PrincipalId, IdentityType = fixture.Target.IdentityType,
            IsActive = fixture.Target.IsActive, IsPrivileged = fixture.Target.IsPrivileged
        };
        var targetAccess = new DomainEffectiveAccess
        {
            Id = fixture.Access[0].Id, PrincipalId = unrelatedTarget.Id,
            CloudAccountId = unrelatedTarget.AccountId, Provider = CloudProvider.Gcp,
            ResourceId = fixture.Access[0].ResourceId, PermissionId = fixture.Access[0].PermissionId,
            Decision = fixture.Access[0].Decision, EvaluationStatus = fixture.Access[0].EvaluationStatus,
            AccessType = fixture.Access[0].AccessType, IsDirect = fixture.Access[0].IsDirect,
            Evidence = fixture.Access[0].Evidence, AccessSource = fixture.Access[0].AccessSource,
            CalculatedAt = fixture.Access[0].CalculatedAt
        };
        var request = fixture.Request with
        {
            Principals = [fixture.Source, unrelatedTarget],
            TrustRelationships = [],
            EffectiveAccess = [targetAccess]
        };

        var graph = await new AttackPathEngine().BuildAsync(request);

        Assert.Empty(graph.Paths);
        Assert.Contains(graph.Nodes, node => node.IsInitialAccessPoint);
    }

    [Fact]
    public async Task AzureFederatedCredential_CreatesExternalEvidenceNodeAndKeepsRuntimeClaimsPotential()
    {
        var fixture = CreateFixture(CloudProviderType.Azure, IdentityType.ServicePrincipal, "Federated workload identity");
        fixture.Target.FederationInventoryComplete = true;
        fixture.Target.FederationTrusts =
        [
            new WorkloadFederationTrust("https://token.actions.githubusercontent.com",
                "repo:org/project:environment:prod", ["api://AzureADTokenExchange"],
                "Azure Entra federated identity credential",
                "claims['sub'] matches 'repo:org/project:*'")
        ];
        var trusts = AttackTrustRelationshipMapper.FromProviderEvidence([fixture.Source, fixture.Target]);

        var graph = await new AttackPathEngine().BuildAsync(fixture.Request with
        {
            Principals = [fixture.Target],
            TrustRelationships = trusts
        });

        var source = Assert.Single(graph.Nodes, node => node.IsInitialAccessPoint);
        Assert.Contains("token.actions.githubusercontent.com", source.Details, StringComparison.Ordinal);
        var path = Assert.Single(graph.Paths);
        Assert.Equal(AttackPathReachability.Theoretical, path.Reachability);
        Assert.Contains(graph.Edges, edge => edge.RelationshipType == "Federated workload identity"
            && edge.Feasibility == AttackEdgeFeasibility.Unknown
            && edge.ConditionEvidence!.Contains("runtime token claims", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GcpServiceAccountTrust_WithUnsatisfiedCelCondition_IsDenied()
    {
        var fixture = CreateFixture(CloudProviderType.Gcp, IdentityType.ServiceAccount, "GCP service account impersonation");
        fixture.Source.CloudProvider = CloudProviderType.Gcp;
        fixture.Source.PrincipalId = "user:demo@example.invalid";
        fixture.Target.IdentityTrusts =
        [
            new CloudIdentityTrust("user:demo@example.invalid", "GCP service account impersonation",
                "GCP service-account IAM policy", "Potential", "NotSatisfied",
                "resource.name == \"projects/other/serviceAccounts/target\"",
                "GCP CEL condition did not match the target service account.")
        ];
        var trusts = AttackTrustRelationshipMapper.FromProviderEvidence([fixture.Source, fixture.Target]);

        var graph = await new AttackPathEngine().BuildAsync(fixture.Request with
        {
            TrustRelationships = trusts
        });

        Assert.Contains(graph.Edges, edge => edge.RelationshipType == "GCP service account impersonation"
            && edge.Feasibility == AttackEdgeFeasibility.Denied);
        Assert.Empty(graph.Paths);
    }

    [Fact]
    public void AwsAssumeRoleTrust_RequiresSeparateSourceSidePermissionEvidence()
    {
        var fixture = CreateFixture(CloudProviderType.Aws, IdentityType.Role, "AWS AssumeRole");
        fixture.Source.CloudProvider = CloudProviderType.Aws;
        fixture.Source.PrincipalId = "arn:aws:iam::123456789012:role/source";
        fixture.Target.FederationInventoryComplete = true;
        fixture.Target.PrincipalId = "arn:aws:iam::987654321098:role/target";
        fixture.Target.FederationTrusts =
        [
            new WorkloadFederationTrust("aws-principal:arn:aws:iam::123456789012:root",
                string.Empty, [], "AWS IAM AssumeRole trust policy")
        ];

        var trust = Assert.Single(AttackTrustRelationshipMapper.FromProviderEvidence([fixture.Source, fixture.Target]));

        Assert.True(trust.RequiresSourcePermission);
        Assert.Equal("sts:AssumeRole", trust.RequiredSourcePermissionPattern);
        Assert.Equal("Confirmed", trust.Status);
    }

    private static Fixture CreateFixture(
        CloudProviderType targetProvider,
        IdentityType targetType,
        string relationship,
        string trustStatus = "Confirmed",
        string condition = "Satisfied")
    {
        var sourceAccount = Guid.NewGuid();
        var targetAccount = targetProvider == CloudProviderType.Azure ? sourceAccount : Guid.NewGuid();
        var source = new CloudPrincipal
        {
            Id = Guid.NewGuid(), AccountId = sourceAccount, CloudProvider = CloudProviderType.Azure,
            Name = "External contractor", PrincipalId = "external-1", IsExternal = true, IsActive = true,
            IdentityType = IdentityType.User
        };
        var target = new CloudPrincipal
        {
            Id = Guid.NewGuid(), AccountId = targetAccount, CloudProvider = targetProvider,
            Name = "Workload identity", PrincipalId = "workload-1", IsActive = true, IsPrivileged = true,
            IdentityType = targetType
        };
        var resource = new CloudResource
        {
            Id = Guid.NewGuid(), AccountId = targetAccount, Name = "Sensitive production database",
            ResourceId = "provider://database/critical"
        };
        var permission = new CloudPermission
        {
            Id = Guid.NewGuid(), AccountId = targetAccount, Name = "Database access",
            Action = targetProvider switch
            {
                CloudProviderType.Aws => "rds:DescribeDBInstances",
                CloudProviderType.Azure => "Microsoft.Sql/servers/read",
                _ => "cloudsql.instances.get"
            },
            AccessType = AccessType.Allow,
            Source = "provider effective access"
        };
        var effective = new DomainEffectiveAccess
        {
            Id = Guid.NewGuid(), CloudAccountId = targetAccount, Provider = ToProvider(targetProvider),
            PrincipalId = target.Id, ResourceId = resource.Id, PermissionId = permission.Id,
            Decision = AccessDecision.Allow, EvaluationStatus = EvaluationStatus.FullyEvaluated,
            AccessType = AccessType.Allow, IsDirect = true, CalculatedAt = DateTime.UtcNow,
            AccessSource = "provider effective access", Evidence = "Provider authorization record 123"
        };
        var trust = new AttackTrustRelationship(
            source.Id, sourceAccount, CloudProvider.Azure, target.Id, targetAccount, ToProvider(targetProvider),
            relationship, trustStatus, "Provider trust evidence", "trust-record-123", DateTime.UtcNow,
            ConditionState: condition, ConditionEvidence: "Condition context evidence");
        return new Fixture(source, target, [effective], trust, new AttackGraphBuildRequest(
            [source, target], [], [], [permission], [resource], [effective], [trust],
            [new ResourceSecuritySignal(resource.Id, ResourceSecuritySignalType.SensitiveData,
                "Sensitive data classification", "Provider evidence", RiskSeverity.Critical)]));
    }

    private static CloudProvider ToProvider(CloudProviderType provider) => provider switch
    {
        CloudProviderType.Aws => CloudProvider.Aws,
        CloudProviderType.Azure => CloudProvider.Azure,
        CloudProviderType.Gcp => CloudProvider.Gcp,
        _ => CloudProvider.Kubernetes
    };

    private sealed record Fixture(CloudPrincipal Source, CloudPrincipal Target, DomainEffectiveAccess[] Access,
        AttackTrustRelationship Trust, AttackGraphBuildRequest Request);

    private static DomainEffectiveAccess WithAccessDecision(DomainEffectiveAccess access, AccessDecision decision,
        EvaluationStatus status = EvaluationStatus.FullyEvaluated, bool conditional = false)
    {
        return new DomainEffectiveAccess
        {
            Id = access.Id, CloudAccountId = access.CloudAccountId, Provider = access.Provider,
            PrincipalId = access.PrincipalId, ResourceId = access.ResourceId, PermissionId = access.PermissionId,
            Decision = decision, EvaluationStatus = status, AccessType = decision == AccessDecision.Deny ? AccessType.Deny : AccessType.Allow,
            IsDirect = access.IsDirect, IsInherited = access.IsInherited, IsConditional = conditional,
            IsPrivileged = access.IsPrivileged, CalculatedAt = access.CalculatedAt,
            AccessSource = access.AccessSource, Evidence = access.Evidence
        };
    }
}
