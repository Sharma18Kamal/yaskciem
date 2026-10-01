using CloudEntitle.Compliance.Models;
using CloudEntitle.Compliance.Services;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.IdentityGovernance.Models;
using Xunit;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;
using DomainAccessPath = CloudEntitle.Domain.Entities.AccessPath;

namespace CloudEntitle.Compliance.Tests;

public sealed class ComplianceTests
{
    [Fact]
    public void Baseline_ShouldFailWhenAccountIsNotReadOnlyAndHighFindingExists()
    {
        var account = new CloudAccount { Id = Guid.NewGuid(), Name = "Production", CloudProvider = CloudProviderType.Azure, ExternalAccountId = "prod", IsReadOnly = false };
        var finding = new RiskFindingModel { Id = Guid.NewGuid(), RuleId = Guid.NewGuid(), CloudProvider = CloudProvider.Azure, CloudAccountId = account.Id, PrincipalId = Guid.NewGuid(), Severity = RiskSeverity.Critical, Status = FindingStatus.Open };
        var context = new ComplianceContext(new[] { account }, Array.Empty<CloudPrincipal>(), Array.Empty<DomainEffectiveAccess>(), Array.Empty<DomainAccessPath>(), new[] { finding }, Array.Empty<IdentityGovernanceRecord>(), Array.Empty<AuditEvent>());

        var assessment = new ComplianceAssessmentService().Assess(context, ComplianceFramework.CiemBaseline);

        Assert.Contains(assessment.Controls, control => control.ControlId == "CIEM-BASE-001" && control.State == ComplianceControlState.Fail);
        Assert.Contains(assessment.Controls, control => control.ControlId == "CIEM-BASE-003" && control.State == ComplianceControlState.Fail);
    }

    [Fact]
    public void LeastPrivilege_ShouldPassWhenAllowedAccessIsFullyEvaluatedAndPathed()
    {
        var accountId = Guid.NewGuid();
        var principalId = Guid.NewGuid();
        var accessId = Guid.NewGuid();
        var access = new DomainEffectiveAccess { Id = accessId, Provider = CloudProvider.Aws, CloudAccountId = accountId, PrincipalId = principalId, Decision = AccessDecision.Allow, EvaluationStatus = EvaluationStatus.FullyEvaluated, IsPrivileged = true };
        var principal = new CloudPrincipal { Id = principalId, AccountId = accountId, Name = "Admin", IdentityType = IdentityType.User, IsPrivileged = true };
        var path = new DomainAccessPath { Id = Guid.NewGuid(), EffectiveAccessId = accessId, PrincipalId = principalId, CloudAccountId = accountId, PathStatus = AccessPathStatus.Complete };
        var context = new ComplianceContext(new[] { new CloudAccount { Id = accountId, Name = "AWS", ExternalAccountId = "aws", CloudProvider = CloudProviderType.Aws, IsReadOnly = true } }, new[] { principal }, new[] { access }, new[] { path }, Array.Empty<RiskFindingModel>(), Array.Empty<IdentityGovernanceRecord>(), Array.Empty<AuditEvent>());

        var assessment = new ComplianceAssessmentService().Assess(context, ComplianceFramework.LeastPrivilege);

        Assert.All(assessment.Controls, control => Assert.Equal(ComplianceControlState.Pass, control.State));
    }

    [Fact]
    public void AuditService_ShouldRemoveSensitiveMetadata()
    {
        var audit = new AuditEventService().Create("ReviewCompleted", "Access review completed", metadata: new Dictionary<string, string> { ["ticket"] = "INC-1", ["token"] = "do-not-store", ["password"] = "do-not-store" });

        Assert.Contains("ticket", audit.Metadata.Keys);
        Assert.DoesNotContain("token", audit.Metadata.Keys);
        Assert.DoesNotContain("password", audit.Metadata.Keys);
    }
}
