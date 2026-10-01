using CloudEntitle.Alerting.Models;
using CloudEntitle.Compliance.Models;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.IdentityGovernance.Models;
using CloudEntitle.Monitoring.Models;
using Serilog;

namespace CloudEntitle.Alerting.Services;

public sealed class AlertingService
{
    public AlertDispatchResult Evaluate(
        IReadOnlyList<RiskFindingModel> findings,
        DriftReport? drift = null,
        ComplianceAssessment? compliance = null,
        IReadOnlyList<IdentityGovernanceRecord>? governance = null,
        AlertingOptions? options = null,
        DateTime? now = null)
    {
        options ??= new AlertingOptions();
        var timestamp = now ?? DateTime.UtcNow;
        var candidates = new List<Alert>();
        if (options.AlertOnNewRiskFindings) candidates.AddRange(findings.Where(finding => finding.Status == FindingStatus.Open && ToAlertSeverity(finding.Severity) >= options.MinimumSeverity).Select(ToAlert));
        if (options.AlertOnEffectiveAccessDrift && drift != null) candidates.AddRange(drift.Events.Where(item => item.EntityType == "EffectiveAccess" && item.Type == DriftType.Added && item.Severity >= DriftSeverity.High).Select(item => ToAlert(item)));
        if (options.AlertOnComplianceFailure && compliance != null) candidates.AddRange(compliance.Controls.Where(control => control.State == ComplianceControlState.Fail).Select(control => ToAlert(control, timestamp)));
        if (options.AlertOnGovernanceUnknown && governance != null) candidates.AddRange(governance.Where(item => item.LifecycleState == GovernanceLifecycleState.Unknown && item.IsPrivileged).Select(item => ToAlert(item, timestamp)));

        var unique = candidates.GroupBy(alert => alert.Fingerprint, StringComparer.Ordinal).Select(group => group.OrderByDescending(alert => alert.CreatedAt).First()).ToArray();
        Log.Information("Alert evaluation completed: {Created} alerts", unique.Length);
        return new AlertDispatchResult(unique.Length, candidates.Count - unique.Length, Array.Empty<string>());
    }

    public NotificationMessage CreateNotification(Alert alert, NotificationChannelType channel) =>
        new(alert, channel, $"{alert.Title}\n{alert.Description}\nEvidence: {string.Join("; ", alert.Evidence.Select(item => $"{item.Key}={item.Value}"))}");

    private static Alert ToAlert(RiskFindingModel finding) => new(finding.Id, finding.Fingerprint, AlertSource.RiskFinding, ToAlertSeverity(finding.Severity), AlertStatus.New, finding.CloudProvider, finding.Title, finding.Description, new Dictionary<string, string> { ["Evidence"] = finding.Evidence.Summary, ["EvaluationState"] = finding.EvaluationState.ToString() }, finding.CreatedAt, null, null);
    private static Alert ToAlert(DriftEvent drift) => new(drift.Id, $"drift|{drift.EntityType}|{drift.EntityKey}|{drift.Type}", AlertSource.DriftEvent, drift.Severity switch { DriftSeverity.Critical => AlertSeverity.Critical, DriftSeverity.High => AlertSeverity.High, DriftSeverity.Medium => AlertSeverity.Medium, DriftSeverity.Low => AlertSeverity.Low, _ => AlertSeverity.Informational }, AlertStatus.New, drift.Provider, drift.Summary, $"{drift.Type} {drift.EntityType} detected between scans.", new Dictionary<string, string> { ["EntityKey"] = drift.EntityKey, ["Evidence"] = drift.Evidence }, drift.DetectedAt, null, null);
    private static Alert ToAlert(ComplianceControl control, DateTime now) => new(Guid.NewGuid(), $"compliance|{control.ControlId}|{control.State}", AlertSource.ComplianceControl, AlertSeverity.High, AlertStatus.New, null, $"Compliance control failed: {control.Title}", control.Description, new Dictionary<string, string> { ["ControlId"] = control.ControlId, ["Gaps"] = string.Join("; ", control.Gaps) }, now, null, null);
    private static Alert ToAlert(IdentityGovernanceRecord identity, DateTime now) => new(Guid.NewGuid(), $"governance|{identity.PrincipalId}|unknown-activity", AlertSource.Governance, AlertSeverity.High, AlertStatus.New, null, "Privileged identity activity unknown", $"Activity evidence is unavailable for {identity.DisplayName}.", new Dictionary<string, string> { ["PrincipalId"] = identity.PrincipalId.ToString(), ["LifecycleState"] = identity.LifecycleState.ToString() }, now, null, null);
    private static AlertSeverity ToAlertSeverity(RiskSeverity severity) => severity switch { RiskSeverity.Critical => AlertSeverity.Critical, RiskSeverity.High => AlertSeverity.High, RiskSeverity.Medium => AlertSeverity.Medium, RiskSeverity.Low => AlertSeverity.Low, _ => AlertSeverity.Informational };
}
