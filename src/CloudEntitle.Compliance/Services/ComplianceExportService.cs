using System.IO.Compression;
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

    public byte[] CreateBundle(ComplianceAssessment assessment, ComplianceExportMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentException.ThrowIfNullOrWhiteSpace(metadata.OrganizationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(metadata.EntraTenantId);
        if (metadata.OrganizationId == Guid.Empty) throw new ArgumentException("An organization ID is required.", nameof(metadata));

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddJson(archive, "manifest.json", new
            {
                schemaVersion = "1.0",
                product = "YASK-CIEM",
                organizationId = metadata.OrganizationId,
                organizationName = metadata.OrganizationName,
                entraTenantId = metadata.EntraTenantId,
                assessmentId = assessment.Id,
                framework = assessment.Framework,
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
                    total = assessment.Controls.Count
                },
                evidenceCaveat = "Assessment reflects the supplied snapshot; it is not a provider authorization decision or certification."
            });
            AddJson(archive, "assessment.json", assessment);
            AddText(archive, "controls.csv", CreateControlCsv(assessment));
        }

        return output.ToArray();
    }

    private static string CreateControlCsv(ComplianceAssessment assessment)
    {
        var builder = new StringBuilder("ControlId,Framework,Title,State,EvidenceCount,Evidence,Gaps\r\n");
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
                string.Join(" | ", control.Gaps)
            }.Select(EscapeCsv)));
        }
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
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }
}
