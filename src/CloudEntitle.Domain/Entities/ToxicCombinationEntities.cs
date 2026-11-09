namespace CloudEntitle.Domain.Entities;

public enum ToxicResourceRequirement
{
    Any,
    SensitiveStorage,
    CriticalDatabase,
    SensitiveOrCritical
}

public sealed class ToxicCombinationRule
{
    public Guid Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public CloudProvider? Provider { get; set; }
    public string FirstPermissionPattern { get; set; } = string.Empty;
    public string SecondPermissionPattern { get; set; } = string.Empty;
    public ToxicResourceRequirement ResourceRequirement { get; set; }
    public bool FirstRequiresPrivilegedAccess { get; set; }
    public bool SecondRequiresPrivilegedAccess { get; set; }
    public bool RequireSameResource { get; set; }
    public bool RequireTrustPath { get; set; }
    public RiskSeverity Severity { get; set; } = RiskSeverity.High;
    public int RiskScore { get; set; } = 70;
    public bool Enabled { get; set; } = true;
    public string Recommendation { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ToxicCombinationFinding
{
    public Guid Id { get; set; }
    public Guid RuleId { get; set; }
    public string RuleKey { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public CloudProvider Provider { get; set; }
    public Guid CloudAccountId { get; set; }
    public Guid PrincipalId { get; set; }
    public Guid? ResourceId { get; set; }
    public RiskSeverity Severity { get; set; }
    public int RiskScore { get; set; }
    public RiskEvaluationState EvaluationState { get; set; }
    public string Confidence { get; set; } = "Low";
    public string ResourceRelation { get; set; } = "Unknown";
    public string EvidenceSummary { get; set; } = string.Empty;
    public string Recommendation { get; set; } = string.Empty;
    public FindingStatus Status { get; set; } = FindingStatus.Open;
    public DateTime FirstDetectedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastEvaluatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ToxicCombinationEvidence
{
    public Guid Id { get; set; }
    public Guid FindingId { get; set; }
    public Guid EffectiveAccessId { get; set; }
    public Guid PermissionId { get; set; }
    public Guid? ResourceId { get; set; }
    public Guid? RoleId { get; set; }
    public Guid? PolicyId { get; set; }
    public Guid? AccessPathId { get; set; }
    public string PermissionAction { get; set; } = string.Empty;
    public string? Scope { get; set; }
    public string AssignmentType { get; set; } = string.Empty;
    public bool IsDirect { get; set; }
    public bool IsInherited { get; set; }
    public bool IsConditional { get; set; }
    public string EvaluationStatus { get; set; } = string.Empty;
    public string SourceEvidence { get; set; } = string.Empty;
}

public sealed class PermissionCombination
{
    public Guid Id { get; set; }
    public Guid FindingId { get; set; }
    public Guid FirstPermissionId { get; set; }
    public Guid SecondPermissionId { get; set; }
    public Guid FirstEffectiveAccessId { get; set; }
    public Guid SecondEffectiveAccessId { get; set; }
    public string FirstAction { get; set; } = string.Empty;
    public string SecondAction { get; set; } = string.Empty;
    public string ProviderSemantics { get; set; } = string.Empty;
    public string ScopeAssessment { get; set; } = string.Empty;
}

public sealed class ToxicCombinationRecommendation
{
    public Guid Id { get; set; }
    public Guid FindingId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Evidence { get; set; } = string.Empty;
    public bool Executable { get; set; }
    public string Status { get; set; } = "ReviewOnly";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ToxicCombinationTrustPathEvidence
{
    public Guid Id { get; set; }
    public Guid FindingId { get; set; }
    public int Sequence { get; set; }
    public CloudProvider SourceProvider { get; set; }
    public Guid SourceCloudAccountId { get; set; }
    public Guid SourcePrincipalId { get; set; }
    public CloudProvider TargetProvider { get; set; }
    public Guid TargetCloudAccountId { get; set; }
    public Guid TargetPrincipalId { get; set; }
    public string RelationshipType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string EvidenceSource { get; set; } = string.Empty;
    public string EvidenceReference { get; set; } = string.Empty;
    public bool RequiresSourcePermission { get; set; }
    public string? RequiredSourcePermissionPattern { get; set; }
    public string ConditionEvidence { get; set; } = string.Empty;
    public DateTime ObservedAtUtc { get; set; }
}
