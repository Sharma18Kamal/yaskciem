using CloudEntitle.Domain;
using CloudEntitle.Normalization.Models;

namespace CloudEntitle.EffectiveAccess.Models;

public enum WhatIfTargetKind { Role, Policy, Identity }
public enum WhatIfOperation { Add, Remove, Replace }
public enum WhatIfImpact
{
    ModeledAccessLost,
    ModeledAccessAdded,
    RetainedByAlternative,
    AlreadyNotAllowed,
    PotentialAccessLoss,
    PotentialAccessGain,
    Inconclusive
}
public enum WhatIfOperationalImpactState { Known, Potential, Unknown }

public sealed record OfflineAuthorizationBundle(
    string SchemaVersion,
    string SourceLabel,
    ProviderDiscoveryResult Discovery,
    IReadOnlyList<CloudResource> Resources,
    IReadOnlyList<CloudEntitle.Domain.Entities.GroupMembership> GroupMemberships,
    IReadOnlyList<string> Limitations);

public sealed record WhatIfInput(AccessEvaluationRequest Evaluation, string SourceLabel, IReadOnlyList<string> Limitations)
{
    public DateTime? SourceSnapshotAtUtc { get; init; }
}
public sealed record WhatIfChange(CloudProvider Provider, Guid AccountId, WhatIfTargetKind TargetKind,
    Guid TargetId, string Action)
{
    public WhatIfOperation Operation { get; init; } = WhatIfOperation.Remove;
    public string? ReplacementAction { get; init; }
    public string? ResourceScope { get; init; }
}

public sealed record WhatIfOrigin(string SourceId, string SourceType, Guid? RoleId, Guid? PolicyId,
    string Scope, string Action, bool Inherited, string EvidenceSha256, string? OriginalStatement);

public sealed record WhatIfAccessChange(Guid PrincipalId, string Principal, Guid ResourceId, string Resource,
    AccessDecision Before, EvaluationStatus BeforeStatus, AccessDecision After, EvaluationStatus AfterStatus,
    WhatIfImpact Impact, bool IsInherited, IReadOnlyList<WhatIfOrigin> RemovedOrigins,
    IReadOnlyList<WhatIfOrigin> AlternativeOrigins, IReadOnlyList<AccessExplanationPath> BeforePaths,
    IReadOnlyList<AccessExplanationPath> AfterPaths, IReadOnlyList<AccessEvaluationDiagnostic> Diagnostics)
{
    public string Action { get; init; } = string.Empty;
    public IReadOnlyList<WhatIfOrigin> AddedOrigins { get; init; } = Array.Empty<WhatIfOrigin>();
}

public sealed record WhatIfFinding(string Key, string Title, int Score, string Severity, string State, string Evidence);
public sealed record WhatIfAttackPath(string Key, string Category, string Reachability, int RiskScore, string Evidence);
public sealed record WhatIfSecurityImpact(
    bool IsComplete,
    int? RiskScoreBefore,
    int? RiskScoreAfter,
    int RiskFindingsBefore,
    int RiskFindingsAfter,
    int ToxicFindingsBefore,
    int ToxicFindingsAfter,
    int AttackPathsBefore,
    int AttackPathsAfter,
    IReadOnlyList<WhatIfFinding> AddedFindings,
    IReadOnlyList<WhatIfFinding> RemovedFindings,
    IReadOnlyList<WhatIfAttackPath> AddedAttackPaths,
    IReadOnlyList<WhatIfAttackPath> RemovedAttackPaths,
    IReadOnlyList<string> Diagnostics);

public sealed record WhatIfReport(WhatIfChange Change, string SourceLabel, DateTime EvaluatedAtUtc,
    string InputSha256, IReadOnlyList<WhatIfAccessChange> AccessChanges,
    int ModeledAllowedBefore, int ModeledAllowedAfter, int ModeledLostPairs, int PotentialLossPairs,
    decimal? ModeledExposureReductionPercent, string RiskExplanation, IReadOnlyList<string> Limitations)
{
    public DateTime? SourceSnapshotAtUtc { get; init; }
    public WhatIfSecurityImpact? SecurityImpact { get; init; }
    public string OperationalImpactAssessment { get; init; } =
        "Operational dependencies were not assessed because activity evidence was not included in the simulation snapshot.";
    public WhatIfOperationalImpactState OperationalImpactState { get; init; } = WhatIfOperationalImpactState.Unknown;
    public bool MutatesCloud => false;
    public bool IsProviderVerified => false;
    public string Notice => "Isolated what-if prediction only. No provider API is called or changed; results are not provider-confirmed authorization or proof of operational safety.";
}

public sealed record WhatIfSecuritySnapshot(
    AccessEvaluationRequest Request,
    CloudEntitle.EffectiveAccess.Models.AccessEvaluationResult Evaluation);

public interface IWhatIfScenarioAnalyzer
{
    Task<WhatIfSecurityImpact> AnalyzeAsync(
        WhatIfSecuritySnapshot before,
        WhatIfSecuritySnapshot after,
        CancellationToken cancellationToken = default);
}
