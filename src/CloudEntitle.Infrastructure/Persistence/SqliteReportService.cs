namespace CloudEntitle.Infrastructure.Persistence;

using System.Text;
using System.Text.Json;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;
using CloudEntitle.Reporting.Models;
using CloudEntitle.Reporting.Services;
using Microsoft.EntityFrameworkCore;

public sealed class SqliteReportService : IReportService
{
    private readonly DbContextOptions<CloudEntitleDbContext> _options;
    private readonly string _reportDirectory;

    public SqliteReportService(DbContextOptions<CloudEntitleDbContext> options, string? reportDirectory = null)
    {
        _options = options;
        _reportDirectory = reportDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YASK-CIEM",
            "reports");
    }

    public async Task<string> GenerateReportAsync(string reportType, CancellationToken cancellationToken = default)
        => await GenerateReportAsync(reportType, GetDefaultPath(reportType), cancellationToken);

    public async Task<string> GenerateReportAsync(string reportType, string outputPath, CancellationToken cancellationToken = default)
        => await GenerateReportAsync(reportType, outputPath, null, cancellationToken);

    public async Task<string> GenerateReportAsync(string reportType, string outputPath, IReadOnlyList<CloudEntitle.Domain.RiskFinding>? currentScanFindings, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportType);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        await using var context = new CloudEntitleDbContext(_options);
        var storedFindings = await context.RiskFindings.AsNoTracking()
            .OrderByDescending(finding => finding.CreatedAt)
            .ToListAsync(cancellationToken);
        var findings = storedFindings.Select(finding => new ReportFinding(
                finding.Severity.ToString(), finding.CloudProvider.ToString(), finding.Title, finding.Principal,
                finding.Resource, finding.Permission, finding.Recommendation, finding.Evidence))
            .Concat((currentScanFindings ?? Array.Empty<CloudEntitle.Domain.RiskFinding>()).Select(finding => new ReportFinding(
                finding.Severity.ToString(), finding.CloudProvider.ToString(), finding.Title, finding.Principal,
                finding.Resource, finding.Permission, finding.Recommendation, finding.Evidence)))
            .ToArray();
        var extension = reportType.Trim().ToLowerInvariant();
        var path = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (extension == "json")
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(findings, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        }
        else if (extension == "csv")
        {
            var csv = new StringBuilder("Severity,Provider,Title,Principal,Resource,Permission,Recommendation,Evidence\r\n");
            foreach (var finding in findings)
            {
                csv.AppendLine(string.Join(",", new[]
                {
                    finding.Severity, finding.Provider, finding.Title,
                    finding.Principal, finding.Resource, finding.Permission, finding.Recommendation, finding.Evidence
                }.Select(EscapeCsv)));
            }
            await File.WriteAllTextAsync(path, csv.ToString(), cancellationToken);
        }
        else
        {
            throw new ArgumentException("Report type must be json or csv.", nameof(reportType));
        }

        await RecordExecutionAsync(context, extension, extension, path, cancellationToken);
        return path;
    }

    public async Task<string> GenerateEnterpriseReportAsync(string reportCategory, string outputPath,
        IReadOnlyList<CloudEntitle.Domain.RiskFinding>? currentScanFindings = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportCategory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (!Enum.TryParse<ReportType>(reportCategory, ignoreCase: true, out var reportType))
            throw new ArgumentException("Choose a supported report category.", nameof(reportCategory));
        var format = Path.GetExtension(outputPath).TrimStart('.').ToLowerInvariant() switch
        {
            "json" => ReportFormat.Json,
            "csv" => ReportFormat.Csv,
            "html" => ReportFormat.Html,
            "pdf" => ReportFormat.Pdf,
            "xlsx" => ReportFormat.Excel,
            _ => throw new ArgumentException("Report output must use JSON, CSV, HTML, PDF, or XLSX.", nameof(outputPath))
        };

        await using var context = new CloudEntitleDbContext(_options);
        var accounts = await context.CloudAccounts.AsNoTracking().ToListAsync(cancellationToken);
        var principals = await context.CloudPrincipals.AsNoTracking().ToListAsync(cancellationToken);
        var resources = await context.CloudResources.AsNoTracking().ToListAsync(cancellationToken);
        var entitlements = await context.Entitlements.AsNoTracking().ToListAsync(cancellationToken);
        var effectiveAccess = await context.EffectiveAccesses.AsNoTracking().ToListAsync(cancellationToken);
        var findings = await context.NormalizedRiskFindings.AsNoTracking().ToListAsync(cancellationToken);
        var recommendations = await context.NormalizedRecommendations.AsNoTracking().ToListAsync(cancellationToken);
        var scans = await context.Scans.AsNoTracking().ToListAsync(cancellationToken);
        var usage = await context.PermissionUsageSummaries.AsNoTracking().ToListAsync(cancellationToken);
        var toxicFindings = await context.ToxicCombinationFindings.AsNoTracking().ToListAsync(cancellationToken);
        var attackPaths = await context.AccessPaths.AsNoTracking().Include(item => item.Nodes).Include(item => item.Edges).ToListAsync(cancellationToken);
        var remediationPlans = (await context.RemediationPlans.AsNoTracking().ToListAsync(cancellationToken))
            .Select(item => new ReportRemediationPlan(item.Id, item.Provider.ToString(), item.Status.ToString(),
                item.CreatedAt, item.UpdatedAt, item.Payload)).ToArray();
        var auditEvents = await context.AuditEvents.AsNoTracking().ToListAsync(cancellationToken);
        var complianceControls = BuildComplianceControls(accounts, effectiveAccess, findings);
        var snapshot = new ReportingSnapshot(accounts, principals, resources, entitlements, effectiveAccess, findings,
            recommendations, scans, DateTime.UtcNow)
        {
            PermissionUsageSummaries = usage,
            ToxicCombinationFindings = toxicFindings,
            AttackPaths = attackPaths,
            RemediationPlans = remediationPlans,
            AuditEvents = auditEvents,
            ComplianceControls = complianceControls,
            SupplementalFindings = currentScanFindings ?? Array.Empty<CloudEntitle.Domain.RiskFinding>()
        };
        var generated = new ReportGenerator().Generate(snapshot, reportType, format);
        var path = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (generated.BinaryContent is { } binary)
            await File.WriteAllBytesAsync(path, binary, cancellationToken);
        else
            await File.WriteAllTextAsync(path, generated.Content, new UTF8Encoding(false), cancellationToken);
        await RecordExecutionAsync(context, reportType.ToString(), format.ToString(), path, cancellationToken);
        return path;
    }

    private static async Task RecordExecutionAsync(CloudEntitleDbContext context, string category, string format,
        string outputPath, CancellationToken cancellationToken)
    {
        context.ReportExecutionHistory.Add(new ReportExecutionHistoryRecord
        {
            Id = Guid.NewGuid(),
            Category = category,
            Format = format,
            GeneratedAtUtc = DateTime.UtcNow,
            OutputFileName = Path.GetFileName(outputPath),
            Status = "Succeeded"
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    private static IReadOnlyList<ComplianceReportControl> BuildComplianceControls(
        IReadOnlyList<CloudEntitle.Domain.CloudAccount> accounts,
        IReadOnlyList<CloudEntitle.Domain.Entities.EffectiveAccess> effectiveAccess,
        IReadOnlyList<CloudEntitle.Domain.Entities.RiskFindingModel> findings)
    {
        var relevantFindings = findings.Where(item => item.Severity is CloudEntitle.Domain.RiskSeverity.High or CloudEntitle.Domain.RiskSeverity.Critical).ToArray();
        return new[]
        {
            new ComplianceReportControl("CIEM-BASE-001", "CIEM baseline",
                accounts.Count == 0 ? "NotAssessed" : accounts.All(item => item.IsReadOnly) ? "Pass" : "Fail",
                string.Join(" | ", accounts.Select(item => $"{item.CloudProvider}:{item.Name}: ReadOnly={item.IsReadOnly}")),
                accounts.Count == 0 ? "No cloud account evidence is available." : accounts.Any(item => !item.IsReadOnly) ? "One or more accounts are not marked read-only." : string.Empty),
            new ComplianceReportControl("CIEM-BASE-002", "CIEM baseline",
                effectiveAccess.Count == 0 ? "NotAssessed" : effectiveAccess.All(item => !string.IsNullOrWhiteSpace(item.Evidence) || !string.IsNullOrWhiteSpace(item.EvaluationDetails)) ? "Pass" : "Fail",
                string.Join(" | ", effectiveAccess.Select(item => $"EffectiveAccess={item.Id}; Decision={item.Decision}")),
                effectiveAccess.Count == 0 ? "No effective-access evidence is available." : effectiveAccess.Any(item => string.IsNullOrWhiteSpace(item.Evidence) && string.IsNullOrWhiteSpace(item.EvaluationDetails)) ? "Some effective-access evidence is incomplete." : string.Empty),
            new ComplianceReportControl("CIEM-BASE-003", "CIEM baseline",
                relevantFindings.Length == 0 ? "NotAssessed" : "Fail",
                string.Join(" | ", relevantFindings.Select(item => $"Finding={item.Id}; Status={item.Status}")),
                relevantFindings.Length == 0 ? "No high or critical finding evidence is available." : "High or critical findings require review.")
        };
    }

    private string GetDefaultPath(string reportType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportType);
        var extension = reportType.Trim().ToLowerInvariant();
        return Path.Combine(_reportDirectory, $"yask-ciem-report-{DateTime.UtcNow:yyyyMMdd-HHmmss}.{extension}");
    }

    private sealed record ReportFinding(string Severity, string Provider, string Title, string Principal, string Resource, string Permission, string Recommendation, string Evidence);

    private static string EscapeCsv(string value)
        => $"\"{value.Replace("\"", "\"\"")}\"";
}
