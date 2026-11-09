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
    public void ManualEvidence_ShouldBeClassifiedAndMustNotMakeUnassessedControlPass()
    {
        var context = new ComplianceContext(Array.Empty<CloudAccount>(), Array.Empty<CloudPrincipal>(),
            Array.Empty<DomainEffectiveAccess>(), Array.Empty<DomainAccessPath>(), Array.Empty<RiskFindingModel>(),
            Array.Empty<IdentityGovernanceRecord>(), Array.Empty<AuditEvent>())
        {
            ManualEvidence = new[]
            {
                new ComplianceEvidenceRecord(Guid.NewGuid(), "CIEM-BASE-001", "Access review record supplied by administrator",
                    "ticket:REV-100", "security@example.invalid", DateTime.UtcNow)
            }
        };

        var assessment = new ComplianceAssessmentService().Assess(context, ComplianceFramework.CiemBaseline);
        var control = Assert.Single(assessment.Controls, item => item.ControlId == "CIEM-BASE-001");
        var evidence = Assert.Single(control.EvidenceLineage);

        Assert.Equal(ComplianceControlState.Partial, control.State);
        Assert.Equal(1, control.ManualEvidenceCount);
        Assert.Equal(0, control.AutomatedEvidenceCount);
        Assert.Equal(ComplianceEvidenceSourceKind.Manual, evidence.SourceKind);
        Assert.Equal(64, evidence.Sha256.Length);
        Assert.Contains(control.Gaps, gap => gap.Contains("not been independently assessed", StringComparison.Ordinal));
        Assert.False(assessment.IsCertification);
    }

    [Fact]
    public void GdprCrosswalk_ShouldBeInformativeAndExplicitlyDisclaimCompliance()
    {
        var mappings = ComplianceStandardCatalog.GetMappings("CIEM-BASE-002",
            new[] { ComplianceStandard.GdprAccessEvidence });
        var mapping = Assert.Single(mappings);

        Assert.Equal("Articles 5(1)(f), 25, 32", mapping.Reference);
        Assert.Equal("Informative alignment", mapping.Relationship);
        Assert.Contains("does not establish GDPR compliance", mapping.Rationale);
        Assert.StartsWith("https://eur-lex.europa.eu/", mapping.SourceUri);
    }

    [Fact]
    public void ComplianceStandardEnum_ShouldPreservePersistedValuesAndAppendNewStandards()
    {
        Assert.Equal(0, (int)ComplianceStandard.CisControlsV8_1);
        Assert.Equal(1, (int)ComplianceStandard.Nist80053Rev5);
        Assert.Equal(2, (int)ComplianceStandard.Iso27001_2022);
        Assert.Equal(3, (int)ComplianceStandard.Soc2TrustServices);
        Assert.Equal(4, (int)ComplianceStandard.NistCsf2_0);
        Assert.Equal(5, (int)ComplianceStandard.PciDss4_0_1);
        Assert.Equal(6, (int)ComplianceStandard.CisMicrosoftAzureFoundationsBenchmark2_0_0);
        Assert.Equal(7, (int)ComplianceStandard.CisAwsFoundationsBenchmark3_0_0);
        Assert.Equal(8, (int)ComplianceStandard.GdprAccessEvidence);
        Assert.Equal(ComplianceStandard.CisControlsV8_1, JsonSerializer.Deserialize<ComplianceStandard>("0"));
        Assert.Equal(ComplianceStandard.Soc2TrustServices, JsonSerializer.Deserialize<ComplianceStandard>("3"));
        Assert.Equal(ComplianceStandard.NistCsf2_0, JsonSerializer.Deserialize<ComplianceStandard>("4"));
        Assert.Equal(ComplianceStandard.PciDss4_0_1, JsonSerializer.Deserialize<ComplianceStandard>("5"));
        Assert.Equal(ComplianceStandard.CisMicrosoftAzureFoundationsBenchmark2_0_0, JsonSerializer.Deserialize<ComplianceStandard>("6"));
        Assert.Equal(ComplianceStandard.CisAwsFoundationsBenchmark3_0_0, JsonSerializer.Deserialize<ComplianceStandard>("7"));
    }

    [Theory]
    [InlineData("LP-002", ComplianceStandard.CisMicrosoftAzureFoundationsBenchmark2_0_0, "1.23",
        "https://learn.microsoft.com/en-us/azure/governance/policy/samples/cis-azure-2-0-0")]
    [InlineData("AR-001", ComplianceStandard.CisMicrosoftAzureFoundationsBenchmark2_0_0, "1.23",
        "https://learn.microsoft.com/en-us/azure/governance/policy/samples/cis-azure-2-0-0")]
    [InlineData("AR-002", ComplianceStandard.CisMicrosoftAzureFoundationsBenchmark2_0_0, "5.1.2",
        "https://learn.microsoft.com/en-us/azure/governance/policy/samples/cis-azure-2-0-0")]
    [InlineData("LP-002", ComplianceStandard.CisAwsFoundationsBenchmark3_0_0, "1.15",
        "https://docs.aws.amazon.com/securityhub/latest/userguide/iam-controls.html#iam-2")]
    [InlineData("AR-001", ComplianceStandard.CisAwsFoundationsBenchmark3_0_0, "1.15",
        "https://docs.aws.amazon.com/securityhub/latest/userguide/iam-controls.html#iam-2")]
    [InlineData("IG-001", ComplianceStandard.CisAwsFoundationsBenchmark3_0_0, "1.12",
        "https://docs.aws.amazon.com/securityhub/latest/userguide/iam-controls.html#iam-22")]
    public void BroaderBenchmarkMappings_ShouldRetainExactSourceVersionAndLimits(
        string controlId, ComplianceStandard standard, string reference, string sourceUri)
    {
        var mapping = Assert.Single(ComplianceStandardCatalog.GetMappings(controlId, new[] { standard }));
        Assert.Equal(reference, mapping.Reference);
        Assert.Equal(sourceUri, mapping.SourceUri);
        Assert.Equal(controlId, mapping.SourceControlId);
        Assert.Equal(ComplianceStandardCatalog.GetStandardVersion(standard), mapping.StandardVersion);
        Assert.Equal("2026.11.3", mapping.MappingCatalogVersion);
        Assert.Equal("Informative alignment", mapping.Relationship);
        Assert.Contains("does not", mapping.Rationale, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ComplianceStandardCatalog.GetMappings("CIEM-BASE-001", new[] { standard }));
    }

    [Fact]
    public void AwsBenchmarkCrosswalk_ShouldExportWithoutChangingAssessmentResults()
    {
        var context = new ComplianceContext(Array.Empty<CloudAccount>(), Array.Empty<CloudPrincipal>(),
            Array.Empty<DomainEffectiveAccess>(), Array.Empty<DomainAccessPath>(), Array.Empty<RiskFindingModel>(),
            Array.Empty<IdentityGovernanceRecord>(), Array.Empty<AuditEvent>());
        var service = new ComplianceAssessmentService();
        var baseline = service.Assess(context, ComplianceFramework.IdentityGovernance,
            selectedStandards: new[] { ComplianceStandard.CisControlsV8_1 });
        var assessment = service.Assess(context, ComplianceFramework.IdentityGovernance,
            selectedStandards: new[] { ComplianceStandard.CisAwsFoundationsBenchmark3_0_0 });
        Assert.Equal(baseline.Controls.Select(item => item.State), assessment.Controls.Select(item => item.State));
        Assert.False(assessment.IsCertification);
        using var signer = new TestComplianceBundleSigner();
        var bundle = new ComplianceExportService().CreateBundle(assessment,
            new ComplianceExportMetadata(Guid.NewGuid(), "Acme", Guid.NewGuid().ToString("D"), null), signer);
        using var archive = new ZipArchive(new MemoryStream(bundle), ZipArchiveMode.Read);
        var csv = ReadEntry(archive, "control-mappings.csv");
        Assert.Contains("CisAwsFoundationsBenchmark3_0_0", csv);
        Assert.Contains("CIS AWS Foundations Benchmark v3.0.0", csv);
        Assert.Contains(",\"1.12\",", csv);
        Assert.Contains("https://docs.aws.amazon.com/securityhub/latest/userguide/iam-controls.html#iam-22", csv);
    }

    [Fact]
    public void CisAzureBenchmarkMapping_ShouldBeLimitedToVerifiedExternalGuestReviewRecommendation()
    {
        const string sourceUri = "https://learn.microsoft.com/en-us/azure/governance/policy/samples/cis-azure-2-0-0";
        var standards = new[] { ComplianceStandard.CisMicrosoftAzureFoundationsBenchmark2_0_0 };
        var context = new ComplianceContext(Array.Empty<CloudAccount>(), Array.Empty<CloudPrincipal>(),
            Array.Empty<DomainEffectiveAccess>(), Array.Empty<DomainAccessPath>(), Array.Empty<RiskFindingModel>(),
            Array.Empty<IdentityGovernanceRecord>(), Array.Empty<AuditEvent>());

        var assessment = new ComplianceAssessmentService().Assess(context, ComplianceFramework.IdentityGovernance, selectedStandards: standards);
        var control = Assert.Single(assessment.Controls, item => item.ControlId == "IG-002");
        var mapping = Assert.Single(control.Mappings);

        Assert.Equal(ComplianceStandard.CisMicrosoftAzureFoundationsBenchmark2_0_0, mapping.Standard);
        Assert.Equal("1.5", mapping.Reference);
        Assert.Equal("CIS Microsoft Azure Foundations Benchmark v2.0.0", mapping.StandardVersion);
        Assert.Equal(sourceUri, mapping.SourceUri);
        Assert.Equal("Informative alignment", mapping.Relationship);
        Assert.Contains("does not establish that a regular review occurred", mapping.Rationale, StringComparison.Ordinal);
        Assert.Equal(assessment.MappingCatalogVersion, mapping.MappingCatalogVersion);
        Assert.All(assessment.Controls.Where(item => item.ControlId != "IG-002"), item => Assert.Empty(item.Mappings));

        using var signer = new TestComplianceBundleSigner();
        var bundle = new ComplianceExportService().CreateBundle(assessment,
            new ComplianceExportMetadata(Guid.NewGuid(), "Acme", Guid.NewGuid().ToString("D"), null), signer);
        using var archive = new ZipArchive(new MemoryStream(bundle), ZipArchiveMode.Read);
        var mappingCsv = ReadEntry(archive, "control-mappings.csv");
        Assert.Contains("CisMicrosoftAzureFoundationsBenchmark2_0_0", mappingCsv);
        Assert.Contains(sourceUri, mappingCsv);
        Assert.Contains(",\"1.5\",", mappingCsv);
    }

    [Fact]
    public void NistCsfAndPciDssMappings_ShouldBeInformativeAndExportVersionedReferences()
    {
        var access = new DomainEffectiveAccess
        {
            Id = Guid.NewGuid(),
            Provider = CloudProvider.Azure,
            CloudAccountId = Guid.NewGuid(),
            PrincipalId = Guid.NewGuid(),
            Decision = AccessDecision.Allow,
            EvaluationStatus = EvaluationStatus.FullyEvaluated,
            Evidence = "Provider access evaluation"
        };
        var context = new ComplianceContext(Array.Empty<CloudAccount>(), Array.Empty<CloudPrincipal>(),
            new[] { access }, Array.Empty<DomainAccessPath>(), Array.Empty<RiskFindingModel>(),
            Array.Empty<IdentityGovernanceRecord>(), Array.Empty<AuditEvent>());
        var selectedStandards = new[] { ComplianceStandard.NistCsf2_0, ComplianceStandard.PciDss4_0_1 };
        var assessment = new ComplianceAssessmentService().Assess(context, ComplianceFramework.LeastPrivilege, selectedStandards: selectedStandards);
        var control = Assert.Single(assessment.Controls, item => item.ControlId == "LP-001");

        Assert.Equal(selectedStandards, assessment.SelectedStandards);
        Assert.Contains(control.Mappings, mapping => mapping.Standard == ComplianceStandard.NistCsf2_0
            && mapping.Reference == "PR.AA-05"
            && mapping.StandardVersion == "NIST Cybersecurity Framework (CSF) 2.0 (NIST CSWP 29, February 26, 2024)");
        Assert.Contains(control.Mappings, mapping => mapping.Standard == ComplianceStandard.PciDss4_0_1
            && mapping.Reference == "7.2.2"
            && mapping.StandardVersion == "PCI DSS v4.0.1 (June 2024)");
        Assert.All(control.Mappings.Where(mapping => selectedStandards.Contains(mapping.Standard)),
            mapping => Assert.Equal("Informative alignment", mapping.Relationship));

        using var signer = new TestComplianceBundleSigner();
        var bundle = new ComplianceExportService().CreateBundle(assessment,
            new ComplianceExportMetadata(Guid.NewGuid(), "Acme", Guid.NewGuid().ToString("D"), null), signer);
        using var archive = new ZipArchive(new MemoryStream(bundle), ZipArchiveMode.Read);
        using var manifest = JsonDocument.Parse(ReadEntry(archive, "manifest.json"));
        var versions = manifest.RootElement.GetProperty("standardVersions");
        var mappingCsv = ReadEntry(archive, "control-mappings.csv");

        Assert.Equal(control.Mappings.First(mapping => mapping.Standard == ComplianceStandard.NistCsf2_0).StandardVersion,
            versions.GetProperty(nameof(ComplianceStandard.NistCsf2_0)).GetString());
        Assert.Equal(control.Mappings.First(mapping => mapping.Standard == ComplianceStandard.PciDss4_0_1).StandardVersion,
            versions.GetProperty(nameof(ComplianceStandard.PciDss4_0_1)).GetString());
        Assert.Contains("PR.AA-05", mappingCsv);
        Assert.Contains("7.2.2", mappingCsv);
        Assert.False(manifest.RootElement.GetProperty("isCertification").GetBoolean());
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
        Assert.Equal("1.4", manifestJson.RootElement.GetProperty("schemaVersion").GetString());
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
