using CloudEntitle.Domain;

namespace CloudEntitle.Integration.Contracts;

/// <summary>A retrospective client-owned emergency access report. It never issues credentials or bypasses approvals.</summary>
public sealed record CreateBreakGlassReportRequest(Guid ReportId, CloudProvider Provider, string AccountId,
    string ExternalPrincipal, DateTimeOffset StartedAtUtc, DateTimeOffset EndedAtUtc, string Reason,
    string ClientIncidentReference, Guid? ActivitySnapshotId = null);

/// <summary>A configured security reviewer's post-outage attestation, not provider-verified revocation.</summary>
public sealed record ReviewBreakGlassReportRequest(string Reason, string ClientClosureReference, bool ClientVerifiedClosure);

/// <summary>An immutable incident and optional independent review; activity matches never prove privilege revocation.</summary>
public sealed record BreakGlassReport(Guid ReportId, CloudProvider Provider, string AccountId, string ExternalPrincipal,
    DateTimeOffset StartedAtUtc, DateTimeOffset EndedAtUtc, string Reason, string ClientIncidentReference,
    Guid ReportedBy, DateTimeOffset ReportedAtUtc, Guid? ActivitySnapshotId, IReadOnlyList<string> MatchingEventIds,
    string EvidenceStatus, BreakGlassReview? Review = null);

/// <summary>An immutable, attributable review of client emergency-account cleanup and credential rotation.</summary>
public sealed record BreakGlassReview(Guid ReviewerId, DateTimeOffset ReviewedAtUtc, string Reason,
    string ClientClosureReference, bool ClientVerifiedClosure);
