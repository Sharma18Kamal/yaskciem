using CloudEntitle.Domain;
using CloudEntitle.Normalization.Models;

namespace CloudEntitle.EffectiveAccess.Models;

public enum WhatIfTargetKind { Role, Policy }
public enum WhatIfImpact { ModeledAccessLost, RetainedByAlternative, AlreadyNotAllowed, PotentialAccessLoss, Inconclusive }

public sealed record OfflineAuthorizationBundle(
    string SchemaVersion,
    string SourceLabel,
    ProviderDiscoveryResult Discovery,
    IReadOnlyList<CloudResource> Resources,
    IReadOnlyList<CloudEntitle.Domain.Entities.GroupMembership> GroupMemberships,
    IReadOnlyList<string> Limitations);

public sealed record WhatIfInput(AccessEvaluationRequest Evaluation, string SourceLabel, IReadOnlyList<string> Limitations);
public sealed record WhatIfChange(CloudProvider Provider, Guid AccountId, WhatIfTargetKind TargetKind,
    Guid TargetId, string Action);

public sealed record WhatIfOrigin(string SourceId, string SourceType, Guid? RoleId, Guid? PolicyId,
    string Scope, string Action, bool Inherited, string EvidenceSha256, string? OriginalStatement);

public sealed record WhatIfAccessChange(Guid PrincipalId, string Principal, Guid ResourceId, string Resource,
    AccessDecision Before, EvaluationStatus BeforeStatus, AccessDecision After, EvaluationStatus AfterStatus,
    WhatIfImpact Impact, bool IsInherited, IReadOnlyList<WhatIfOrigin> RemovedOrigins,
    IReadOnlyList<WhatIfOrigin> AlternativeOrigins, IReadOnlyList<AccessExplanationPath> BeforePaths,
    IReadOnlyList<AccessExplanationPath> AfterPaths, IReadOnlyList<AccessEvaluationDiagnostic> Diagnostics);

public sealed record WhatIfReport(WhatIfChange Change, string SourceLabel, DateTime EvaluatedAtUtc,
    string InputSha256, IReadOnlyList<WhatIfAccessChange> AccessChanges,
    int ModeledAllowedBefore, int ModeledAllowedAfter, int ModeledLostPairs, int PotentialLossPairs,
    decimal? ModeledExposureReductionPercent, string RiskExplanation, IReadOnlyList<string> Limitations)
{
    public bool MutatesCloud => false;
    public bool IsProviderVerified => false;
    public string Notice => "Offline model prediction, not a live authorization decision or proof of operational safety. NotApplicable means no modeled grant, not provider-confirmed denial. Distinct actions are not assumed equivalent.";
}
