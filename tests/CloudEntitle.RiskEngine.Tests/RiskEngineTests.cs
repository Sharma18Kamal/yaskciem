using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Normalization.Models;
using CloudEntitle.RiskEngine.Services;
using CloudEntitle.RiskEngine.Models;
using CloudEntitle.RiskEngine.Rules;
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

    [Fact]
    public async Task SensitiveStorageReadAndPublicPolicyPermission_ShouldReportToxicCombination()
    {
        var fixture = FixtureData.Create("s3:GetObject", "demo-bucket");
        var storage = fixture.Request.Resources.Single();
        storage.Type = "AWS S3 Bucket";
        var publicPolicy = new CloudPermission
        {
            Id = Guid.NewGuid(),
            AccountId = fixture.Effective.CloudAccountId,
            Name = "Set bucket policy",
            Action = "s3:PutBucketPolicy",
            AccessType = AccessType.Allow
        };
        var companion = new DomainEffectiveAccess
        {
            Id = Guid.NewGuid(),
            Provider = fixture.Effective.Provider,
            CloudAccountId = fixture.Effective.CloudAccountId,
            PrincipalId = fixture.Effective.PrincipalId,
            ResourceId = fixture.Effective.ResourceId,
            PermissionId = publicPolicy.Id,
            Decision = AccessDecision.Allow,
            EvaluationStatus = EvaluationStatus.FullyEvaluated
        };
        var request = fixture.Request with
        {
            Permissions = fixture.Request.Permissions.Append(publicPolicy).ToArray(),
            EffectiveAccess = fixture.Request.EffectiveAccess.Append(companion).ToArray(),
            ResourceSecuritySignals = new[]
            {
                new ResourceSecuritySignal(storage.Id, ResourceSecuritySignalType.SensitiveData,
                    "Sensitive data", "test", RiskSeverity.High)
            }
        };

        var result = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(request);

        var finding = Assert.Single(result.Findings, item => item.RuleId == Stable("CIEM-021"));
        Assert.Equal(RiskEvaluationState.Potential, finding.EvaluationState);
        Assert.Equal(RiskSeverity.High, finding.Severity);
        Assert.Contains(companion.Id.ToString("D"), finding.Evidence.Summary);
    }

    [Fact]
    public async Task CredentialCreationAndAdministrativeAccess_ShouldReportToxicCombination()
    {
        var fixture = FixtureData.Create("iam:CreateAccessKey", "identity");
        var adminPermission = new CloudPermission
        {
            Id = Guid.NewGuid(),
            AccountId = fixture.Effective.CloudAccountId,
            Name = "AdministratorAccess",
            Action = "AdministratorAccess",
            AccessType = AccessType.Allow
        };
        var adminAccess = new DomainEffectiveAccess
        {
            Id = Guid.NewGuid(),
            Provider = fixture.Effective.Provider,
            CloudAccountId = fixture.Effective.CloudAccountId,
            PrincipalId = fixture.Effective.PrincipalId,
            PermissionId = adminPermission.Id,
            Decision = AccessDecision.Allow,
            IsPrivileged = true,
            EvaluationStatus = EvaluationStatus.FullyEvaluated
        };
        var request = fixture.Request with
        {
            Permissions = fixture.Request.Permissions.Append(adminPermission).ToArray(),
            EffectiveAccess = fixture.Request.EffectiveAccess.Append(adminAccess).ToArray()
        };

        var result = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(request);

        var finding = Assert.Single(result.Findings, item => item.RuleId == Stable("CIEM-021"));
        Assert.Equal(RiskSeverity.Critical, finding.Severity);
        Assert.Contains("Credential creation", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IamPolicyModificationAndCriticalDatabaseAccess_ShouldReportToxicCombination()
    {
        var fixture = FixtureData.Create("database.read", "prod-db");
        var database = fixture.Request.Resources.Single();
        database.Type = "AWS RDS Database";
        var iamPermission = new CloudPermission
        {
            Id = Guid.NewGuid(),
            AccountId = fixture.Effective.CloudAccountId,
            Name = "Modify role policy",
            Action = "iam:PutRolePolicy",
            AccessType = AccessType.Allow
        };
        var iamAccess = new DomainEffectiveAccess
        {
            Id = Guid.NewGuid(),
            Provider = fixture.Effective.Provider,
            CloudAccountId = fixture.Effective.CloudAccountId,
            PrincipalId = fixture.Effective.PrincipalId,
            ResourceId = database.Id,
            PermissionId = iamPermission.Id,
            Decision = AccessDecision.Allow,
            EvaluationStatus = EvaluationStatus.FullyEvaluated
        };
        var request = fixture.Request with
        {
            Permissions = fixture.Request.Permissions.Append(iamPermission).ToArray(),
            EffectiveAccess = fixture.Request.EffectiveAccess.Append(iamAccess).ToArray(),
            ResourceSecuritySignals = new[]
            {
                new ResourceSecuritySignal(database.Id, ResourceSecuritySignalType.CriticalAsset,
                    "Critical database", "test", RiskSeverity.Critical)
            }
        };

        var result = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(request);

        var finding = Assert.Single(result.Findings, item => item.RuleId == Stable("CIEM-021"));
        Assert.Equal(RiskSeverity.Critical, finding.Severity);
        Assert.Contains("database", finding.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ToxicPermissionCombination_ShouldNotJoinDifferentAccounts()
    {
        var fixture = FixtureData.Create("s3:GetObject", "demo-bucket");
        var storage = fixture.Request.Resources.Single();
        storage.Type = "AWS S3 Bucket";
        var publicPolicy = new CloudPermission
        {
            Id = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            Name = "Set bucket policy",
            Action = "s3:PutBucketPolicy",
            AccessType = AccessType.Allow
        };
        var otherAccountAccess = new DomainEffectiveAccess
        {
            Id = Guid.NewGuid(),
            Provider = fixture.Effective.Provider,
            CloudAccountId = publicPolicy.AccountId,
            PrincipalId = fixture.Effective.PrincipalId,
            PermissionId = publicPolicy.Id,
            Decision = AccessDecision.Allow
        };
        var request = fixture.Request with
        {
            Permissions = fixture.Request.Permissions.Append(publicPolicy).ToArray(),
            EffectiveAccess = fixture.Request.EffectiveAccess.Append(otherAccountAccess).ToArray(),
            ResourceSecuritySignals = new[]
            {
                new ResourceSecuritySignal(storage.Id, ResourceSecuritySignalType.SensitiveData,
                    "Sensitive data", "test", RiskSeverity.High)
            }
        };

        var result = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(request);

        Assert.DoesNotContain(result.Findings, item => item.RuleId == Stable("CIEM-021"));
    }

    [Fact]
    public async Task StaleCredential_ShouldRequireCompleteInventoryEvidence()
    {
        var fixture = FixtureData.Create("s3:GetObject", "demo-bucket");
        var principal = fixture.Request.Principals.Single();
        principal.CredentialLastRotatedAtUtc = DateTime.UtcNow.AddDays(-120);
        principal.CredentialInventoryComplete = true;
        principal.CredentialEvidenceSource = "test credential inventory";

        var result = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(fixture.Request);

        var finding = Assert.Single(result.Findings, item => item.RuleId == Stable("CIEM-022"));
        Assert.Equal(RiskEvaluationState.Confirmed, finding.EvaluationState);
        Assert.Contains("MaximumAgeDays=90", finding.Evidence.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IncompleteCredentialInventory_ShouldNotClaimCredentialIsFreshOrStale()
    {
        var fixture = FixtureData.Create("s3:GetObject", "demo-bucket");
        var principal = fixture.Request.Principals.Single();
        principal.CredentialLastRotatedAtUtc = DateTime.UtcNow.AddDays(-120);
        principal.CredentialInventoryComplete = false;

        var result = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(fixture.Request);

        Assert.DoesNotContain(result.Findings, item => item.RuleId == Stable("CIEM-022"));
    }

    [Theory]
    [InlineData(CloudProvider.Aws, "s3:GetObject", "s3:PutBucketPolicy")]
    [InlineData(CloudProvider.Azure, "Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read", "Microsoft.Storage/storageAccounts/write")]
    [InlineData(CloudProvider.Gcp, "storage.objects.get", "storage.buckets.setIamPolicy")]
    public async Task ProviderSpecificSensitiveStoragePair_ShouldCreateEvidenceBackedPotentialFinding(
        CloudProvider provider, string readAction, string exposureAction)
    {
        var fixture = ToxicFixture.Create(provider, readAction, exposureAction);
        var result = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(fixture.Request);

        var finding = Assert.Single(result.Findings, item => item.RuleId == Stable("CIEM-021"));
        Assert.Equal(RiskEvaluationState.Potential, finding.EvaluationState);
        Assert.Equal(RiskSeverity.High, finding.Severity);
        Assert.Contains("FirstEffectiveAccessId", finding.Evidence.Details.Keys);
        Assert.Contains("SecondEffectiveAccessId", finding.Evidence.Details.Keys);
        Assert.Contains("Test classification", finding.Evidence.Details["ResourceEvidence"]);
    }

    [Fact]
    public async Task GcpPermissionMatching_IsCaseSensitive()
    {
        var fixture = ToxicFixture.Create(CloudProvider.Gcp, "Storage.objects.get", "storage.buckets.setIamPolicy");
        var result = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(fixture.Request);

        Assert.DoesNotContain(result.Findings, item => item.RuleId == Stable("CIEM-021"));
    }

    [Fact]
    public async Task InheritedAndConditionalPair_IsPotentialWithReducedConfidenceAndTraceableEvidence()
    {
        var fixture = ToxicFixture.Create(CloudProvider.Aws, "s3:GetObject", "s3:PutBucketPolicy");
        fixture.Accesses[0].IsInherited = true;
        fixture.Accesses[0].IsDirect = false;
        fixture.Accesses[0].AssignmentType = AssignmentType.Inherited;
        fixture.Accesses[1].Decision = AccessDecision.Conditional;
        fixture.Accesses[1].IsConditional = true;

        var result = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(fixture.Request);

        var finding = Assert.Single(result.Findings, item => item.RuleId == Stable("CIEM-021"));
        Assert.Equal(RiskEvaluationState.Potential, finding.EvaluationState);
        Assert.Equal("Low", finding.Evidence.Details["Confidence"]);
        Assert.Equal("True", finding.Evidence.Details["FirstIsInherited"]);
        Assert.Equal("True", finding.Evidence.Details["SecondIsConditional"]);
    }

    [Fact]
    public async Task ToxicCombination_DoesNotCombineDifferentAccountsOrUnclassifiedStorage()
    {
        var fixture = ToxicFixture.Create(CloudProvider.Aws, "s3:GetObject", "s3:PutBucketPolicy");
        fixture.Accesses[1].CloudAccountId = Guid.NewGuid();
        var result = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(fixture.Request);
        Assert.DoesNotContain(result.Findings, item => item.RuleId == Stable("CIEM-021"));

        fixture = ToxicFixture.Create(CloudProvider.Aws, "s3:GetObject", "s3:PutBucketPolicy");
        fixture.Request.Resources.Single().Tags.Clear();
        var noClassification = fixture.Request with { ResourceSecuritySignals = Array.Empty<ResourceSecuritySignal>() };
        result = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(noClassification);
        Assert.DoesNotContain(result.Findings, item => item.RuleId == Stable("CIEM-021"));
    }

    [Fact]
    public async Task CrossCloudFederationPath_RequiresConfirmedTrustAndPrivilegedTargetAccess()
    {
        var fixture = CreateTrustPathFixture(TrustRelationshipStatus.Confirmed, AuthorizationConditionState.Satisfied);
        var result = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(fixture);

        var finding = Assert.Single(result.Findings, item => item.RuleId == Stable("CIEM-021"));
        Assert.Equal(RiskEvaluationState.Potential, finding.EvaluationState);
        Assert.Equal("TrustPath", finding.Evidence.Details["ResourceRelation"]);
        Assert.Contains("trust-document-v4", finding.Evidence.Details["TrustPathEvidence"], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TrustRelationshipStatus.Unknown, AuthorizationConditionState.Satisfied)]
    [InlineData(TrustRelationshipStatus.Rejected, AuthorizationConditionState.Satisfied)]
    [InlineData(TrustRelationshipStatus.Confirmed, AuthorizationConditionState.NotSatisfied)]
    public async Task CrossCloudFederationPath_RejectsUnprovenOrConditionDeniedEdges(
        TrustRelationshipStatus status, AuthorizationConditionState conditionState)
    {
        var request = CreateTrustPathFixture(status, conditionState);
        var result = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(request);

        Assert.DoesNotContain(result.Findings, item => item.RuleId == Stable("CIEM-021"));
    }

    [Fact]
    public async Task FederationTrustMapper_RequiresMatchingIssuerSubjectAndAudience()
    {
        var accountId = Guid.NewGuid();
        var source = new CloudPrincipal
        {
            Id = Guid.NewGuid(), AccountId = accountId, CloudProvider = CloudProviderType.Aws,
            FederationIssuer = "https://issuer.example/", FederationSubject = "repo:org/project",
            FederationAudience = "sts.amazonaws.com", FederationInventoryComplete = true
        };
        var target = new CloudPrincipal
        {
            Id = Guid.NewGuid(), AccountId = Guid.NewGuid(), CloudProvider = CloudProviderType.Gcp,
            FederationTrustVerified = true, FederationInventoryComplete = true,
            FederationTrusts = [new WorkloadFederationTrust("https://issuer.example", "repo:org/project",
                ["sts.amazonaws.com"], "provider trust configuration")]
        };
        var mapped = ToxicTrustRelationshipMapper.FromFederationEvidence([source, target]);
        source.FederationSubject = "repo:other/project";
        var unmapped = ToxicTrustRelationshipMapper.FromFederationEvidence([source, target]);

        Assert.Single(mapped);
        Assert.Equal(TrustRelationshipStatus.Confirmed, mapped[0].Status);
        Assert.Empty(unmapped);
    }

    [Fact]
    public async Task ConditionalPermission_IsFilteredWhenNormalizedContextDisprovesItsCondition()
    {
        var fixture = ToxicFixture.Create(CloudProvider.Aws, "s3:GetObject", "s3:PutBucketPolicy");
        fixture.Accesses[1].Decision = AccessDecision.Conditional;
        fixture.Accesses[1].IsConditional = true;
        var deniedCondition = Condition(fixture.Accesses[1].Id, "us-east-1", "eu-west-1");
        var request = fixture.Request with { PermissionConditions = [deniedCondition] };

        var result = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(request);

        Assert.DoesNotContain(result.Findings, item => item.RuleId == Stable("CIEM-021"));
    }

    [Fact]
    public async Task ConditionalPermission_IsSupportedWhenNormalizedContextSatisfiesItsCondition()
    {
        var fixture = ToxicFixture.Create(CloudProvider.Aws, "s3:GetObject", "s3:PutBucketPolicy");
        fixture.Accesses[1].Decision = AccessDecision.Conditional;
        fixture.Accesses[1].IsConditional = true;
        var satisfiedCondition = Condition(fixture.Accesses[1].Id, "us-east-1", "us-east-1");
        var request = fixture.Request with { PermissionConditions = [satisfiedCondition] };

        var result = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(request);

        var finding = Assert.Single(result.Findings, item => item.RuleId == Stable("CIEM-021"));
        Assert.Equal("High", finding.Evidence.Details["Confidence"]);
        Assert.Equal("Satisfied", finding.Evidence.Details["SecondConditionState"]);
    }

    [Fact]
    public async Task UnsupportedConditionSemantics_RemainPotentialWithLowConfidence()
    {
        var fixture = ToxicFixture.Create(CloudProvider.Gcp, "storage.objects.get", "storage.buckets.setIamPolicy");
        fixture.Accesses[1].Decision = AccessDecision.Conditional;
        fixture.Accesses[1].IsConditional = true;
        var unknownCondition = Condition(fixture.Accesses[1].Id, "region", "region") with
        {
            Provider = CloudProvider.Gcp,
            IsSupported = false
        };
        var request = fixture.Request with { PermissionConditions = [unknownCondition] };

        var result = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(request);

        var finding = Assert.Single(result.Findings, item => item.RuleId == Stable("CIEM-021"));
        Assert.Equal("Low", finding.Evidence.Details["Confidence"]);
        Assert.Equal("Unknown", finding.Evidence.Details["SecondConditionState"]);
    }

    [Theory]
    [InlineData("us-east-1", AuthorizationConditionState.Satisfied)]
    [InlineData("eu-west-1", AuthorizationConditionState.NotSatisfied)]
    public void AwsConditionJson_UsesThreeValuedEvaluation(string region, AuthorizationConditionState expected)
    {
        var evidence = AuthorizationConditionEvaluator.FromAwsJson(Guid.NewGuid(), "aws-policy-7",
            """{"StringEquals":{"aws:RequestedRegion":"us-east-1"},"Bool":{"aws:SecureTransport":"true"}}""",
            new Dictionary<string, string>
            {
                ["aws:RequestedRegion"] = region,
                ["aws:SecureTransport"] = "true"
            });

        var result = AuthorizationConditionEvaluator.Evaluate(evidence);

        Assert.Equal(expected, result.State);
        Assert.Contains("aws-policy-7", result.EvidenceSource, StringComparison.Ordinal);
    }

    [Fact]
    public void AwsConditionJson_LeavesMissingContextUnknown()
    {
        var evidence = AuthorizationConditionEvaluator.FromAwsJson(Guid.NewGuid(), "aws-policy-7",
            """{"StringEquals":{"aws:RequestedRegion":"us-east-1"}}""",
            new Dictionary<string, string>());

        Assert.Equal(AuthorizationConditionState.Unknown, AuthorizationConditionEvaluator.Evaluate(evidence).State);
    }

    [Fact]
    public void AwsConditionJson_LeavesUnsupportedOperatorsUnknown()
    {
        var evidence = AuthorizationConditionEvaluator.FromAwsJson(Guid.NewGuid(), "aws-policy-8",
            """{"IpAddress":{"aws:SourceIp":"192.0.2.0/24"}}""",
            new Dictionary<string, string> { ["aws:SourceIp"] = "192.0.2.10" });

        Assert.False(evidence.IsSupported);
        Assert.Equal(AuthorizationConditionState.Unknown, AuthorizationConditionEvaluator.Evaluate(evidence).State);
    }

    private static PermissionConditionEvidence Condition(Guid accessId, string actual, string expected)
        => new(accessId, CloudProvider.Aws, "condition-policy-v2", "aws:RequestedRegion StringEquals",
            true, [new AuthorizationConditionClause([
                new AuthorizationConditionPredicate("aws:RequestedRegion",
                    AuthorizationConditionOperator.StringEquals, [expected], "StringEquals")
            ])], new Dictionary<string, string> { ["aws:RequestedRegion"] = actual });

    private static RiskEvaluationRequest CreateTrustPathFixture(
        TrustRelationshipStatus status, AuthorizationConditionState conditionState)
    {
        var sourceAccountId = Guid.NewGuid();
        var targetAccountId = Guid.NewGuid();
        var sourcePrincipalId = Guid.NewGuid();
        var targetPrincipalId = Guid.NewGuid();
        var sourcePermission = new CloudPermission
        {
            Id = Guid.NewGuid(), AccountId = sourceAccountId, Name = "Assume delegated role",
            Action = "sts:AssumeRole", AccessType = AccessType.Allow
        };
        var targetPermission = new CloudPermission
        {
            Id = Guid.NewGuid(), AccountId = targetAccountId, Name = "Project owner",
            Action = "roles/owner", AccessType = AccessType.Allow
        };
        var sourceAccess = new DomainEffectiveAccess
        {
            Id = Guid.NewGuid(), Provider = CloudProvider.Aws, CloudAccountId = sourceAccountId,
            PrincipalId = sourcePrincipalId, PermissionId = sourcePermission.Id,
            Decision = AccessDecision.Allow, EvaluationStatus = EvaluationStatus.FullyEvaluated,
            AccessType = AccessType.Allow, IsDirect = true
        };
        var targetAccess = new DomainEffectiveAccess
        {
            Id = Guid.NewGuid(), Provider = CloudProvider.Gcp, CloudAccountId = targetAccountId,
            PrincipalId = targetPrincipalId, PermissionId = targetPermission.Id,
            Decision = AccessDecision.Allow, EvaluationStatus = EvaluationStatus.FullyEvaluated,
            AccessType = AccessType.Allow, IsDirect = true, IsPrivileged = true
        };
        var sourceAccount = new CloudAccount
        {
            Id = sourceAccountId, Name = "source-account", ExternalAccountId = "source-account",
            CloudProvider = CloudProviderType.Aws
        };
        var targetAccount = new CloudAccount
        {
            Id = targetAccountId, Name = "target-account", ExternalAccountId = "target-account",
            CloudProvider = CloudProviderType.Gcp
        };
        var sourcePrincipal = new CloudPrincipal
        {
            Id = sourcePrincipalId, AccountId = sourceAccountId, Name = "source-workload",
            PrincipalId = "source-workload", CloudProvider = CloudProviderType.Aws,
            IdentityType = IdentityType.ServiceAccount
        };
        var targetPrincipal = new CloudPrincipal
        {
            Id = targetPrincipalId, AccountId = targetAccountId, Name = "target-owner",
            PrincipalId = "target-owner", CloudProvider = CloudProviderType.Gcp,
            IdentityType = IdentityType.ServiceAccount, IsPrivileged = true
        };
        var relationship = new ToxicTrustRelationshipEvidence(sourcePrincipalId, sourceAccountId, CloudProvider.Aws,
            targetPrincipalId, targetAccountId, CloudProvider.Gcp, "Workload federation", status,
            "GCP workload identity provider", "trust-document-v4", DateTime.UtcNow,
            RequiresSourcePermission: false,
            ConditionState: conditionState, ConditionEvidence: $"condition={conditionState}");
        var trustRule = ToxicCombinationRuleDefaults.Create().Single(item => item.RequireTrustPath);
        return new RiskEvaluationRequest([sourceAccount, targetAccount], [sourcePrincipal, targetPrincipal],
            [], [], [], [sourcePermission, targetPermission], [], [sourceAccess, targetAccess], [], [],
            Options: new(EnabledRules: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CIEM-021" }),
            ToxicCombinationRules: [trustRule], ToxicTrustRelationships: [relationship]);
    }

    private static Guid Stable(string rule) => GuidUtility.Create(GuidUtility.Namespace, rule);

    private sealed record ToxicFixture(RiskEvaluationRequest Request, DomainEffectiveAccess[] Accesses)
    {
        public static ToxicFixture Create(CloudProvider provider, string firstAction, string secondAction)
        {
            var accountId = Guid.NewGuid();
            var principalId = Guid.NewGuid();
            var resourceId = Guid.NewGuid();
            var firstPermission = new CloudPermission { Id = Guid.NewGuid(), AccountId = accountId, Name = firstAction, Action = firstAction };
            var secondPermission = new CloudPermission { Id = Guid.NewGuid(), AccountId = accountId, Name = secondAction, Action = secondAction };
            var first = new DomainEffectiveAccess
            {
                Id = Guid.NewGuid(), Provider = provider, CloudAccountId = accountId, PrincipalId = principalId,
                PermissionId = firstPermission.Id, ResourceId = resourceId, Decision = AccessDecision.Allow,
                EvaluationStatus = EvaluationStatus.FullyEvaluated, AccessType = AccessType.Allow, IsDirect = true
            };
            var second = new DomainEffectiveAccess
            {
                Id = Guid.NewGuid(), Provider = provider, CloudAccountId = accountId, PrincipalId = principalId,
                PermissionId = secondPermission.Id, ResourceId = resourceId, Decision = AccessDecision.Allow,
                EvaluationStatus = EvaluationStatus.FullyEvaluated, AccessType = AccessType.Allow, IsDirect = true
            };
            var resource = new CloudResource { Id = resourceId, AccountId = accountId, Name = "customer-objects",
                Type = "Object Storage Bucket", ResourceId = "provider://bucket/customer-objects",
                Tags = new Dictionary<string, string> { ["classification"] = "sensitive" } };
            var request = new RiskEvaluationRequest(
                [new CloudAccount { Id = accountId, Name = "tenant", ExternalAccountId = "test-account" }],
                [new CloudPrincipal { Id = principalId, AccountId = accountId, Name = "workload",
                    CloudProvider = (CloudProviderType)provider, IdentityType = IdentityType.ManagedIdentity }],
                [resource], [], [], [firstPermission, secondPermission], [], [first, second], [], [],
                Options: new(EnabledRules: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CIEM-021" }),
                ResourceSecuritySignals: [new ResourceSecuritySignal(resourceId, ResourceSecuritySignalType.SensitiveData,
                    "Sensitive", "Test classification", RiskSeverity.High)]);
            return new ToxicFixture(request, [first, second]);
        }
    }

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
