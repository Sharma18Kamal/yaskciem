using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;

namespace CloudEntitle.Reporting.Models;

public enum ReportType
{
    Executive, Technical, Identity, Entitlement, Risk, Recommendation,
    IdentityRisk, PermissionUsage, ToxicCombination, AttackPath, Remediation, ComplianceAssessment, AuditHistory
}
public enum ReportFormat { Json, Csv, Html, Pdf, Excel }
public enum ReportSchedule { Daily, Weekly, Monthly }

public sealed record ReportingSnapshot(
    IReadOnlyList<CloudAccount> Accounts,
    IReadOnlyList<CloudPrincipal> Principals,
    IReadOnlyList<CloudResource> Resources,
    IReadOnlyList<Entitlement> Entitlements,
    IReadOnlyList<DomainEffectiveAccess> EffectiveAccess,
    IReadOnlyList<RiskFindingModel> Findings,
    IReadOnlyList<RecommendationModel> Recommendations,
    IReadOnlyList<Scan> Scans,
    DateTime GeneratedAt)
{
    public IReadOnlyList<PermissionUsageSummary> PermissionUsageSummaries { get; init; } = Array.Empty<PermissionUsageSummary>();
    public IReadOnlyList<ToxicCombinationFinding> ToxicCombinationFindings { get; init; } = Array.Empty<ToxicCombinationFinding>();
    public IReadOnlyList<AccessPath> AttackPaths { get; init; } = Array.Empty<AccessPath>();
    public IReadOnlyList<ReportRemediationPlan> RemediationPlans { get; init; } = Array.Empty<ReportRemediationPlan>();
    public IReadOnlyList<AuditEvent> AuditEvents { get; init; } = Array.Empty<AuditEvent>();
    public IReadOnlyList<ComplianceReportControl> ComplianceControls { get; init; } = Array.Empty<ComplianceReportControl>();
    public IReadOnlyList<RiskFinding> SupplementalFindings { get; init; } = Array.Empty<RiskFinding>();
}

public sealed record ComplianceReportControl(string ControlId, string Framework, string State, string Evidence, string Gaps);
public sealed record ReportRemediationPlan(Guid Id, string Provider, string Status, DateTime CreatedAt, DateTime UpdatedAt, string Details);
public sealed record GeneratedReport(ReportType Type, ReportFormat Format, string Content, DateTime GeneratedAt)
{
    public byte[]? BinaryContent { get; init; }
}
public sealed record ScheduledReport(Guid Id, string Name, ReportType Type, ReportFormat Format, ReportSchedule Schedule, DateTime NextRunAt, bool Enabled);
public sealed record ScheduledReportRun(Guid ScheduleId, DateTime StartedAt, DateTime CompletedAt, bool Succeeded, string? OutputPath, string? Error);
