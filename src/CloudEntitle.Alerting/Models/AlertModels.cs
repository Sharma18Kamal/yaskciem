using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Monitoring.Models;
using CloudEntitle.Compliance.Models;

namespace CloudEntitle.Alerting.Models;

public enum AlertSeverity { Informational, Low, Medium, High, Critical }
public enum AlertStatus { New, Acknowledged, Resolved, Suppressed }
public enum AlertSource { RiskFinding, DriftEvent, ComplianceControl, Governance }
public enum NotificationChannelType { InProcess, Log, Webhook }

public sealed record Alert(
    Guid Id,
    string Fingerprint,
    AlertSource Source,
    AlertSeverity Severity,
    AlertStatus Status,
    CloudProvider? Provider,
    string Title,
    string Description,
    IReadOnlyDictionary<string, string> Evidence,
    DateTime CreatedAt,
    DateTime? AcknowledgedAt,
    DateTime? ResolvedAt);

public sealed record AlertingOptions(
    AlertSeverity MinimumSeverity = AlertSeverity.High,
    bool AlertOnNewRiskFindings = true,
    bool AlertOnEffectiveAccessDrift = true,
    bool AlertOnComplianceFailure = true,
    bool AlertOnGovernanceUnknown = true,
    TimeSpan? DeduplicationWindow = null);

public sealed record NotificationMessage(Alert Alert, NotificationChannelType Channel, string Body);
public sealed record AlertDispatchResult(int Created, int Suppressed, IReadOnlyList<string> Diagnostics);
