using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.IdentityGovernance.Models;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;
using DomainAccessPath = CloudEntitle.Domain.Entities.AccessPath;

namespace CloudEntitle.Compliance.Models;

public enum ComplianceControlState { Pass, Fail, Partial, NotAssessed }
public enum ComplianceFramework { CiemBaseline, LeastPrivilege, IdentityGovernance, AccessReview }
public enum ComplianceStandard { CisControlsV8_1, Nist80053Rev5, Iso27001_2022, Soc2TrustServices }
public enum ComplianceExceptionStatus { NotAssessed, Active, Expired }

public sealed record ComplianceStandardMapping(ComplianceStandard Standard, string Reference, string Rationale)
{
    public string SourceControlId { get; init; } = string.Empty;
    public string StandardVersion { get; init; } = string.Empty;
    public string MappingCatalogVersion { get; init; } = string.Empty;
    public string Relationship { get; init; } = "Informative alignment";
}

public sealed record ComplianceControlLineage(
    string ControlId,
    ComplianceFramework Framework,
    string MappingCatalogVersion,
    IReadOnlyList<ComplianceStandardMapping> Mappings,
    IReadOnlyList<ComplianceEvidenceReference> Evidence)
{
    public IReadOnlyList<ComplianceException> Exceptions { get; init; } = Array.Empty<ComplianceException>();
}

public sealed record ComplianceException(
    Guid Id,
    string ControlId,
    string Reference,
    string Rationale,
    string RecordedBy,
    DateTime RecordedAtUtc,
    DateTime ExpiresAtUtc,
    string? EvidenceReference)
{
    public ComplianceExceptionStatus Status { get; init; } = ComplianceExceptionStatus.NotAssessed;
}

public sealed record ComplianceEvidenceReference(
    string EvidenceId,
    string SourceType,
    string SourceId,
    Guid? CloudAccountId,
    DateTime? CollectedAtUtc,
    string Sha256);

public sealed record ComplianceAssessmentTrendPoint(
    Guid AssessmentId,
    ComplianceFramework Framework,
    DateTime AssessedAt,
    int Passed,
    int Failed,
    int Partial,
    int NotAssessed)
{
    public string MappingCatalogVersion { get; init; } = ComplianceStandardCatalogVersion.Legacy;
    public int ExceptionCount { get; init; }
}

public static class ComplianceStandardCatalogVersion
{
    public const string Current = "2026.10.0";
    public const string Legacy = "Unversioned (legacy assessment)";
}

public sealed record ComplianceControl(
    string ControlId,
    ComplianceFramework Framework,
    string Title,
    string Description,
    ComplianceControlState State,
    int EvidenceCount,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Gaps)
{
    public IReadOnlyList<ComplianceStandardMapping> Mappings { get; init; } = Array.Empty<ComplianceStandardMapping>();
    public IReadOnlyList<ComplianceEvidenceReference> EvidenceLineage { get; init; } = Array.Empty<ComplianceEvidenceReference>();
    public IReadOnlyList<ComplianceException> Exceptions { get; init; } = Array.Empty<ComplianceException>();
}

public sealed record ComplianceAssessment(
    Guid Id,
    ComplianceFramework Framework,
    DateTime AssessedAt,
    IReadOnlyList<ComplianceControl> Controls,
    int Passed,
    int Failed,
    int Partial,
    int NotAssessed)
{
    public IReadOnlyList<ComplianceStandard> SelectedStandards { get; init; } = Array.Empty<ComplianceStandard>();
    public string MappingCatalogVersion { get; init; } = ComplianceStandardCatalogVersion.Legacy;
    public bool IsCertification => false;
    public string EvidenceCaveat => "Evidence snapshot only; not a certification, legal opinion, or auditor attestation.";
    public IReadOnlyList<ComplianceException> Exceptions { get; init; } = Array.Empty<ComplianceException>();
}

public sealed record ComplianceContext(
    IReadOnlyList<CloudAccount> Accounts,
    IReadOnlyList<CloudPrincipal> Principals,
    IReadOnlyList<DomainEffectiveAccess> EffectiveAccess,
    IReadOnlyList<DomainAccessPath> AccessPaths,
    IReadOnlyList<RiskFindingModel> Findings,
    IReadOnlyList<IdentityGovernanceRecord> GovernanceRecords,
    IReadOnlyList<AuditEvent> AuditEvents)
{
    public IReadOnlyList<Scan> Scans { get; init; } = Array.Empty<Scan>();
    public IReadOnlyList<ComplianceException> Exceptions { get; init; } = Array.Empty<ComplianceException>();
}
