using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Monitoring.Models;
using CloudEntitle.Normalization.Models;
using Serilog;
using DomainAccessPath = CloudEntitle.Domain.Entities.AccessPath;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;

namespace CloudEntitle.Monitoring.Services;

public sealed class DriftDetectionService
{
    public Task<DriftReport> CompareAsync(MonitoringSnapshot? previous, MonitoringSnapshot current, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var events = new List<DriftEvent>();
        CompareEntitlements(previous, current, events);
        CompareEffectiveAccess(previous, current, events);
        ComparePaths(previous, current, events);
        CompareFindings(previous, current, events);
        var report = new DriftReport(current.ScanId, previous?.ScanId, events, events.Count(item => item.Type == DriftType.Added), events.Count(item => item.Type == DriftType.Removed), events.Count(item => item.Type == DriftType.Changed), DateTime.UtcNow);
        Log.Information("Drift comparison completed for scan {ScanId}: {Added} added, {Removed} removed, {Changed} changed", current.ScanId, report.Added, report.Removed, report.Changed);
        return Task.FromResult(report);
    }

    private static void CompareEntitlements(MonitoringSnapshot? previous, MonitoringSnapshot current, List<DriftEvent> events)
    {
        var before = (previous?.Entitlements ?? Array.Empty<NormalizedEntitlement>()).ToDictionary(Key);
        var after = current.Entitlements.ToDictionary(Key);
        CompareSet(before, after, current, previous, "Entitlement", events, item => item.Provider, item => $"Principal={item.PrincipalId}; Permission={item.PermissionId}; Scope={item.Scope.OriginalScope}");
    }

    private static void CompareEffectiveAccess(MonitoringSnapshot? previous, MonitoringSnapshot current, List<DriftEvent> events)
    {
        var before = (previous?.EffectiveAccess ?? Array.Empty<DomainEffectiveAccess>()).ToDictionary(item => item.Id.ToString("N"));
        var after = current.EffectiveAccess.ToDictionary(item => item.Id.ToString("N"));
        CompareSet(before, after, current, previous, "EffectiveAccess", events, item => item.Provider, item => $"Decision={item.Decision}; Principal={item.PrincipalId}; Resource={item.ResourceId}; Permission={item.PermissionId}", (old, @new) => old.Decision != @new.Decision || old.Scope != @new.Scope || old.AssignmentType != @new.AssignmentType);
    }

    private static void ComparePaths(MonitoringSnapshot? previous, MonitoringSnapshot current, List<DriftEvent> events)
    {
        var before = (previous?.AccessPaths ?? Array.Empty<DomainAccessPath>()).ToDictionary(item => item.Id.ToString("N"));
        var after = current.AccessPaths.ToDictionary(item => item.Id.ToString("N"));
        CompareSet(before, after, current, previous, "AccessPath", events, item => item.Provider, item => $"Principal={item.PrincipalId}; Resource={item.ResourceId}; Type={item.PathType}; Status={item.PathStatus}", (old, @new) => old.PathStatus != @new.PathStatus || old.Nodes.Count != @new.Nodes.Count || old.Edges.Count != @new.Edges.Count);
    }

    private static void CompareFindings(MonitoringSnapshot? previous, MonitoringSnapshot current, List<DriftEvent> events)
    {
        var before = (previous?.Findings ?? Array.Empty<RiskFindingModel>()).ToDictionary(item => item.Fingerprint);
        var after = current.Findings.ToDictionary(item => item.Fingerprint);
        CompareSet(before, after, current, previous, "RiskFinding", events, item => item.CloudProvider, item => $"Rule={item.RuleId}; Severity={item.Severity}; Status={item.Status}", (old, @new) => old.Status != @new.Status || old.Severity != @new.Severity || old.EvaluationState != @new.EvaluationState);
    }

    private static void CompareSet<T>(IReadOnlyDictionary<string, T> before, IReadOnlyDictionary<string, T> after, MonitoringSnapshot current, MonitoringSnapshot? previous, string entityType, List<DriftEvent> events, Func<T, CloudProvider> provider, Func<T, string> summary, Func<T, T, bool>? changed = null)
    {
        foreach (var item in after.Where(item => !before.ContainsKey(item.Key))) Add(DriftType.Added, item.Key, item.Value);
        foreach (var item in before.Where(item => !after.ContainsKey(item.Key))) Add(DriftType.Removed, item.Key, item.Value);
        if (changed != null)
        {
            foreach (var item in after.Where(item => before.TryGetValue(item.Key, out var old) && changed(old!, item.Value))) Add(DriftType.Changed, item.Key, item.Value);
        }

        void Add(DriftType type, string key, T item)
        {
            var severity = entityType == "EffectiveAccess" && type == DriftType.Added ? DriftSeverity.High : entityType == "RiskFinding" && type == DriftType.Added ? DriftSeverity.Medium : DriftSeverity.Informational;
            events.Add(new DriftEvent(Guid.NewGuid(), current.ScanId, previous?.ScanId, type, severity, provider(item), entityType, key, summary(item), summary(item), DateTime.UtcNow));
        }
    }

    private static string Key(NormalizedEntitlement item) => string.Join('|', item.Provider, item.CloudAccountId, item.PrincipalId, item.RoleId, item.PolicyId, item.PermissionId, item.Scope.OriginalScope, item.AssignmentType, item.AccessType, item.ResourcePattern);
}
