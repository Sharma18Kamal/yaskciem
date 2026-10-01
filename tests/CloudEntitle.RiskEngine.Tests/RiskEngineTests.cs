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
    public async Task ExternalPrivilegedServiceIdentity_ShouldProduceSeparateFindings()
    {
        var fixture = FixtureData.Create(action: "storage.read", resource: "production-resource", privileged: true, external: true, identityType: IdentityType.ServiceAccount, production: true);
        var result = await new CloudEntitle.RiskEngine.Services.RiskEngine().EvaluateAsync(fixture.Request);

        Assert.Contains(result.Findings, finding => finding.RuleId == Stable("CIEM-006"));
        Assert.Contains(result.Findings, finding => finding.RuleId == Stable("CIEM-007"));
        Assert.Contains(result.Findings, finding => finding.RuleId == Stable("CIEM-009"));
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
            var path = new DomainAccessPath { Id = Guid.NewGuid(), Provider = CloudProvider.Aws, CloudAccountId = account.Id, PrincipalId = principal.Id, ResourceId = resourceEntity.Id, EffectiveAccessId = effective.Id, PathType = AccessPathType.PolicyBased, PathStatus = AccessPathStatus.Complete, Nodes = Enumerable.Range(0, 3).Select(index => new AccessPathNode { Id = Guid.NewGuid(), NodeType = index == 0 ? "Principal" : index == 1 ? "Policy" : "Resource", Name = index == 0 ? principal.Name : index == 1 ? "DemoPolicy" : resourceEntity.Name, Sequence = index }).ToList() };
            return new(new RiskEvaluationRequest(new[] { account }, new[] { principal }, new[] { resourceEntity }, new[] { role }, Array.Empty<CloudPolicy>(), new[] { permission }, new[] { entitlement }, new[] { effective }, new[] { path }, Array.Empty<ActivityRecord>()), effective);
        }
    }

    private static class GuidUtility { public static readonly Guid Namespace = Guid.Parse("7c31cbf1-3b9f-4cc2-b9d2-0f3f0f02c6af"); public static Guid Create(Guid ns, string name) { var h = System.Security.Cryptography.SHA1.HashData(ns.ToByteArray().Concat(System.Text.Encoding.UTF8.GetBytes(name)).ToArray()); var b = h.Take(16).ToArray(); b[6] = (byte)((b[6] & 15) | 80); b[8] = (byte)((b[8] & 63) | 128); return new Guid(b); } }
}
