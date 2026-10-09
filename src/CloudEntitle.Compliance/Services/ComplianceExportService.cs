using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudEntitle.Compliance.Models;

namespace CloudEntitle.Compliance.Services;

public sealed record ComplianceExportMetadata(
    Guid OrganizationId,
    string OrganizationName,
    string EntraTenantId,
    DateTime? SourceScanAt);

public sealed class ComplianceExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public byte[] CreateBundle(ComplianceAssessment assessment, ComplianceExportMetadata metadata, IComplianceBundleSigner signer)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentException.ThrowIfNullOrWhiteSpace(metadata.OrganizationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(metadata.EntraTenantId);
        if (metadata.OrganizationId == Guid.Empty) throw new ArgumentException("An organization ID is required.", nameof(metadata));

        var assessmentBytes = JsonSerializer.SerializeToUtf8Bytes(assessment, JsonOptions);
        var controlsBytes = Encoding.UTF8.GetBytes(CreateControlCsv(assessment));
        var mappingsBytes = Encoding.UTF8.GetBytes(CreateMappingCsv(assessment));
        var lineageBytes = JsonSerializer.SerializeToUtf8Bytes(assessment.Controls.Select(control => new
        {
            controlId = control.ControlId,
            evidence = control.EvidenceLineage
        }), JsonOptions);
        var controlLineageBytes = JsonSerializer.SerializeToUtf8Bytes(assessment.Controls.Select(control => new ComplianceControlLineage(
            control.ControlId, control.Framework, assessment.MappingCatalogVersion, control.Mappings, control.EvidenceLineage)
        {
            Exceptions = control.Exceptions
        }), JsonOptions);
        var exceptionsBytes = JsonSerializer.SerializeToUtf8Bytes(assessment.Exceptions, JsonOptions);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = "1.2",
            product = "YASK-CIEM",
            assessmentType = "EvidenceSnapshot",
            isCertification = false,
            evidenceCaveat = assessment.EvidenceCaveat,
            mappingCatalogVersion = assessment.MappingCatalogVersion,
            standardVersions = assessment.SelectedStandards.ToDictionary(standard => standard.ToString(), ComplianceStandardCatalog.GetStandardVersion),
            organizationId = metadata.OrganizationId,
            organizationName = metadata.OrganizationName,
            entraTenantId = metadata.EntraTenantId,
            assessmentId = assessment.Id,
            framework = assessment.Framework,
            selectedStandards = assessment.SelectedStandards,
            assessedAtUtc = assessment.AssessedAt,
            exportedAtUtc = DateTime.UtcNow,
            sourceScanAtUtc = metadata.SourceScanAt,
            sourceCounts = new
            {
                Accounts = assessment.Controls.FirstOrDefault(control => control.ControlId == "CIEM-BASE-001")?.EvidenceCount ?? 0
            },
            controlCounts = new
            {
                assessment.Passed,
                assessment.Failed,
                assessment.Partial,
                assessment.NotAssessed,
                total = assessment.Controls.Count,
                exceptions = assessment.Exceptions.Count,
                activeExceptions = assessment.Exceptions.Count(exception => exception.Status == ComplianceExceptionStatus.Active),
                expiredExceptions = assessment.Exceptions.Count(exception => exception.Status == ComplianceExceptionStatus.Expired)
            },
            files = new Dictionary<string, string>
            {
                ["assessment.json"] = Sha256(assessmentBytes),
                ["controls.csv"] = Sha256(controlsBytes),
                ["control-mappings.csv"] = Sha256(mappingsBytes),
                ["evidence-lineage.json"] = Sha256(lineageBytes),
                ["control-lineage.json"] = Sha256(controlLineageBytes),
                ["exceptions.json"] = Sha256(exceptionsBytes)
            }
        }, JsonOptions);
        var signature = signer.Sign(manifestBytes);

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddBytes(archive, "manifest.json", manifestBytes);
            AddBytes(archive, "assessment.json", assessmentBytes);
            AddBytes(archive, "controls.csv", controlsBytes);
            AddBytes(archive, "control-mappings.csv", mappingsBytes);
            AddBytes(archive, "evidence-lineage.json", lineageBytes);
            AddBytes(archive, "control-lineage.json", controlLineageBytes);
            AddBytes(archive, "exceptions.json", exceptionsBytes);
            AddJson(archive, "signature.json", signature);
        }

        return output.ToArray();
    }

    private static string CreateControlCsv(ComplianceAssessment assessment)
    {
        var builder = new StringBuilder("ControlId,Framework,Title,State,EvidenceCount,Evidence,Gaps,Exceptions,Standards\r\n");
        foreach (var control in assessment.Controls)
        {
            builder.AppendLine(string.Join(",", new[]
            {
                control.ControlId,
                control.Framework.ToString(),
                control.Title,
                control.State.ToString(),
                control.EvidenceCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                string.Join(" | ", control.Evidence),
                string.Join(" | ", control.Gaps),
                string.Join(" | ", control.Exceptions.Select(exception => $"{exception.Reference}:{exception.Status}:{exception.ExpiresAtUtc:O}")),
                string.Join(" | ", control.Mappings.Select(mapping => $"{mapping.Standard} {mapping.StandardVersion}:{mapping.Reference}"))
            }.Select(EscapeCsv)));
        }
        return builder.ToString();
    }

    private static string CreateMappingCsv(ComplianceAssessment assessment)
    {
        var builder = new StringBuilder("ControlId,SourceControlId,Standard,StandardVersion,MappingCatalogVersion,Reference,Relationship,Rationale\r\n");
        foreach (var control in assessment.Controls)
        foreach (var mapping in control.Mappings)
            builder.AppendLine(string.Join(",", new[] { control.ControlId, mapping.SourceControlId, mapping.Standard.ToString(), mapping.StandardVersion,
                mapping.MappingCatalogVersion, mapping.Reference, mapping.Relationship, mapping.Rationale }.Select(EscapeCsv)));
        return builder.ToString();
    }

    private static string EscapeCsv(string value)
    {
        var firstMeaningfulCharacter = value.TrimStart(' ', '\t', '\r', '\n').FirstOrDefault();
        if (firstMeaningfulCharacter is '=' or '+' or '-' or '@') value = "'" + value;
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static void AddJson<T>(ZipArchive archive, string name, T value)
        => AddText(archive, name, JsonSerializer.Serialize(value, JsonOptions));

    private static void AddText(ZipArchive archive, string name, string content)
        => AddBytes(archive, name, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content));

    private static void AddBytes(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static string Sha256(ReadOnlySpan<byte> content)
        => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}
