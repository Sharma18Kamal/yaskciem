using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Normalization.Models;
using DomainAccessPath = CloudEntitle.Domain.Entities.AccessPath;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;

namespace CloudEntitle.Monitoring.Models;

public enum DriftType { Added, Removed, Changed }
public enum DriftSeverity { Informational, Low, Medium, High, Critical }

public sealed record MonitoringSnapshot(
    Guid ScanId,
    DateTime CollectedAt,
    IReadOnlyList<CloudAccount> Accounts,
    IReadOnlyList<CloudPrincipal> Principals,
    IReadOnlyList<NormalizedEntitlement> Entitlements,
    IReadOnlyList<DomainEffectiveAccess> EffectiveAccess,
    IReadOnlyList<DomainAccessPath> AccessPaths,
    IReadOnlyList<RiskFindingModel> Findings);

public sealed record DriftEvent(
    Guid Id,
    Guid CurrentScanId,
    Guid? PreviousScanId,
    DriftType Type,
    DriftSeverity Severity,
    CloudProvider Provider,
    string EntityType,
    string EntityKey,
    string Summary,
    string Evidence,
    DateTime DetectedAt);

public sealed record DriftReport(
    Guid CurrentScanId,
    Guid? PreviousScanId,
    IReadOnlyList<DriftEvent> Events,
    int Added,
    int Removed,
    int Changed,
    DateTime GeneratedAt);
