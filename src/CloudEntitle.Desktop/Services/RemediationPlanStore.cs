using System.IO;
using System.Text.Json;
using CloudEntitle.RemediationPlanning.Models;

namespace CloudEntitle.Desktop.Services;

public interface IRemediationPlanStore
{
    Task<IReadOnlyList<RemediationPlan>> GetAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(RemediationPlan plan, CancellationToken cancellationToken = default);
}

public sealed class LocalRemediationPlanStore : IRemediationPlanStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LocalRemediationPlanStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YASK-CIEM", "remediation", "plans.json");
    }

    public async Task<IReadOnlyList<RemediationPlan>> GetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(RemediationPlan plan, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var plans = (await ReadAsync(cancellationToken)).ToDictionary(item => item.Id);
            plans[plan.Id] = Normalize(plan);
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temporaryPath = _filePath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(plans.Values.OrderBy(item => item.CreatedAt), JsonOptions), cancellationToken);
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<RemediationPlan>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath)) return Array.Empty<RemediationPlan>();
        try
        {
            var json = await File.ReadAllTextAsync(_filePath, cancellationToken);
            return (JsonSerializer.Deserialize<List<RemediationPlan>>(json, JsonOptions) ?? new List<RemediationPlan>()).Select(Normalize).ToArray();
        }
        catch (JsonException)
        {
            return Array.Empty<RemediationPlan>();
        }
    }

    private static RemediationPlan Normalize(RemediationPlan plan) => plan with
    {
        Changes = plan.Changes ?? Array.Empty<RemediationChange>(),
        RollbackChanges = plan.RollbackChanges ?? Array.Empty<RemediationChange>(),
        AuditTrail = plan.AuditTrail ?? Array.Empty<RemediationAuditEntry>()
    };
}
