using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Normalization.Models;
using CloudEntitle.RiskEngine.Services;
using CloudEntitle.RiskEngine.Models;
using Xunit;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;
using DomainAccessPath = CloudEntitle.Domain.Entities.AccessPath;

namespace CloudEntitle.RiskEngine.Tests;

public sealed class RiskEngineTests
{
    [Fact]
    public void PolicyLoader_ShouldValidateCustomRuleDocument()
    {
        var json = "[{\"ruleId\":\"CUSTOM-001\",\"name\":\"Production wildcard\",\"description\":\"Detect wildcard access in production.\",\"severity\":\"High\",\"category\":\"ExcessiveAccess\",\"actionContains\":\"*\",\"enabled\":true}]";

        var definitions = new PolicyAsCodeRuleLoader().Load(json);

        Assert.Single(definitions);
        Assert.Equal("CUSTOM-001", definitions[0].RuleId);
    }

    [Fact]
    public void PolicyLoader_ShouldRejectInvalidRuleIds()
    {
        Assert.Throws<InvalidDataException>(() => new PolicyAsCodeRuleLoader().Load("[{\"ruleId\":\"CIEM-001\",\"name\":\"Invalid\",\"description\":\"Invalid\",\"severity\":\"Low\",\"category\":\"Privilege\",\"actionContains\":\"read\"}]"));
    }

    [Fact]
    public async Task CustomRule_ShouldGenerateEvidenceBackedFinding()
    {
        var fixture = FixtureData.Create(action: "storage.admin", resource: "production-resource", privileged: true);
        var custom = new CustomRiskRuleDefinition("CUSTOM-001", "Privileged storage action", "Detect privileged storage actions.", RiskSeverity.High, RiskCategory.ExcessiveAccess, ActionContains: "storage", RequiresPrivilegedAccess: true);
        var registry = new RiskRuleRegistry(customRules: new[] { custom });
        var result = await new CloudEntitle.RiskEngine.Services.RiskEngine(registry).EvaluateAsync(fixture.Request);

        Assert.Contains(result.Findings, finding => finding.RuleId == GuidUtility.Create(GuidUtility.Namespace, "CUSTOM-001") && finding.Title == "Privileged storage action");
    }
    [Fact]
    public async Task WildcardAndBroadScope_ShouldProduceEvidenceBackedFindings()
    {
        var fixture = FixtureData.Create(action: "s3:*", resource: "*");
        var result = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(fixture.Request);

        Assert.Contains(result.Findings, finding => finding.RuleId == Stable("CIEM-003") && finding.EvaluationState == RiskEvaluationState.Confirmed);
        Assert.Contains(result.Findings, finding => finding.RuleId == Stable("CIEM-014"));
        Assert.All(result.Findings, finding => Assert.False(string.IsNullOrWhiteSpace(finding.Evidence.Summary)));
    }

    [Fact]
    public async Task MissingActivity_ShouldRemainUnknownForDormancyAndUnusedRules()
    {
        var fixture = FixtureData.Create(action: "storage.read", resource: "specific-resource", privileged: true);
        var result = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(fixture.Request);

        Assert.Contains(result.Findings, finding => finding.RuleId == Stable("CIEM-005") && finding.EvaluationState == RiskEvaluationState.Unknown);
        Assert.Contains(result.Findings, finding => finding.RuleId == Stable("CIEM-008") && finding.EvaluationState == RiskEvaluationState.Unknown);
    }

    [Fact]
    public async Task MissingActivityCoverageForMutation_ShouldRemainUnknown()
    {
        var fixture = FixtureData.Create(action: "storage.write", resource: "specific-resource");

        var result = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(fixture.Request);

        Assert.Contains(result.Findings, finding => finding.RuleId == Stable("CIEM-002") && finding.EvaluationState == RiskEvaluationState.Unknown);
    }

    [Fact]
    public async Task CompleteActivityWindowWithoutMutation_ShouldBePotential()
    {
        var fixture = FixtureData.Create(action: "storage.write", resource: "specific-resource");
        var evaluatedAt = DateTime.UtcNow;
        var request = fixture.Request with
        {
            EvaluatedAt = evaluatedAt,
            ActivityCoverage = new[] { new ActivityEvidenceCoverage(fixture.Effective.CloudAccountId, fixture.Effective.Provider, evaluatedAt.AddDays(-30), evaluatedAt, ActivityCoverageStatus.Complete, true, true, true, 0) }
        };

        var result = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(request);

        Assert.Contains(result.Findings, finding => finding.RuleId == Stable("CIEM-002") && finding.EvaluationState == RiskEvaluationState.Potential);
    }

    [Fact]
    public async Task CompleteActivityWindowWithoutMatchingActions_ShouldConfirmUnusedEntitlement()
    {
        var fixture = FixtureData.Create(action: "storage.read", resource: "specific-resource");
        var evaluatedAt = DateTime.UtcNow;
        var request = fixture.Request with
        {
            EvaluatedAt = evaluatedAt,
            ActivityCoverage = new[] { new ActivityEvidenceCoverage(fixture.Effective.CloudAccountId, fixture.Effective.Provider, evaluatedAt.AddDays(-30), evaluatedAt, ActivityCoverageStatus.Complete, true, true, true, 0, "Cloud audit query completed") }
        };

        var result = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(request);

        var finding = Assert.Single(result.Findings, item => item.RuleId == Stable("CIEM-008"));
        Assert.Equal(RiskEvaluationState.Confirmed, finding.EvaluationState);
        Assert.Contains("complete activity window", finding.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActivityOnDifferentResource_ShouldNotBeMistakenForPermissionUse()
    {
        var fixture = FixtureData.Create(action: "storage.read", resource: "specific-resource");
        var evaluatedAt = DateTime.UtcNow;
        var request = fixture.Request with
        {
            EvaluatedAt = evaluatedAt,
            ActivityCoverage = new[] { new ActivityEvidenceCoverage(fixture.Effective.CloudAccountId, fixture.Effective.Provider, evaluatedAt.AddDays(-30), evaluatedAt, ActivityCoverageStatus.Complete, true, true, true, 1) },
            ActivityRecords = new[] { new ActivityRecord { PrincipalId = fixture.Effective.PrincipalId, RoleId = fixture.Effective.RoleId, CloudAccountId = fixture.Effective.CloudAccountId, ResourceId = Guid.NewGuid(), Action = "storage.read", Timestamp = evaluatedAt.AddDays(-1), Success = true } }
        };

        var result = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(request);

        Assert.Contains(result.Findings, finding => finding.RuleId == Stable("CIEM-008") && finding.EvaluationState == RiskEvaluationState.Confirmed);
    }

    [Fact]
    public async Task PartialActivityCoverage_ShouldRemainUnknown()
    {
        var fixture = FixtureData.Create(action: "storage.read", resource: "specific-resource");
        var evaluatedAt = DateTime.UtcNow;
        var request = fixture.Request with
        {
            EvaluatedAt = evaluatedAt,
            ActivityCoverage = new[] { new ActivityEvidenceCoverage(fixture.Effective.CloudAccountId, fixture.Effective.Provider, evaluatedAt.AddDays(-30), evaluatedAt, ActivityCoverageStatus.Partial, true, true, true, 0, "Some regions denied") }
        };

        var result = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(request);

        Assert.Contains(result.Findings, finding => finding.RuleId == Stable("CIEM-008") && finding.EvaluationState == RiskEvaluationState.Unknown);
    }

    [Fact]
    public async Task CompleteRoleCoverageWithoutUse_ShouldConfirmUnusedRoleAssignment()
    {
        var fixture = FixtureData.Create(action: "storage.read", resource: "specific-resource");
        var evaluatedAt = DateTime.UtcNow;
        var request = fixture.Request with
        {
            EvaluatedAt = evaluatedAt,
            ActivityCoverage = new[] { new ActivityEvidenceCoverage(fixture.Effective.CloudAccountId, fixture.Effective.Provider, evaluatedAt.AddDays(-30), evaluatedAt, ActivityCoverageStatus.Complete, true, true, true, 0, "Role usage query completed") }
        };

        var result = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(request);

        Assert.Contains(result.Findings, finding => finding.RuleId == Stable("CIEM-016") && finding.EvaluationState == RiskEvaluationState.Confirmed);
    }

    [Fact]
    public async Task SuccessfulRoleUse_ShouldPreventUnusedRoleFinding()
    {
        var fixture = FixtureData.Create(action: "storage.read", resource: "specific-resource");
        var evaluatedAt = DateTime.UtcNow;
        var request = fixture.Request with
        {
            EvaluatedAt = evaluatedAt,
            ActivityCoverage = new[] { new ActivityEvidenceCoverage(fixture.Effective.CloudAccountId, fixture.Effective.Provider, evaluatedAt.AddDays(-30), evaluatedAt, ActivityCoverageStatus.Complete, true, true, true, 1) },
            ActivityRecords = new[] { new ActivityRecord { CloudAccountId = fixture.Effective.CloudAccountId, RoleId = fixture.Effective.RoleId, Action = "AssumeRole", Timestamp = evaluatedAt.AddDays(-1), Success = true } }
        };

        var result = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(request);

        Assert.DoesNotContain(result.Findings, finding => finding.RuleId == Stable("CIEM-016"));
    }

    [Fact]
    public async Task ExternalPrivilegedServiceIdentity_ShouldProduceSeparateFindings()
    {
        var fixture = FixtureData.Create(action: "storage.read", resource: "production-resource", privileged: true, external: true, identityType: IdentityType.ServiceAccount, production: true);
        var result = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(fixture.Request);

        Assert.Contains(result.Findings, finding => finding.RuleId == Stable("CIEM-006"));
        Assert.Contains(result.Findings, finding => finding.RuleId == Stable("CIEM-007"));
        Assert.Contains(result.Findings, finding => finding.RuleId == Stable("CIEM-009"));
    }

    [Fact]
    public async Task WorkloadIdentityGovernance_ShouldFindOrphanedAndUnownedEvenWithoutAccess()
    {
        var now = DateTime.UtcNow;
        var principal = new CloudPrincipal
        {
            Id = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            CloudProvider = CloudProviderType.Azure,
            Name = "stale-unowned-app",
            IdentityType = IdentityType.ServicePrincipal,
            IsActive = true,
            OwnerInventoryComplete = true,
            ActivityEvidenceComplete = true,
            ActivityObservationDays = 180,
            LastActivityAt = now.AddDays(-120),
            ActivityEvidenceSource = "Complete test observation"
        };
        var request = EmptyRequest(new[] { principal }) with { EvaluatedAt = now };

        var result = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(request);

        Assert.Contains(result.Findings, finding => finding.RuleId == Stable("CIEM-018") && finding.PrincipalId == principal.Id);
        Assert.Contains(result.Findings, finding => finding.RuleId == Stable("CIEM-019") && finding.PrincipalId == principal.Id);
    }

    [Fact]
    public async Task WorkloadIdentityGovernance_ShouldNotInferOrphanedFromIncompleteUseCoverage()
    {
        var principal = new CloudPrincipal
        {
            Id = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            CloudProvider = CloudProviderType.Gcp,
            Name = "unknown-use-service-account",
            IdentityType = IdentityType.ServiceAccount,
            OwnerInventoryComplete = false,
            ActivityEvidenceComplete = false,
            LastActivityAt = null
        };

        var result = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(EmptyRequest(new[] { principal }));

        Assert.DoesNotContain(result.Findings, finding => finding.RuleId == Stable("CIEM-018") || finding.RuleId == Stable("CIEM-019"));
    }

    [Fact]
    public async Task WorkloadIdentityGovernance_ShouldFlagPrivilegedEffectiveAccess()
    {
        var principal = new CloudPrincipal
        {
            Id = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            CloudProvider = CloudProviderType.Kubernetes,
            Name = "automation-service-account",
            IdentityType = IdentityType.ServiceAccount
        };
        var access = new DomainEffectiveAccess
        {
            Id = Guid.NewGuid(),
            Provider = CloudEntitle.Domain.CloudProvider.Kubernetes,
            CloudAccountId = principal.AccountId,
            PrincipalId = principal.Id,
            AccessType = AccessType.Allow,
            Decision = AccessDecision.Allow,
            IsPrivileged = true,
            Scope = "clusters/prod"
        };

        var result = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(EmptyRequest(new[] { principal }) with
        {
            EffectiveAccess = new[] { access }
        });

        var finding = Assert.Single(result.Findings, item => item.RuleId == Stable("CIEM-020"));
        Assert.Equal(principal.Id, finding.PrincipalId);
        Assert.Contains("PrivilegedGrantCount=1", finding.Evidence.Summary, StringComparison.Ordinal);
    }

    private static RiskEvaluationRequest EmptyRequest(IReadOnlyList<CloudPrincipal> principals)
        => new(Array.Empty<CloudAccount>(), principals, Array.Empty<CloudResource>(), Array.Empty<CloudRole>(), Array.Empty<CloudPolicy>(),
            Array.Empty<CloudPermission>(), Array.Empty<NormalizedEntitlement>(), Array.Empty<DomainEffectiveAccess>(), Array.Empty<DomainAccessPath>(), Array.Empty<ActivityRecord>());

    [Fact]
    public async Task ExploitableAttackPath_ShouldPrioritizeNonHumanIdentityToPublicSensitiveVulnerableResource()
    {
        var fixture = FixtureData.Create(action: "storage.read", resource: "production-resource", privileged: true, external: true, identityType: IdentityType.ServiceAccount);
        var resourceId = fixture.Effective.ResourceId!.Value;
        var observedAt = DateTime.UtcNow;
        fixture.Request.Principals[0].UpdatedAt = observedAt;
        fixture.Effective.CalculatedAt = observedAt;
        var signals = CreateCurrentRiskSignals(resourceId, observedAt);

        var result = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(fixture.Request with
        {
            ResourceSecuritySignals = signals,
            ResourceSecurityCoverage = CompleteResourceCoverage(resourceId, observedAt)
        });

        var finding = Assert.Single(result.Findings, item => item.RuleId == Stable("CIEM-017"));
        Assert.Equal(100, finding.PriorityScore);
        Assert.Equal(RiskSeverity.Critical, finding.Severity);
        Assert.Equal(RiskEvaluationState.Confirmed, finding.EvaluationState);
        Assert.Equal(fixture.Request.AccessPaths[0].Id, finding.AccessPathId);
        Assert.Contains("PublicExposure", finding.Evidence.Summary, StringComparison.Ordinal);
        Assert.Contains("SensitiveData", finding.Evidence.Summary, StringComparison.Ordinal);
        Assert.Contains("CriticalAsset", finding.Evidence.Summary, StringComparison.Ordinal);
        Assert.Contains("CVE-2026-1234", finding.Evidence.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaleSignalsAndPaths_ShouldLowerPriorityAndRemainPotential()
    {
        var fixture = FixtureData.Create(action: "storage.read", resource: "production-resource", privileged: true, external: true, identityType: IdentityType.ServiceAccount);
        var resourceId = fixture.Effective.ResourceId!.Value;
        var now = DateTime.UtcNow;
        var staleAt = now.AddDays(-60);
        fixture.Request.Principals[0].UpdatedAt = staleAt;
        fixture.Effective.CalculatedAt = staleAt;
        var stalePath = fixture.Request.AccessPaths[0];
        stalePath.CreatedAt = staleAt;

        var result = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(fixture.Request with
        {
            EvaluatedAt = now,
            ResourceSecuritySignals = CreateCurrentRiskSignals(resourceId, staleAt),
            ResourceSecurityCoverage = CompleteResourceCoverage(resourceId, staleAt)
        });

        var finding = Assert.Single(result.Findings, item => item.RuleId == Stable("CIEM-017"));
        Assert.True(finding.PriorityScore < 100);
        Assert.Equal(RiskEvaluationState.Potential, finding.EvaluationState);
        Assert.Contains("Stale", finding.Evidence.Summary, StringComparison.Ordinal);
        Assert.Equal("Stale", finding.Evidence.Details["AccessPathFreshness"]);
    }

    [Fact]
    public async Task MissingResourceCoverage_ShouldBeVisibleAndReducePriority()
    {
        var fixture = FixtureData.Create(action: "storage.read", resource: "production-resource", privileged: true, external: true, identityType: IdentityType.ServiceAccount);
        var resourceId = fixture.Effective.ResourceId!.Value;
        var now = DateTime.UtcNow;
        fixture.Request.Principals[0].UpdatedAt = now;
        fixture.Effective.CalculatedAt = now;
        var result = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(fixture.Request with
        {
            EvaluatedAt = now,
            ResourceSecuritySignals = CreateCurrentRiskSignals(resourceId, now),
            ResourceSecurityCoverage = Array.Empty<ResourceSecurityEvidenceCoverage>()
        });

        var finding = Assert.Single(result.Findings, item => item.RuleId == Stable("CIEM-017"));
        Assert.True(finding.PriorityScore < 100);
        Assert.Equal(RiskEvaluationState.Unknown, finding.EvaluationState);
        Assert.Contains("EvidenceCoverage=", finding.Evidence.Summary, StringComparison.Ordinal);
        Assert.Contains("CriticalAsset=Unknown", finding.Evidence.Details["EvidenceCoverage"], StringComparison.Ordinal);
    }

    private static IReadOnlyList<ResourceSecuritySignal> CreateCurrentRiskSignals(Guid resourceId, DateTime observedAt)
        => new[]
        {
            new ResourceSecuritySignal(resourceId, ResourceSecuritySignalType.PublicExposure, "Public endpoint", "Cloud inventory", RiskSeverity.High, ObservedAtUtc: observedAt),
            new ResourceSecuritySignal(resourceId, ResourceSecuritySignalType.SensitiveData, "Restricted customer data", "Data catalog", RiskSeverity.High, ObservedAtUtc: observedAt),
            new ResourceSecuritySignal(resourceId, ResourceSecuritySignalType.WorkloadVulnerability, "CVE-2026-1234", "Scanner", RiskSeverity.Critical, IsExploitable: true, CvssScore: 9.8, ObservedAtUtc: observedAt),
            new ResourceSecuritySignal(resourceId, ResourceSecuritySignalType.CriticalAsset, "Crown-jewel database", "Asset inventory", RiskSeverity.Critical, ObservedAtUtc: observedAt)
        };

    private static IReadOnlyList<ResourceSecurityEvidenceCoverage> CompleteResourceCoverage(Guid resourceId, DateTime observedThroughUtc)
        => Enum.GetValues<ResourceSecuritySignalType>()
            .Select(type => new ResourceSecurityEvidenceCoverage(resourceId, type, SecurityEvidenceCoverageStatus.Complete, observedThroughUtc, "Source query completed."))
            .ToArray();

    [Fact]
    public async Task SecuritySignalsOnUnrelatedResource_ShouldNotCreateAttackPathFinding()
    {
        var fixture = FixtureData.Create(action: "storage.read", resource: "production-resource", privileged: true, identityType: IdentityType.ServiceAccount);
        var result = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(fixture.Request with
        {
            ResourceSecuritySignals = new[] { new ResourceSecuritySignal(Guid.NewGuid(), ResourceSecuritySignalType.PublicExposure, "Public endpoint", "Cloud inventory", RiskSeverity.Critical) }
        });

        Assert.DoesNotContain(result.Findings, finding => finding.RuleId == Stable("CIEM-017"));
    }

    [Fact]
    public async Task NormalReadOnlyUser_ShouldNotProducePrivilegeOrWildcardFindings()
    {
        var fixture = FixtureData.Create(action: "storage.read", resource: "specific-resource", privileged: false);
        var result = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(fixture.Request);

        Assert.DoesNotContain(result.Findings, finding => finding.RuleId == Stable("CIEM-001") || finding.RuleId == Stable("CIEM-003") || finding.RuleId == Stable("CIEM-006") || finding.RuleId == Stable("CIEM-007") || finding.RuleId == Stable("CIEM-009"));
    }

    [Fact]
    public async Task DuplicateRuleOutput_ShouldBeDeduplicatedByFingerprint()
    {
        var fixture = FixtureData.Create(action: "*", resource: "*");
        var duplicated = fixture.Request with { EffectiveAccess = new[] { fixture.Effective, fixture.Effective } };
        var result = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(duplicated);

        Assert.Equal(result.Findings.Select(finding => finding.Fingerprint).Distinct().Count(), result.Findings.Count);
    }

    private static Guid Stable(string rule) => GuidUtility.Create(GuidUtility.Namespace, rule);

    private sealed record FixtureData(RiskEvaluationRequest Request, DomainEffectiveAccess Effective)
    {
        public static FixtureData Create(string action, string resource, bool privileged = false, bool external = false, IdentityType identityType = IdentityType.User, bool production = false)
        {
            var account = new CloudAccount { Id = Guid.NewGuid(), Name = "Demo", ExternalAccountId = "demo", CloudProvider = CloudProviderType.Aws };
            var principal = new CloudPrincipal { Id = Guid.NewGuid(), AccountId = account.Id, Name = "demo-principal", PrincipalId = "demo-principal", CloudProvider = CloudProviderType.Aws, IdentityType = identityType, IsPrivileged = privileged, IsExternal = external };
            var resourceName = resource;
            var resourceEntity = new CloudResource { Id = Guid.NewGuid(), AccountId = account.Id, Name = resourceName, ResourceId = resourceName, Type = "Resource" };
            if (production) resourceEntity.Tags["Environment"] = "Production";
            var permission = new CloudPermission { Id = Guid.NewGuid(), AccountId = account.Id, Name = action, Action = action, Resource = resourceName, AccessType = AccessType.Allow };
            var role = new CloudRole { Id = Guid.NewGuid(), AccountId = account.Id, Name = "DemoRole", RoleId = "demo-role", IsPrivileged = privileged };
            var evidence = new ProviderEvidence(CloudProvider.Aws, "PolicyStatement", "demo-statement", "DemoPolicy", "{}", "{}", "arn:aws:iam::demo:root", "Allow", DateTime.UtcNow, new Dictionary<string, string>());
            var entitlement = new NormalizedEntitlement(Guid.NewGuid(), CloudProvider.Aws, account.Id, principal.Id, resourceEntity.Id, role.Id, null, permission.Id, new NormalizedScope(CloudProvider.Aws, NormalizedScopeType.Account, "arn:aws:iam::demo", null, null, false), AssignmentType.Policy, NormalizedAccessType.Allow, true, false, false, null, NormalizationStatus.Normalized, evidence, DateTime.UtcNow, null, resourceName);
            var effective = new DomainEffectiveAccess { Id = Guid.NewGuid(), Provider = CloudProvider.Aws, CloudAccountId = account.Id, PrincipalId = principal.Id, ResourceId = resourceEntity.Id, PermissionId = permission.Id, RoleId = role.Id, Decision = AccessDecision.Allow, EvaluationStatus = EvaluationStatus.FullyEvaluated, AssignmentType = AssignmentType.Policy, IsDirect = true, IsPrivileged = privileged };
            var normalized = new NormalizationResult(new[] { principal }, new[] { role }, Array.Empty<CloudPolicy>(), new[] { permission }, new[] { entitlement }, Array.Empty<NormalizationWarning>(), Array.Empty<string>(), new(1, 1, 0, 0, 0));
            var path = new DomainAccessPath { Id = Guid.NewGuid(), Provider = CloudProvider.Aws, CloudAccountId = account.Id, PrincipalId = principal.Id, ResourceId = resourceEntity.Id, EffectiveAccessId = effective.Id, PathType = AccessPathType.PolicyBased, PathStatus = AccessPathStatus.Complete, CreatedAt = DateTime.UtcNow, Nodes = Enumerable.Range(0, 3).Select(index => new AccessPathNode { Id = Guid.NewGuid(), NodeType = index == 0 ? "Principal" : index == 1 ? "Policy" : "Resource", Name = index == 0 ? principal.Name : index == 1 ? "DemoPolicy" : resourceEntity.Name, Sequence = index }).ToList() };
            return new(new RiskEvaluationRequest(new[] { account }, new[] { principal }, new[] { resourceEntity }, new[] { role }, Array.Empty<CloudPolicy>(), new[] { permission }, new[] { entitlement }, new[] { effective }, new[] { path }, Array.Empty<ActivityRecord>()), effective);
        }
    }

    private static class GuidUtility { public static readonly Guid Namespace = Guid.Parse("7c31cbf1-3b9f-4cc2-b9d2-0f3f0f02c6af"); public static Guid Create(Guid ns, string name) { var h = System.Security.Cryptography.SHA1.HashData(ns.ToByteArray().Concat(System.Text.Encoding.UTF8.GetBytes(name)).ToArray()); var b = h.Take(16).ToArray(); b[6] = (byte)((b[6] & 15) | 80); b[8] = (byte)((b[8] & 63) | 128); return new Guid(b); } }
}
