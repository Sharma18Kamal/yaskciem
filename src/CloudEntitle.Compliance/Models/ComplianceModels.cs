using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.IdentityGovernance.Models;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;
using DomainAccessPath = CloudEntitle.Domain.Entities.AccessPath;

namespace CloudEntitle.Compliance.Models;

public enum ComplianceControlState { Pass, Fail, Partial, NotAssessed }
public enum ComplianceFramework { CiemBaseline, LeastPrivilege, IdentityGovernance, AccessReview }

public sealed record ComplianceControl(
    string ControlId,
    ComplianceFramework Framework,
    string Title,
    string Description,
    ComplianceControlState State,
    int EvidenceCount,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Gaps);

public sealed record ComplianceAssessment(
    Guid Id,
    ComplianceFramework Framework,
    DateTime AssessedAt,
    IReadOnlyList<ComplianceControl> Controls,
    int Passed,
    int Failed,
    int Partial,
    int NotAssessed);

public sealed record ComplianceContext(
    IReadOnlyList<CloudAccount> Accounts,
    IReadOnlyList<CloudPrincipal> Principals,
    IReadOnlyList<DomainEffectiveAccess> EffectiveAccess,
    IReadOnlyList<DomainAccessPath> AccessPaths,
    IReadOnlyList<RiskFindingModel> Findings,
    IReadOnlyList<IdentityGovernanceRecord> GovernanceRecords,
    IReadOnlyList<AuditEvent> AuditEvents);
