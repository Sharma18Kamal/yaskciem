namespace CloudEntitle.Domain.Entities;

public sealed class CloudActivityEvent
{
    public Guid Id { get; set; }
    public CloudProvider Provider { get; set; }
    public Guid CloudAccountId { get; set; }
    public string TenantOrAccountId { get; set; } = string.Empty;
    public Guid? IdentityId { get; set; }
    public string ProviderIdentity { get; set; } = string.Empty;
    public string PrincipalType { get; set; } = "Unknown";
    public string Action { get; set; } = string.Empty;
    public string? PermissionAction { get; set; }
    public PermissionActionMappingStatus PermissionMappingStatus { get; set; }
    public string? PermissionMappingEvidence { get; set; }
    public string? ResourceReference { get; set; }
    public Guid? ResourceId { get; set; }
    public string Outcome { get; set; } = "Unknown";
    public bool OutcomeKnown { get; set; }
    public string SourceEventId { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string? RawEvidence { get; set; }
    public DateTimeOffset EventTimestamp { get; set; }
    public DateTimeOffset CollectionTimestamp { get; set; }
    public string DeduplicationKey { get; set; } = string.Empty;
}

public sealed class PermissionUsageRecord
{
    public Guid Id { get; set; }
    public Guid CloudAccountId { get; set; }
    public CloudProvider Provider { get; set; }
    public Guid IdentityId { get; set; }
    public Guid PermissionId { get; set; }
    public Guid? ResourceId { get; set; }
    public Guid? EffectiveAccessId { get; set; }
    public string EffectiveAccessIds { get; set; } = "[]";
    public string Action { get; set; } = string.Empty;
    public PermissionUsageEvidenceState State30Days { get; set; }
    public PermissionUsageEvidenceState State60Days { get; set; }
    public PermissionUsageEvidenceState State90Days { get; set; }
    public DateTimeOffset? FirstObservedAtUtc { get; set; }
    public DateTimeOffset? LastObservedAtUtc { get; set; }
    public int SuccessfulActions30Days { get; set; }
    public int SuccessfulActions60Days { get; set; }
    public int SuccessfulActions90Days { get; set; }
    public string CorrelationStatus { get; set; } = string.Empty;
    public DateTimeOffset CalculatedAtUtc { get; set; }
    public string Evidence { get; set; } = string.Empty;
}

public sealed class PermissionUsageSummary
{
    public Guid Id { get; set; }
    public Guid CloudAccountId { get; set; }
    public CloudProvider Provider { get; set; }
    public int WindowDays { get; set; }
    public Guid? IdentityId { get; set; }
    public string BucketType { get; set; } = "Snapshot";
    public DateTimeOffset BucketStartUtc { get; set; }
    public DateTimeOffset BucketEndUtc { get; set; }
    public int TotalEffectivePermissions { get; set; }
    public int Used { get; set; }
    public int NotObserved { get; set; }
    public int InsufficientObservation { get; set; }
    public int UnknownMapping { get; set; }
    public int CollectionIncomplete { get; set; }
    public int ObservedEventCount { get; set; }
    public int CandidateActionCount { get; set; }
    public DateTimeOffset CalculatedAtUtc { get; set; }
}

public sealed class PermissionObservationWindow
{
    public Guid Id { get; set; }
    public Guid SnapshotId { get; set; }
    public Guid CloudAccountId { get; set; }
    public CloudProvider Provider { get; set; }
    public DateTimeOffset ObservedFromUtc { get; set; }
    public DateTimeOffset ObservedThroughUtc { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool IncludesActions { get; set; }
    public bool IncludesResourceIds { get; set; }
    public bool IncludesRoleIds { get; set; }
    public bool Truncated { get; set; }
    public int RecordCount { get; set; }
    public string Details { get; set; } = string.Empty;
    public string Provenance { get; set; } = "[]";
    public DateTimeOffset CollectedAtUtc { get; set; }
}

public sealed class UsageAnalyticsFinding
{
    public Guid Id { get; set; }
    public Guid RiskFindingId { get; set; }
    public Guid CloudAccountId { get; set; }
    public CloudProvider Provider { get; set; }
    public Guid IdentityId { get; set; }
    public Guid PermissionId { get; set; }
    public Guid? ResourceId { get; set; }
    public int RiskScore { get; set; }
    public RiskSeverity Severity { get; set; }
    public int ObservationWindowDays { get; set; }
    public UsageConfidenceLevel Confidence { get; set; }
    public string Evidence { get; set; } = string.Empty;
    public DateTimeOffset ObservedAtUtc { get; set; }
}

public sealed class UsageRecommendation
{
    public Guid Id { get; set; }
    public Guid RecommendationId { get; set; }
    public Guid FindingId { get; set; }
    public Guid CloudAccountId { get; set; }
    public CloudProvider Provider { get; set; }
    public string RecommendationType { get; set; } = string.Empty;
    public string Status { get; set; } = "ReviewOnly";
    public string Evidence { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}
