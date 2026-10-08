using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.IdentityGovernance.Models;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;
using DomainAccessPath = CloudEntitle.Domain.Entities.AccessPath;

namespace CloudEntitle.Compliance.Models;

public enum ComplianceControlState { Pass, Fail, Partial, NotAssessed }
public enum ComplianceFramework { CiemBaseline, LeastPrivilege, IdentityGovernance, AccessReview }
public enum ComplianceStandard { Nist80053Rev5, Iso27001_2022, Soc2TrustServices }

public sealed record ComplianceStandardMapping(ComplianceStandard Standard, string Reference, string Rationale);

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
    int NotAssessed);

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
}
