using CloudEntitle.Compliance.Models;
using CloudEntitle.Compliance.Services;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.IdentityGovernance.Models;
using System.IO.Compression;
using System.Security.Cryptography;
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
    public void Assessment_ShouldAttachSelectedStandardMappingsAndEvidenceLineage()
    {
        var account = new CloudAccount { Id = Guid.NewGuid(), Name = "Production", ExternalAccountId = "prod", CloudProvider = CloudProviderType.Azure, IsReadOnly = true };
        var context = new ComplianceContext(new[] { account }, Array.Empty<CloudPrincipal>(), Array.Empty<DomainEffectiveAccess>(), Array.Empty<DomainAccessPath>(), Array.Empty<RiskFindingModel>(), Array.Empty<IdentityGovernanceRecord>(), Array.Empty<AuditEvent>());

        var assessment = new ComplianceAssessmentService().Assess(context, ComplianceFramework.CiemBaseline, selectedStandards: new[] { ComplianceStandard.Nist80053Rev5 });
        var accountControl = Assert.Single(assessment.Controls, control => control.ControlId == "CIEM-BASE-001");

        Assert.Equal(new[] { ComplianceStandard.Nist80053Rev5 }, assessment.SelectedStandards);
        Assert.Contains(accountControl.Mappings, mapping => mapping.Reference == "AC-6" && mapping.Standard == ComplianceStandard.Nist80053Rev5);
        Assert.Equal("NIST SP 800-53 Rev. 5", Assert.Single(accountControl.Mappings).StandardVersion);
        Assert.Equal(accountControl.ControlId, Assert.Single(accountControl.Mappings).SourceControlId);
        Assert.Equal(ComplianceStandardCatalog.MappingCatalogVersion, assessment.MappingCatalogVersion);
        Assert.DoesNotContain(accountControl.Mappings, mapping => mapping.Standard == ComplianceStandard.Iso27001_2022);
        var lineage = Assert.Single(accountControl.EvidenceLineage);
        Assert.Equal(account.Id.ToString("D"), lineage.SourceId);
        Assert.Equal(64, lineage.Sha256.Length);
        Assert.Equal(account.Id, lineage.CloudAccountId);
    }

    [Fact]
    public void VersionedCrosswalk_ShouldExposeCisNistSoc2AndIsoLineage()
    {
        var context = new ComplianceContext(Array.Empty<CloudAccount>(), Array.Empty<CloudPrincipal>(),
            Array.Empty<DomainEffectiveAccess>(), Array.Empty<DomainAccessPath>(), Array.Empty<RiskFindingModel>(),
            Array.Empty<IdentityGovernanceRecord>(), Array.Empty<AuditEvent>());

        var assessment = new ComplianceAssessmentService().Assess(context, ComplianceFramework.CiemBaseline);
        var control = Assert.Single(assessment.Controls, item => item.ControlId == "CIEM-BASE-001");

        Assert.Equal(4, control.Mappings.Count);
        Assert.Contains(control.Mappings, mapping => mapping.Standard == ComplianceStandard.CisControlsV8_1
            && mapping.StandardVersion == "CIS Controls v8.1" && mapping.Reference == "4.1");
        Assert.Contains(control.Mappings, mapping => mapping.Standard == ComplianceStandard.Nist80053Rev5 && mapping.StandardVersion.Contains("Rev. 5", StringComparison.Ordinal));
        Assert.Contains(control.Mappings, mapping => mapping.Standard == ComplianceStandard.Soc2TrustServices && mapping.StandardVersion.Contains("2017", StringComparison.Ordinal));
        Assert.Contains(control.Mappings, mapping => mapping.Standard == ComplianceStandard.Iso27001_2022 && mapping.StandardVersion.Contains("2022", StringComparison.Ordinal));
        Assert.All(control.Mappings, mapping => Assert.Equal(control.ControlId, mapping.SourceControlId));
        Assert.All(control.Mappings, mapping => Assert.Equal(assessment.MappingCatalogVersion, mapping.MappingCatalogVersion));
    }

    [Fact]
    public void ComplianceException_ShouldBeIncludedWithoutChangingControlState()
    {
        var now = new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);
        var account = new CloudAccount { Id = Guid.NewGuid(), Name = "Production", ExternalAccountId = "prod", CloudProvider = CloudProviderType.Azure, IsReadOnly = false };
        var exception = new ComplianceException(Guid.NewGuid(), "CIEM-BASE-001", "RISK-42", "Temporary read-only discovery exception",
            "security@example.invalid", now.AddDays(-1), now.AddDays(14), "approval-record:42");
        var context = new ComplianceContext(new[] { account }, Array.Empty<CloudPrincipal>(), Array.Empty<DomainEffectiveAccess>(),
            Array.Empty<DomainAccessPath>(), Array.Empty<RiskFindingModel>(), Array.Empty<IdentityGovernanceRecord>(), Array.Empty<AuditEvent>())
        {
            Exceptions = new[] { exception }
        };

        var assessment = new ComplianceAssessmentService().Assess(context, ComplianceFramework.CiemBaseline, now);
        var control = Assert.Single(assessment.Controls, item => item.ControlId == exception.ControlId);

        Assert.Equal(ComplianceControlState.Fail, control.State);
        Assert.Equal(ComplianceExceptionStatus.Active, Assert.Single(control.Exceptions).Status);
        Assert.Equal(ComplianceExceptionStatus.Active, Assert.Single(assessment.Exceptions).Status);
        Assert.False(assessment.IsCertification);
        Assert.Contains("not a certification", assessment.EvidenceCaveat, StringComparison.OrdinalIgnoreCase);

        var expired = new ComplianceAssessmentService().Assess(context, ComplianceFramework.CiemBaseline, exception.ExpiresAtUtc.AddMinutes(1));
        Assert.Equal(ComplianceExceptionStatus.Expired, Assert.Single(expired.Exceptions).Status);
        Assert.Equal(ComplianceControlState.Fail, Assert.Single(expired.Controls, item => item.ControlId == exception.ControlId).State);
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
        var exception = new ComplianceException(Guid.NewGuid(), "CIEM-001", "EX-9", "Time-bound migration exception",
            "security@example.invalid", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(30), "approval:EX-9")
        {
            Status = ComplianceExceptionStatus.Active
        };
        var mapping = new ComplianceStandardMapping(ComplianceStandard.CisControlsV8_1, "6.8", "Role-based access alignment")
        {
            SourceControlId = "CIEM-001",
            StandardVersion = "CIS Controls v8.1",
            MappingCatalogVersion = ComplianceStandardCatalog.MappingCatalogVersion
        };
        var control = new ComplianceControl("CIEM-001", ComplianceFramework.CiemBaseline, "Evidence check", "Review access", ComplianceControlState.Partial, 1,
            new[] { "=HYPERLINK(\"https://example.invalid\")" }, new[] { "Owner review required" })
        {
            Mappings = new[] { mapping },
            Exceptions = new[] { exception }
        };
        var assessment = new ComplianceAssessment(Guid.NewGuid(), ComplianceFramework.CiemBaseline, DateTime.UtcNow, new[] { control }, 0, 0, 1, 0)
        {
            SelectedStandards = new[] { ComplianceStandard.CisControlsV8_1 },
            MappingCatalogVersion = ComplianceStandardCatalog.MappingCatalogVersion,
            Exceptions = new[] { exception }
        };
        var metadata = new ComplianceExportMetadata(Guid.NewGuid(), "Acme", Guid.NewGuid().ToString("D"), DateTime.UtcNow);

        using var signer = new TestComplianceBundleSigner();
        using var stream = new MemoryStream(new ComplianceExportService().CreateBundle(assessment, metadata, signer));
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var manifest = ReadEntry(archive, "manifest.json");
        var exportedAssessment = ReadEntry(archive, "assessment.json");
        var csv = ReadEntry(archive, "controls.csv");
        var controlLineage = ReadEntry(archive, "control-lineage.json");
        var exceptions = ReadEntry(archive, "exceptions.json");
        var signature = ComplianceBundleSignatureVerifier.ReadSignature(Encoding.UTF8.GetBytes(ReadEntry(archive, "signature.json")));
        var manifestBytes = Encoding.UTF8.GetBytes(manifest);

        using var manifestJson = JsonDocument.Parse(manifest);
        using var assessmentJson = JsonDocument.Parse(exportedAssessment);
        Assert.Equal("1.2", manifestJson.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("EvidenceSnapshot", manifestJson.RootElement.GetProperty("assessmentType").GetString());
        Assert.False(manifestJson.RootElement.GetProperty("isCertification").GetBoolean());
        Assert.Equal(ComplianceStandardCatalog.MappingCatalogVersion, manifestJson.RootElement.GetProperty("mappingCatalogVersion").GetString());
        Assert.Equal(metadata.OrganizationId, manifestJson.RootElement.GetProperty("organizationId").GetGuid());
        Assert.Equal(control.Evidence[0], assessmentJson.RootElement.GetProperty("controls")[0].GetProperty("evidence")[0].GetString());
        Assert.False(assessmentJson.RootElement.GetProperty("isCertification").GetBoolean());
        Assert.Contains("'=HYPERLINK", csv);
        Assert.Contains("Owner review required", csv);
        Assert.Contains("CIS Controls v8.1", csv);
        Assert.Contains("EX-9", exceptions);
        Assert.Contains("CIEM-001", controlLineage);
        Assert.Contains(archive.Entries, entry => entry.FullName == "control-mappings.csv");
        Assert.Contains(archive.Entries, entry => entry.FullName == "evidence-lineage.json");
        Assert.Contains(archive.Entries, entry => entry.FullName == "control-lineage.json");
        Assert.Contains(archive.Entries, entry => entry.FullName == "exceptions.json");
        Assert.True(ComplianceBundleSignatureVerifier.Verify(manifestBytes, signature));
        Assert.True(ComplianceBundleSignatureVerifier.VerifyArtifact(manifestBytes, "controls.csv", Encoding.UTF8.GetBytes(csv)));
        Assert.False(ComplianceBundleSignatureVerifier.VerifyArtifact(manifestBytes, "controls.csv", Encoding.UTF8.GetBytes(csv + "tampered")));
        Assert.False(ComplianceBundleSignatureVerifier.Verify(Encoding.UTF8.GetBytes(manifest + "tampered"), signature));
    }

    [Fact]
    public void ComplianceExport_ShouldRequireOrganizationMetadata()
    {
        var assessment = new ComplianceAssessment(Guid.NewGuid(), ComplianceFramework.CiemBaseline, DateTime.UtcNow, Array.Empty<ComplianceControl>(), 0, 0, 0, 0);

        using var signer = new TestComplianceBundleSigner();
        Assert.Throws<ArgumentException>(() => new ComplianceExportService().CreateBundle(assessment,
            new ComplianceExportMetadata(Guid.Empty, "Acme", Guid.NewGuid().ToString("D"), null), signer));
    }

    private static string ReadEntry(ZipArchive archive, string name)
    {
        using var reader = new StreamReader(archive.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private sealed class TestComplianceBundleSigner : IComplianceBundleSigner, IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public ComplianceBundleSignatureDocument Sign(ReadOnlySpan<byte> manifestBytes)
        {
            var publicKey = _key.ExportSubjectPublicKeyInfo();
            return new ComplianceBundleSignatureDocument("ECDSA-P256-SHA256",
                Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant(), Convert.ToBase64String(publicKey),
                Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant(),
                Convert.ToBase64String(_key.SignData(manifestBytes, HashAlgorithmName.SHA256)));
        }

        public void Dispose() => _key.Dispose();
    }
}
