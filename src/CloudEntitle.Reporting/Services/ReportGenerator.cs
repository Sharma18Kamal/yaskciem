using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Json;
using CloudEntitle.Reporting.Models;

namespace CloudEntitle.Reporting.Services;

public sealed class ReportGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public GeneratedReport Generate(ReportingSnapshot snapshot, ReportType type, ReportFormat format)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var (payload, rows) = BuildReport(snapshot, type);
        var content = format switch
        {
            ReportFormat.Json => JsonSerializer.Serialize(payload, JsonOptions),
            ReportFormat.Html => $"<html><head><meta charset=\"utf-8\"><title>{type} Report</title></head><body><h1>{type} Report</h1><p>Generated {snapshot.GeneratedAt:O}. Evidence snapshot only; not a certification or attestation.</p><pre>{System.Net.WebUtility.HtmlEncode(JsonSerializer.Serialize(payload, JsonOptions))}</pre></body></html>",
            ReportFormat.Csv => CreateCsv(rows),
            ReportFormat.Pdf => string.Empty,
            ReportFormat.Excel => string.Empty,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported report format.")
        };
        var report = new GeneratedReport(type, format, content, snapshot.GeneratedAt);
        return format switch
        {
            ReportFormat.Pdf => report with { BinaryContent = CreatePdf(type, snapshot.GeneratedAt, rows) },
            ReportFormat.Excel => report with { BinaryContent = CreateExcel(type, snapshot.GeneratedAt, rows) },
            _ => report
        };
    }

    private static (object Payload, IReadOnlyList<(string Section, string Value)> Rows) BuildReport(ReportingSnapshot snapshot, ReportType type)
    {
        object payload = type switch
        {
            ReportType.Executive => new
            {
                type,
                snapshot.GeneratedAt,
                Accounts = snapshot.Accounts.Count,
                Identities = snapshot.Principals.Count,
                PrivilegedIdentities = snapshot.Principals.Count(item => item.IsPrivileged),
                CriticalFindings = snapshot.Findings.Count(item => item.Severity == CloudEntitle.Domain.RiskSeverity.Critical),
                HighFindings = snapshot.Findings.Count(item => item.Severity == CloudEntitle.Domain.RiskSeverity.High),
                AccessDecisions = snapshot.EffectiveAccess.GroupBy(item => item.Decision).ToDictionary(group => group.Key.ToString(), group => group.Count())
            },
            ReportType.Technical => new { type, snapshot.GeneratedAt, snapshot.Accounts, snapshot.Principals, snapshot.Resources, snapshot.Entitlements, snapshot.EffectiveAccess },
            ReportType.Identity => new { type, snapshot.GeneratedAt, Identities = snapshot.Principals },
            ReportType.Entitlement => new { type, snapshot.GeneratedAt, snapshot.Entitlements, snapshot.EffectiveAccess },
            ReportType.Risk or ReportType.IdentityRisk => new { type, snapshot.GeneratedAt, Findings = snapshot.Findings.Cast<object>().Concat(snapshot.SupplementalFindings).ToArray() },
            ReportType.Recommendation => new { type, snapshot.GeneratedAt, Recommendations = snapshot.Recommendations },
            ReportType.PermissionUsage => new { type, snapshot.GeneratedAt, UsageSummaries = snapshot.PermissionUsageSummaries },
            ReportType.ToxicCombination => new { type, snapshot.GeneratedAt, Findings = snapshot.ToxicCombinationFindings },
            ReportType.AttackPath => new { type, snapshot.GeneratedAt, Paths = snapshot.AttackPaths },
            ReportType.Remediation => new { type, snapshot.GeneratedAt, Plans = snapshot.RemediationPlans },
            ReportType.ComplianceAssessment => new
            {
                type,
                snapshot.GeneratedAt,
                IsCertification = false,
                Caveat = "Technical evidence snapshot only; it does not establish legal, contractual, or certification compliance.",
                Controls = snapshot.ComplianceControls
            },
            ReportType.AuditHistory => new { type, snapshot.GeneratedAt, AuditEvents = snapshot.AuditEvents },
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported report category.")
        };

        var rows = type switch
        {
            ReportType.Executive => new[]
            {
                ("Summary", JsonSerializer.Serialize(payload, JsonOptions))
            },
            ReportType.Technical => Rows("Accounts", snapshot.Accounts)
                .Concat(Rows("Identities", snapshot.Principals))
                .Concat(Rows("Resources", snapshot.Resources))
                .Concat(Rows("Entitlements", snapshot.Entitlements))
                .Concat(Rows("Effective access", snapshot.EffectiveAccess)).ToArray(),
            ReportType.Identity => Rows("Identity", snapshot.Principals),
            ReportType.Entitlement => Rows("Entitlement", snapshot.Entitlements)
                .Concat(Rows("Effective access", snapshot.EffectiveAccess)).ToArray(),
            ReportType.Risk or ReportType.IdentityRisk => Rows("Risk finding", snapshot.Findings.Cast<object>().Concat(snapshot.SupplementalFindings)),
            ReportType.Recommendation => Rows("Recommendation", snapshot.Recommendations),
            ReportType.PermissionUsage => Rows("Permission usage", snapshot.PermissionUsageSummaries),
            ReportType.ToxicCombination => Rows("Toxic combination", snapshot.ToxicCombinationFindings),
            ReportType.AttackPath => Rows("Attack path", snapshot.AttackPaths),
            ReportType.Remediation => Rows("Remediation plan", snapshot.RemediationPlans),
            ReportType.ComplianceAssessment => Rows("Compliance control", snapshot.ComplianceControls),
            ReportType.AuditHistory => Rows("Audit event", snapshot.AuditEvents),
            _ => Array.Empty<(string, string)>()
        };
        return (payload, rows);
    }

    private static IReadOnlyList<(string Section, string Value)> Rows<T>(string section, IEnumerable<T> items)
        => items.Select(item => (section, JsonSerializer.Serialize(item, JsonOptions))).ToArray();

    private static string CreateCsv(IEnumerable<(string Section, string Value)> rows)
    {
        var builder = new StringBuilder("Section,RecordJson\r\n");
        foreach (var (section, value) in rows)
            builder.AppendLine($"{EscapeCsv(section)},{EscapeCsv(value)}");
        return builder.ToString();
    }

    private static byte[] CreateExcel(ReportType type, DateTime generatedAt, IReadOnlyList<(string Section, string Value)> rows)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddZipText(archive, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
            AddZipText(archive, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            AddZipText(archive, "xl/workbook.xml",
                $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"{type}\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            AddZipText(archive, "xl/_rels/workbook.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
            var sheet = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
            AppendExcelRow(sheet, 1, "Section", "RecordJson");
            AppendExcelRow(sheet, 2, "GeneratedAtUtc", generatedAt.ToUniversalTime().ToString("O"));
            var rowNumber = 3;
            foreach (var (section, value) in rows) AppendExcelRow(sheet, rowNumber++, section, value);
            sheet.Append("</sheetData></worksheet>");
            AddZipText(archive, "xl/worksheets/sheet1.xml", sheet.ToString());
        }
        return output.ToArray();
    }

    private static void AppendExcelRow(StringBuilder builder, int row, string section, string value)
    {
        builder.Append($"<row r=\"{row}\"><c r=\"A{row}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{XmlEscape(section)}</t></is></c><c r=\"B{row}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{XmlEscape(value)}</t></is></c></row>");
    }

    private static byte[] CreatePdf(ReportType type, DateTime generatedAt, IReadOnlyList<(string Section, string Value)> rows)
    {
        var sourceLines = new List<string>
        {
            $"YASK-CIEM {type} Report",
            $"Generated UTC: {generatedAt.ToUniversalTime():O}",
            "Evidence snapshot only; not a certification or attestation."
        };
        sourceLines.AddRange(rows.Select(row => $"{row.Section}: {row.Value}"));
        var textLines = sourceLines.SelectMany(WrapPdfLine).ToArray();
        var pages = textLines.Chunk(64).ToArray();
        var objects = new List<byte[]>
        {
            Encoding.ASCII.GetBytes($"<< /Type /Catalog /Pages 2 0 R >>"),
            Encoding.ASCII.GetBytes($"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, pages.Length).Select(index => $"{4 + index * 2} 0 R"))}] /Count {pages.Length} >>"),
            Encoding.ASCII.GetBytes("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
        };
        foreach (var (pageLines, index) in pages.Select((value, index) => (value, index)))
        {
            var stream = new StringBuilder("BT /F1 8 Tf 42 790 Td 11 TL ");
            foreach (var line in pageLines)
                stream.Append('(').Append(PdfEscape(Encoding.ASCII.GetString(Encoding.ASCII.GetBytes(line)))).Append(") Tj T* ");
            stream.Append("ET");
            var streamBytes = Encoding.ASCII.GetBytes(stream.ToString());
            objects.Add(Encoding.ASCII.GetBytes($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 842] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + index * 2} 0 R >>"));
            objects.Add(Encoding.ASCII.GetBytes($"<< /Length {streamBytes.Length} >>\nstream\n").Concat(streamBytes).Concat(Encoding.ASCII.GetBytes("\nendstream")).ToArray());
        }
        using var output = new MemoryStream();
        WriteAscii(output, "%PDF-1.4\n");
        var offsets = new List<long> { 0 };
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(output.Position);
            WriteAscii(output, $"{index + 1} 0 obj\n");
            output.Write(objects[index]);
            WriteAscii(output, "\nendobj\n");
        }
        var xrefOffset = output.Position;
        WriteAscii(output, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) WriteAscii(output, $"{offset:0000000000} 00000 n \n");
        WriteAscii(output, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF");
        return output.ToArray();
    }

    private static IEnumerable<string> WrapPdfLine(string line)
    {
        var remaining = line.Replace('\r', ' ').Replace('\n', ' ');
        const int maxLength = 110;
        while (remaining.Length > maxLength)
        {
            var boundary = remaining.LastIndexOf(' ', maxLength);
            if (boundary < maxLength / 2) boundary = maxLength;
            yield return remaining[..boundary];
            remaining = remaining[(boundary + (remaining[boundary] == ' ' ? 1 : 0))..];
        }
        yield return remaining;
    }

    private static void AddZipText(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string EscapeCsv(string value)
    {
        var firstMeaningfulCharacter = value.TrimStart(' ', '\t', '\r', '\n').FirstOrDefault();
        if (firstMeaningfulCharacter is '=' or '+' or '-' or '@') value = "'" + value;
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static string XmlEscape(string value) => SecurityElement.Escape(value) ?? string.Empty;
    private static string PdfEscape(string value) => value.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
    private static void WriteAscii(Stream output, string value) => output.Write(Encoding.ASCII.GetBytes(value));
}

public sealed class ReportScheduler
{
    public IReadOnlyList<ScheduledReport> GetDueReports(IEnumerable<ScheduledReport> schedules, DateTime now) => schedules.Where(schedule => schedule.Enabled && schedule.NextRunAt <= now).OrderBy(schedule => schedule.NextRunAt).ToArray();

    public ScheduledReport Advance(ScheduledReport schedule)
    {
        var next = schedule.Schedule switch { ReportSchedule.Daily => schedule.NextRunAt.AddDays(1), ReportSchedule.Weekly => schedule.NextRunAt.AddDays(7), _ => schedule.NextRunAt.AddMonths(1) };
        return schedule with { NextRunAt = next };
    }
}
