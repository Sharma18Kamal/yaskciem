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
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportType);
        Directory.CreateDirectory(_reportDirectory);

        await using var context = new CloudEntitleDbContext(_options);
        var findings = await context.RiskFindings.AsNoTracking()
            .OrderByDescending(finding => finding.CreatedAt)
            .ToListAsync(cancellationToken);
        var extension = reportType.Trim().ToLowerInvariant();
        var path = Path.Combine(_reportDirectory, $"yask-ciem-report-{DateTime.UtcNow:yyyyMMdd-HHmmss}.{extension}");

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
                    finding.Severity.ToString(), finding.CloudProvider.ToString(), finding.Title,
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

    private static string EscapeCsv(string value)
        => $"\"{value.Replace("\"", "\"\"")}\"";
}
