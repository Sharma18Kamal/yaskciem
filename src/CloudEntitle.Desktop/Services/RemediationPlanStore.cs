using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
            if (_contextFactory is null) return await ReadAsync(cancellationToken);
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var records = await context.RemediationPlans.AsNoTracking().ToListAsync(cancellationToken);
            var actions = (await context.RemediationActions.AsNoTracking().ToListAsync(cancellationToken)).ToLookup(item => item.PlanId);
            var rollbacks = (await context.RemediationRollbacks.AsNoTracking().ToListAsync(cancellationToken)).ToLookup(item => item.PlanId);
            var approvals = (await context.RemediationApprovals.AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(item => item.PlanId);
            var executions = (await context.RemediationExecutions.AsNoTracking().ToListAsync(cancellationToken))
                .ToLookup(item => item.PlanId);
            var audits = (await context.RemediationAuditRecords.AsNoTracking().ToListAsync(cancellationToken)).ToLookup(item => item.PlanId);
            var plans = new Dictionary<Guid, RemediationPlan>();
            foreach (var record in records)
            {
                var plan = Deserialize<RemediationPlan>(record.Payload);
                var approval = approvals.TryGetValue(record.Id, out var storedApproval)
                    ? Deserialize<ApprovalPayload>(storedApproval.Payload) : null;
                var executionHistory = plan.ExecutionHistory
                    .Concat(executions[record.Id].Select(item => Deserialize<RemediationExecution>(item.Payload)))
                    .Concat(plan.LastExecution is { } previousExecution ? new[] { previousExecution } : Array.Empty<RemediationExecution>())
                    .DistinctBy(item => item.Id)
                    .OrderBy(item => item.CompletedAt).ToArray();
                plans.Add(record.Id, Normalize(plan with
                {
                    Changes = actions[record.Id].OrderBy(item => item.Position).Select(item => Deserialize<RemediationChange>(item.Payload)).ToArray(),
                    RollbackChanges = rollbacks[record.Id].OrderBy(item => item.Position).Select(item => Deserialize<RemediationChange>(item.Payload)).ToArray(),
                    AuditTrail = audits[record.Id].OrderBy(item => item.Timestamp).Select(item => Deserialize<RemediationAuditEntry>(item.Payload)).ToArray(),
                    ExecutionHistory = executionHistory,
                    LastExecution = executionHistory.LastOrDefault() ?? plan.LastExecution,
                    ApprovedBy = approval?.ApprovedBy, ApprovedByUserId = approval?.ApprovedByUserId,
                    ApprovedAt = approval?.ApprovedAt, ApprovalComment = approval?.ApprovalComment,
                    ApprovedChangeSetFingerprint = approval?.ApprovedChangeSetFingerprint,
                    DryRunApprovedByUserId = approval?.DryRunApprovedByUserId,
                    ApprovedDryRunFingerprint = approval?.ApprovedDryRunFingerprint
                }));
            }
            foreach (var legacy in await ReadAsync(cancellationToken))
            {
                if (plans.ContainsKey(legacy.Id)) continue;
                await SaveToDatabaseAsync(context, legacy, cancellationToken);
                plans.Add(legacy.Id, legacy);
            }
            return plans.Values.OrderBy(plan => plan.CreatedAt).ToArray();
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
            var normalized = Normalize(plan);
            ValidatePayload(normalized);
            if (_contextFactory is not null)
            {
                await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
                await SaveToDatabaseAsync(context, normalized, cancellationToken);
                return;
            }
            var plans = (await ReadAsync(cancellationToken)).ToDictionary(item => item.Id);
            plans[plan.Id] = normalized;
            foreach (var item in plans.Values) ValidatePayload(item);
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
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Remediation plan file '{_filePath}' contains invalid JSON and was not modified.", exception);
        }
    }

    private static async Task SaveToDatabaseAsync(CloudEntitleDbContext context, RemediationPlan plan, CancellationToken cancellationToken)
    {
        ValidatePayload(plan);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var storedAudits = await context.RemediationAuditRecords.AsNoTracking().Where(entry => entry.PlanId == plan.Id)
            .OrderBy(entry => entry.Timestamp).ToArrayAsync(cancellationToken);
        var audit = storedAudits.Select(entry => JsonSerializer.Deserialize<RemediationAuditEntry>(entry.Payload, JsonOptions)!)
            .Concat(plan.AuditTrail).DistinctBy(entry => entry.Id).ToArray();
        plan = plan with { AuditTrail = audit };
        var record = await context.RemediationPlans.FindAsync([plan.Id], cancellationToken);
        if (record is null)
        {
            record = new RemediationPlanRecord { Id = plan.Id };
            context.RemediationPlans.Add(record);
        }
        record.OrganizationId = plan.OrganizationId;
        record.Provider = (int)plan.Provider;
        record.Status = (int)plan.Status;
        record.CreatedAt = plan.CreatedAt;
        record.UpdatedAt = plan.UpdatedAt;
        record.Payload = JsonSerializer.Serialize(plan, JsonOptions);
        await context.RemediationActions.Where(item => item.PlanId == plan.Id).ExecuteDeleteAsync(cancellationToken);
        await context.RemediationApprovals.Where(item => item.PlanId == plan.Id).ExecuteDeleteAsync(cancellationToken);
        await context.RemediationExecutions.Where(item => item.PlanId == plan.Id).ExecuteDeleteAsync(cancellationToken);
        await context.RemediationRollbacks.Where(item => item.PlanId == plan.Id).ExecuteDeleteAsync(cancellationToken);
        context.RemediationActions.AddRange(plan.Changes.Select((change, index) => new RemediationActionRecord
            { PlanId = plan.Id, Id = change.Id, Position = index, Payload = JsonSerializer.Serialize(change, JsonOptions) }));
        context.RemediationRollbacks.AddRange(plan.RollbackChanges.Select((change, index) => new RemediationRollbackRecord
            { PlanId = plan.Id, Id = change.Id, Position = index, Payload = JsonSerializer.Serialize(change, JsonOptions) }));
        if (plan.ApprovedAt.HasValue || plan.ApprovedBy is not null || plan.ApprovedByUserId.HasValue
            || plan.ApprovalComment is not null || plan.ApprovedChangeSetFingerprint is not null
            || plan.DryRunApprovedByUserId.HasValue || plan.ApprovedDryRunFingerprint is not null)
            context.RemediationApprovals.Add(new RemediationApprovalRecord { PlanId = plan.Id, Payload = JsonSerializer.Serialize(new
            {
                plan.ApprovedBy, plan.ApprovedByUserId, plan.ApprovedAt, plan.ApprovalComment,
                plan.ApprovedChangeSetFingerprint, plan.DryRunApprovedByUserId, plan.ApprovedDryRunFingerprint
            }, JsonOptions) });
        var executionHistory = plan.ExecutionHistory
            .Concat(plan.LastExecution is { } latestExecution ? new[] { latestExecution } : Array.Empty<RemediationExecution>())
            .DistinctBy(execution => execution.Id)
            .OrderBy(execution => execution.CompletedAt)
            .ToArray();
        context.RemediationExecutions.AddRange(executionHistory.Select(execution => new RemediationExecutionRecord
            { PlanId = plan.Id, Id = execution.Id, Payload = JsonSerializer.Serialize(execution, JsonOptions) }));
        var storedIds = storedAudits.Select(entry => entry.Id).ToHashSet();
        context.RemediationAuditRecords.AddRange(audit.Where(entry => !storedIds.Contains(entry.Id)).Select(entry => new RemediationAuditRecord
        {
            Id = entry.Id, PlanId = plan.Id, Timestamp = entry.Timestamp, Payload = JsonSerializer.Serialize(entry, JsonOptions)
        }));
        var entryIds = audit.Select(entry => entry.Id).ToArray();
        var existingIds = await context.AuditEvents.Where(entry => entryIds.Contains(entry.Id))
            .Select(entry => entry.Id).ToHashSetAsync(cancellationToken);
        var newEvents = audit.Where(entry => !existingIds.Contains(entry.Id))
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
        context.AuditEvents.AddRange(newEvents);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        context.ChangeTracker.Clear();
    }

    private static void ValidatePayload(RemediationPlan plan)
    {
        var node = JsonSerializer.SerializeToNode(plan, JsonOptions);
        ValidateNode(node, null);
    }

    private static void ValidateNode(JsonNode? node, string? key)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj) ValidateNode(property.Value, property.Key);
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array) ValidateNode(item, key);
        }
        else if (node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
        {
            const string sensitiveName = @"(?:aws[_-]?)?(?:password|secret|token|access[_-]?token|refresh[_-]?token|id[_-]?token|session[_-]?token|client[_-]?secret|secret[_-]?access[_-]?key|access[_-]?key(?:[_-]?id)?|account[_-]?key|api[_-]?key|private[_-]?key|authorization|credentials?|sig)";
            if ((key is not null && Regex.IsMatch(key, $"(?i)^{sensitiveName}$"))
                || Regex.IsMatch(text, $@"(?i)\b(bearer\s+\S+|{sensitiveName}['""]?\s*[:=]\s*['""]?\S+|AKIA[0-9A-Z]{{16}}|eyJ[A-Za-z0-9_-]{{10,}}\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+|-----BEGIN [A-Z ]*PRIVATE KEY-----)"))
                throw new InvalidOperationException("Remediation plans containing credentials or tokens cannot be persisted.");
            if (text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('['))
            {
                try { ValidateNode(JsonNode.Parse(text), null); }
                catch (JsonException) { }
            }
        }
    }

    private static T Deserialize<T>(string payload)
        => JsonSerializer.Deserialize<T>(payload, JsonOptions) ?? throw new JsonException("Invalid remediation payload.");

    private sealed record ApprovalPayload(
        string? ApprovedBy, Guid? ApprovedByUserId, DateTime? ApprovedAt, string? ApprovalComment,
        string? ApprovedChangeSetFingerprint, Guid? DryRunApprovedByUserId, string? ApprovedDryRunFingerprint);

    private static RemediationPlan Normalize(RemediationPlan plan) => plan with
    {
        Changes = plan.Changes ?? Array.Empty<RemediationChange>(),
        RollbackChanges = plan.RollbackChanges ?? Array.Empty<RemediationChange>(),
        AuditTrail = (plan.AuditTrail ?? Array.Empty<RemediationAuditEntry>())
            .Select(entry => entry.Id == Guid.Empty ? entry with { Id = Guid.NewGuid() } : entry).DistinctBy(entry => entry.Id).ToArray(),
        ExecutionHistory = (plan.ExecutionHistory ?? Array.Empty<RemediationExecution>())
            .DistinctBy(execution => execution.Id).OrderBy(execution => execution.CompletedAt).ToArray()
    };
}
