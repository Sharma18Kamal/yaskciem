using CloudEntitle.Domain;

namespace CloudEntitle.Analytics.Models;

public enum PermissionUsageState
{
    Used,
    Unused,
    Unknown
}

public sealed record PermissionUsageWindow(
    int Days,
    PermissionUsageState State,
    DateTime? LastUsedAtUtc,
    int SuccessfulActionCount)
{
    public PermissionUsageEvidenceState EvidenceState { get; init; } = PermissionUsageEvidenceState.InsufficientObservation;
    public int CandidateActionCount { get; init; }
}

public sealed record PermissionActionFrequency(string Action, int SuccessfulActionCount);

public sealed record PermissionUsageRecord(
    Guid EffectiveAccessId,
    Guid CloudAccountId,
    CloudProvider Provider,
    Guid PrincipalId,
    Guid PermissionId,
    string Permission,
    DateTime? LastUsedAtUtc,
    IReadOnlyList<PermissionUsageWindow> Windows,
    IReadOnlyList<PermissionActionFrequency> ActionFrequencies)
{
    public DateTime? FirstUsedAtUtc { get; init; }
    public Guid? ResourceId { get; init; }
    public string? PermissionAction { get; init; }
    public IReadOnlyList<Guid> EffectiveAccessIds { get; init; } = Array.Empty<Guid>();
    public string CorrelationStatus { get; init; } = "Identity/action correlation; source grant path may be unresolved.";
    public int CandidateActionCount90Days { get; init; }
}

public sealed record PermissionUsageWindowSummary(
    int Days,
    int Used,
    int Unused,
    int Unknown)
{
    public int NotObserved { get; init; }
    public int InsufficientObservation { get; init; }
    public int UnknownMapping { get; init; }
    public int CollectionIncomplete { get; init; }
}

public sealed record PermissionUsageReport(
    DateTime EvaluatedAtUtc,
    IReadOnlyList<PermissionUsageRecord> Permissions,
    IReadOnlyList<PermissionUsageWindowSummary> Summary);

public sealed record PermissionUsageTrend(DateTime BucketStartUtc, DateTime BucketEndUtc, string BucketType,
    CloudProvider Provider, Guid CloudAccountId, Guid? IdentityId, int ObservedActionCount,
    int CandidateActionCount, int UnknownMappingCount);

public sealed record UsageAnalyticsFindingRow(Guid RiskFindingId, Guid CloudAccountId, CloudProvider Provider,
    Guid IdentityId, string Identity, Guid PermissionId, string Permission, Guid? ResourceId, string? Resource,
    int RiskScore, RiskSeverity Severity, int ObservationWindowDays, UsageConfidenceLevel Confidence,
    string Evidence, DateTimeOffset ObservedAtUtc);

public sealed record UsageAnalyticsRecommendationRow(Guid RecommendationId, Guid CloudAccountId,
    CloudProvider Provider, string Type, string Title, string State, string Reason, string Evidence,
    bool Executable);
