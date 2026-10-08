using System.IO;
using System.Text.Json;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Infrastructure.Persistence;
using CloudEntitle.RemediationPlanning.Models;
using Microsoft.EntityFrameworkCore;

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
    private readonly IDbContextFactory<CloudEntitleDbContext>? _contextFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LocalRemediationPlanStore(string? filePath = null, IDbContextFactory<CloudEntitleDbContext>? contextFactory = null)
    {
        _contextFactory = contextFactory;
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
            var normalized = Normalize(plan);
            await AppendAuditEntriesAsync(normalized, cancellationToken);
            plans[plan.Id] = normalized;
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

    private async Task AppendAuditEntriesAsync(RemediationPlan plan, CancellationToken cancellationToken)
    {
        if (_contextFactory is null || plan.AuditTrail.Count == 0) return;

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var entryIds = plan.AuditTrail.Select(entry => entry.Id).Where(id => id != Guid.Empty).ToArray();
        var existingIds = await context.AuditEvents.Where(entry => entryIds.Contains(entry.Id))
            .Select(entry => entry.Id).ToHashSetAsync(cancellationToken);
        var newEvents = plan.AuditTrail.Where(entry => entry.Id != Guid.Empty && !existingIds.Contains(entry.Id))
            .Select(entry => new AuditEvent
            {
                Id = entry.Id,
                UserId = entry.ActorUserId,
                OrganizationId = plan.OrganizationId,
                EventType = $"Remediation.{entry.Action}",
                Description = entry.Details,
                Timestamp = entry.Timestamp,
                Metadata = new Dictionary<string, string>
                {
                    ["planId"] = plan.Id.ToString("D"),
                    ["provider"] = plan.Provider.ToString(),
                    ["actor"] = entry.Actor
                }
            }).ToArray();
        if (newEvents.Length == 0) return;

        context.AuditEvents.AddRange(newEvents);
        await context.SaveChangesAsync(cancellationToken);
    }

    private static RemediationPlan Normalize(RemediationPlan plan) => plan with
    {
        Changes = plan.Changes ?? Array.Empty<RemediationChange>(),
        RollbackChanges = plan.RollbackChanges ?? Array.Empty<RemediationChange>(),
        AuditTrail = (plan.AuditTrail ?? Array.Empty<RemediationAuditEntry>())
            .Select(entry => entry.Id == Guid.Empty ? entry with { Id = Guid.NewGuid() } : entry).ToArray()
    };
}
