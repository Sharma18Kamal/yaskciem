using CloudEntitle.Domain;

namespace CloudEntitle.Integration.Contracts;

/// <summary>A server-configured read-only provider connection; credentials are never returned.</summary>
public sealed record EffectiveDecisionSource(
    string SourceId, CloudProvider Provider, string AccountId, string CollectorIdentity,
    string ConfigurationSha256, bool Enabled, string CoverageNotice,
    IReadOnlyList<string> Principals, IReadOnlyList<string> PrivilegedPermissions, string FullResourceName);

/// <summary>Explicit consent to collect one exact configured principal/permission/resource tuple.</summary>
public sealed record CaptureEffectiveDecisionRequest(
    string SourceId, string Principal, string Permission, string FullResourceName, bool ReadConsent);

/// <summary>An immutable projection of a directly collected native provider decision, not an uploaded claim.</summary>
public sealed record EffectiveDecisionSnapshot(
    Guid SnapshotId, CloudProvider Provider, string AccountId, string SourceId, string ConfigurationSha256,
    string CollectorIdentity, string Principal, string Permission, string FullResourceName,
    string ContextSha256, DateTimeOffset ContextObservedAtUtc, DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc, string Decision, string Coverage, string VerificationProfile,
    string ResponseSha256, IReadOnlyList<string> PolicyVersions, IReadOnlyList<string> Blockers);

/// <summary>Selects two saved server-origin observations; callers cannot supply decisions or verification flags.</summary>
public sealed record AnalyzeDecisionEscalationRequest(string SourceId, Guid BeforeSnapshotId, Guid AfterSnapshotId);

/// <summary>A narrowly scoped provider decision transition, not proof of access to all actions or historical abuse.</summary>
public sealed record DecisionEscalationReport(
    Guid AnalysisId, Guid BeforeSnapshotId, Guid AfterSnapshotId, string Status, string Principal,
    string Permission, string FullResourceName, string ContextSha256, DateTimeOffset AnalyzedAtUtc,
    IReadOnlyList<string> Blockers, string Explanation);
