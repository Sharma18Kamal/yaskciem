using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;

namespace CloudEntitle.Reporting.Models;

public enum ReportType { Executive, Technical, Identity, Entitlement, Risk, Recommendation }
public enum ReportFormat { Json, Csv, Html }
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
    DateTime GeneratedAt);

public sealed record GeneratedReport(ReportType Type, ReportFormat Format, string Content, DateTime GeneratedAt);
public sealed record ScheduledReport(Guid Id, string Name, ReportType Type, ReportFormat Format, ReportSchedule Schedule, DateTime NextRunAt, bool Enabled);
public sealed record ScheduledReportRun(Guid ScheduleId, DateTime StartedAt, DateTime CompletedAt, bool Succeeded, string? OutputPath, string? Error);
