using System.IO;
using System.Text.Json;
using CloudEntitle.Compliance.Models;

namespace CloudEntitle.Desktop.Services;

public interface IComplianceAssessmentHistoryStore
{
    Task SaveAsync(Guid organizationId, ComplianceAssessment assessment, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ComplianceAssessmentTrendPoint>> GetTrendAsync(Guid organizationId, ComplianceFramework framework, int limit = 30, CancellationToken cancellationToken = default);
}

public sealed class LocalComplianceAssessmentHistoryStore : IComplianceAssessmentHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LocalComplianceAssessmentHistoryStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YASK-CIEM", "compliance", "assessments.json");
    }

    public async Task SaveAsync(Guid organizationId, ComplianceAssessment assessment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        if (organizationId == Guid.Empty) throw new ArgumentException("An organization ID is required.", nameof(organizationId));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var entries = await ReadAsync(cancellationToken);
            entries.RemoveAll(entry => entry.OrganizationId == organizationId && entry.Assessment.Id == assessment.Id);
            entries.Add(new ComplianceAssessmentHistoryEntry(organizationId, assessment));
            var retained = entries
                .Where(entry => entry.OrganizationId != organizationId || entry.Assessment.Framework != assessment.Framework)
                .Concat(entries.Where(entry => entry.OrganizationId == organizationId && entry.Assessment.Framework == assessment.Framework)
                    .OrderByDescending(entry => entry.Assessment.AssessedAt).Take(200))
                .ToArray();
            await WriteAsync(retained, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ComplianceAssessmentTrendPoint>> GetTrendAsync(Guid organizationId, ComplianceFramework framework, int limit = 30, CancellationToken cancellationToken = default)
    {
        if (organizationId == Guid.Empty) throw new ArgumentException("An organization ID is required.", nameof(organizationId));
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return (await ReadAsync(cancellationToken))
                .Where(entry => entry.OrganizationId == organizationId && entry.Assessment.Framework == framework)
                .OrderByDescending(entry => entry.Assessment.AssessedAt)
                .Take(limit)
                .OrderBy(entry => entry.Assessment.AssessedAt)
                .Select(entry => new ComplianceAssessmentTrendPoint(entry.Assessment.Id, framework, entry.Assessment.AssessedAt,
                    entry.Assessment.Passed, entry.Assessment.Failed, entry.Assessment.Partial, entry.Assessment.NotAssessed))
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<ComplianceAssessmentHistoryEntry>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return new List<ComplianceAssessmentHistoryEntry>();
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<List<ComplianceAssessmentHistoryEntry>>(stream, JsonOptions, cancellationToken)
            ?? new List<ComplianceAssessmentHistoryEntry>();
    }

    private async Task WriteAsync(IReadOnlyList<ComplianceAssessmentHistoryEntry> entries, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("History path must include a directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
                await JsonSerializer.SerializeAsync(stream, entries, JsonOptions, cancellationToken);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private sealed record ComplianceAssessmentHistoryEntry(Guid OrganizationId, ComplianceAssessment Assessment);
}
