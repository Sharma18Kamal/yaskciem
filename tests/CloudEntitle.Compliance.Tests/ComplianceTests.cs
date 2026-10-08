using CloudEntitle.Compliance.Models;
using CloudEntitle.Compliance.Services;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.IdentityGovernance.Models;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
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
        Assert.Equal("[REDACTED]", audit.Metadata["token"]);
        Assert.Equal("[REDACTED]", audit.Metadata["password"]);
    }

    [Fact]
    public void ComplianceExport_ShouldIncludeManifestAssessmentEvidenceAndSpreadsheetSafeCsv()
    {
        var control = new ComplianceControl("CIEM-001", ComplianceFramework.CiemBaseline, "Evidence check", "Review access", ComplianceControlState.Partial, 1,
            new[] { "=HYPERLINK(\"https://example.invalid\")" }, new[] { "Owner review required" });
        var assessment = new ComplianceAssessment(Guid.NewGuid(), ComplianceFramework.CiemBaseline, DateTime.UtcNow, new[] { control }, 0, 0, 1, 0);
        var metadata = new ComplianceExportMetadata(Guid.NewGuid(), "Acme", Guid.NewGuid().ToString("D"), DateTime.UtcNow);

        using var stream = new MemoryStream(new ComplianceExportService().CreateBundle(assessment, metadata));
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var manifest = ReadEntry(archive, "manifest.json");
        var exportedAssessment = ReadEntry(archive, "assessment.json");
        var csv = ReadEntry(archive, "controls.csv");

        using var manifestJson = JsonDocument.Parse(manifest);
        using var assessmentJson = JsonDocument.Parse(exportedAssessment);
        Assert.Equal("1.0", manifestJson.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(metadata.OrganizationId, manifestJson.RootElement.GetProperty("organizationId").GetGuid());
        Assert.Equal(control.Evidence[0], assessmentJson.RootElement.GetProperty("controls")[0].GetProperty("evidence")[0].GetString());
        Assert.Contains("'=HYPERLINK", csv);
        Assert.Contains("Owner review required", csv);
    }

    [Fact]
    public void ComplianceExport_ShouldRequireOrganizationMetadata()
    {
        var assessment = new ComplianceAssessment(Guid.NewGuid(), ComplianceFramework.CiemBaseline, DateTime.UtcNow, Array.Empty<ComplianceControl>(), 0, 0, 0, 0);

        Assert.Throws<ArgumentException>(() => new ComplianceExportService().CreateBundle(assessment,
            new ComplianceExportMetadata(Guid.Empty, "Acme", Guid.NewGuid().ToString("D"), null)));
    }

    private static string ReadEntry(ZipArchive archive, string name)
    {
        using var reader = new StreamReader(archive.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
