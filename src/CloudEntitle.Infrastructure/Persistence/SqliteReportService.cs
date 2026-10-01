namespace CloudEntitle.Infrastructure.Persistence;

using System.Text;
using System.Text.Json;
using CloudEntitle.Application.Interfaces;
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

        return path;
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
