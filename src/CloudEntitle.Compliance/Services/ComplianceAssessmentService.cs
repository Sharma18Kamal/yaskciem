using CloudEntitle.Compliance.Models;
using CloudEntitle.Domain;
using System.Security.Cryptography;
using System.Text;

namespace CloudEntitle.Compliance.Services;

public sealed class ComplianceAssessmentService
{
    public ComplianceAssessment Assess(
        ComplianceContext context,
        ComplianceFramework framework,
        DateTime? assessedAt = null,
        IReadOnlyCollection<ComplianceStandard>? selectedStandards = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var assessedAtUtc = (assessedAt ?? DateTime.UtcNow).ToUniversalTime();
        var standards = (selectedStandards ?? ComplianceStandardCatalog.Standards).Distinct().ToArray();
        var controls = framework switch
        {
            ComplianceFramework.LeastPrivilege => LeastPrivilege(context),
            ComplianceFramework.IdentityGovernance => IdentityGovernance(context),
            ComplianceFramework.AccessReview => AccessReview(context),
            _ => CiemBaseline(context)
        };
        var controlIds = controls.Select(control => control.ControlId).ToHashSet(StringComparer.Ordinal);
        var exceptions = context.Exceptions.Select(exception =>
        {
            if (exception.Id == Guid.Empty || string.IsNullOrWhiteSpace(exception.ControlId)
                || string.IsNullOrWhiteSpace(exception.Reference) || string.IsNullOrWhiteSpace(exception.Rationale)
                || string.IsNullOrWhiteSpace(exception.RecordedBy))
                throw new ArgumentException("Compliance exceptions require an ID, control, reference, rationale, and recorded-by identity.", nameof(context));
            if (!controlIds.Contains(exception.ControlId))
                throw new ArgumentException($"Compliance exception references unknown control '{exception.ControlId}'.", nameof(context));
            if (exception.ExpiresAtUtc.ToUniversalTime() <= exception.RecordedAtUtc.ToUniversalTime())
                throw new ArgumentException("Compliance exception expiry must be later than its recorded time.", nameof(context));
            return exception with { Status = exception.ExpiresAtUtc.ToUniversalTime() > assessedAtUtc ? ComplianceExceptionStatus.Active : ComplianceExceptionStatus.Expired };
        }).ToArray();
        controls = controls.Select(control => control with
        {
            Mappings = ComplianceStandardCatalog.GetMappings(control.ControlId, standards),
            Exceptions = exceptions.Where(exception => exception.ControlId == control.ControlId).ToArray()
        }).ToArray();
        return new ComplianceAssessment(Guid.NewGuid(), framework, assessedAtUtc, controls, controls.Count(item => item.State == ComplianceControlState.Pass), controls.Count(item => item.State == ComplianceControlState.Fail), controls.Count(item => item.State == ComplianceControlState.Partial), controls.Count(item => item.State == ComplianceControlState.NotAssessed))
        {
            SelectedStandards = standards,
            MappingCatalogVersion = ComplianceStandardCatalog.MappingCatalogVersion,
            Exceptions = exceptions
        };
    }

    private static IReadOnlyList<ComplianceControl> CiemBaseline(ComplianceContext context) => new[]
    {
        Control("CIEM-BASE-001", ComplianceFramework.CiemBaseline, "Read-only cloud posture", "Cloud accounts are configured for read-only discovery.", context.Accounts.Count > 0 && context.Accounts.All(account => account.IsReadOnly), context.Accounts.Select(account => $"{account.Provider}:{account.Name}: ReadOnly={account.IsReadOnly}"), "One or more accounts are not marked read-only.", context.Accounts.Select(account => Reference("CloudAccount", account.Id, account.Id, $"{account.Provider}:{account.Name}: ReadOnly={account.IsReadOnly}", LatestScanAt(context, account.Id)))),
        Control("CIEM-BASE-002", ComplianceFramework.CiemBaseline, "Evidence-backed access", "Effective access records have evidence or evaluation details.", context.EffectiveAccess.Count > 0 && context.EffectiveAccess.All(access => !string.IsNullOrWhiteSpace(access.Evidence) || !string.IsNullOrWhiteSpace(access.EvaluationDetails)), context.EffectiveAccess.Select(access => $"EffectiveAccess={access.Id}; Decision={access.Decision}"), "Effective access evidence is incomplete.", context.EffectiveAccess.Select(access => Reference("EffectiveAccess", access.Id, access.CloudAccountId, $"EffectiveAccess={access.Id}; Decision={access.Decision}", LatestScanAt(context, access.CloudAccountId)))),
        Control("CIEM-BASE-003", ComplianceFramework.CiemBaseline, "Open high-risk findings reviewed", "High and critical findings are visible for governance review.", context.Findings.Count(finding => finding.Severity is RiskSeverity.High or RiskSeverity.Critical) == 0, context.Findings.Where(finding => finding.Severity is RiskSeverity.High or RiskSeverity.Critical).Select(finding => $"Finding={finding.Id}; Status={finding.Status}"), "High or critical findings require review.", context.Findings.Where(finding => finding.Severity is RiskSeverity.High or RiskSeverity.Critical).Select(finding => Reference("RiskFinding", finding.Id, finding.CloudAccountId, $"Finding={finding.Id}; Status={finding.Status}", LatestScanAt(context, finding.CloudAccountId))))
    };

    private static IReadOnlyList<ComplianceControl> LeastPrivilege(ComplianceContext context) => new[]
    {
        Control("LP-001", ComplianceFramework.LeastPrivilege, "Unknown access is not treated as allowed", "Unknown or unsupported effective access remains explicitly classified.", context.EffectiveAccess.All(access => access.Decision != AccessDecision.Allow || access.EvaluationStatus == EvaluationStatus.FullyEvaluated), context.EffectiveAccess.Where(access => access.Decision == AccessDecision.Allow).Select(access => $"Access={access.Id}; Status={access.EvaluationStatus}"), "Allowed access has incomplete evaluation status.", context.EffectiveAccess.Where(access => access.Decision == AccessDecision.Allow).Select(access => Reference("EffectiveAccess", access.Id, access.CloudAccountId, $"Access={access.Id}; Status={access.EvaluationStatus}", LatestScanAt(context, access.CloudAccountId)))),
        Control("LP-002", ComplianceFramework.LeastPrivilege, "Privileged access has a path", "Privileged effective access has an explainable access path.", context.EffectiveAccess.Where(access => access.IsPrivileged).All(access => context.AccessPaths.Any(path => path.EffectiveAccessId == access.Id)), context.EffectiveAccess.Where(access => access.IsPrivileged).Select(access => $"EffectiveAccess={access.Id}"), "Privileged access is missing an access path.", context.EffectiveAccess.Where(access => access.IsPrivileged).Select(access => Reference("EffectiveAccess", access.Id, access.CloudAccountId, $"EffectiveAccess={access.Id}", LatestScanAt(context, access.CloudAccountId))).Concat(context.AccessPaths.Where(path => context.EffectiveAccess.Any(access => access.Id == path.EffectiveAccessId && access.IsPrivileged)).Select(path => Reference("AccessPath", path.Id, path.CloudAccountId, $"AccessPath={path.Id}; EffectiveAccess={path.EffectiveAccessId}", LatestScanAt(context, path.CloudAccountId)))))
    };

    private static IReadOnlyList<ComplianceControl> IdentityGovernance(ComplianceContext context) => new[]
    {
        Control("IG-001", ComplianceFramework.IdentityGovernance, "Identity activity is known or disclosed", "Identities without activity data are explicitly marked unknown.", context.GovernanceRecords.All(record => record.LastActivityAt.HasValue || record.Diagnostics.Count > 0), context.GovernanceRecords.Select(record => $"Principal={record.PrincipalId}; State={record.LifecycleState}"), "Identity activity state is not evidenced.", context.GovernanceRecords.Select(record => Reference("IdentityGovernance", record.PrincipalId, record.CloudAccountId, $"Principal={record.PrincipalId}; State={record.LifecycleState}", record.LastActivityAt ?? LatestScanAt(context, record.CloudAccountId)))),
        Control("IG-002", ComplianceFramework.IdentityGovernance, "External privileged identities are visible", "External privileged identities are surfaced for review.", context.GovernanceRecords.Any(record => record.IsExternal && record.IsPrivileged) || !context.GovernanceRecords.Any(record => record.IsExternal && record.IsPrivileged), context.GovernanceRecords.Where(record => record.IsExternal && record.IsPrivileged).Select(record => $"Principal={record.PrincipalId}"), "External privileged identity evidence requires review.", context.GovernanceRecords.Where(record => record.IsExternal && record.IsPrivileged).Select(record => Reference("IdentityGovernance", record.PrincipalId, record.CloudAccountId, $"Principal={record.PrincipalId}", record.LastActivityAt ?? LatestScanAt(context, record.CloudAccountId))))
    };

    private static IReadOnlyList<ComplianceControl> AccessReview(ComplianceContext context) => new[]
    {
        Control("AR-001", ComplianceFramework.AccessReview, "Privileged access is reviewable", "Every privileged identity has effective-access evidence and a path where available.", context.Principals.Where(principal => principal.IsPrivileged).All(principal => context.EffectiveAccess.Any(access => access.PrincipalId == principal.Id)), context.Principals.Where(principal => principal.IsPrivileged).Select(principal => $"Principal={principal.Id}"), "Privileged identity lacks effective-access evidence.", context.Principals.Where(principal => principal.IsPrivileged).Select(principal => Reference("CloudPrincipal", principal.Id, principal.AccountId, $"Principal={principal.Id}", LatestScanAt(context, principal.AccountId))).Concat(context.EffectiveAccess.Where(access => access.IsPrivileged).Select(access => Reference("EffectiveAccess", access.Id, access.CloudAccountId, $"EffectiveAccess={access.Id}; Principal={access.PrincipalId}", LatestScanAt(context, access.CloudAccountId))))),
        Control("AR-002", ComplianceFramework.AccessReview, "Audit events are available", "Governance activity can be traced through audit records.", context.AuditEvents.Count > 0, context.AuditEvents.Select(audit => $"{audit.EventType}@{audit.Timestamp:O}"), "No audit events are available for this assessment.", context.AuditEvents.Select(audit => Reference("AuditEvent", audit.Id, null, $"{audit.EventType}@{audit.Timestamp:O}", audit.Timestamp)))
    };

    private static ComplianceControl Control(string id, ComplianceFramework framework, string title, string description, bool pass, IEnumerable<string> evidence, string gap, IEnumerable<ComplianceEvidenceReference> lineage)
    {
        var evidenceList = evidence.ToArray();
        var state = evidenceList.Length == 0 ? ComplianceControlState.NotAssessed : pass ? ComplianceControlState.Pass : ComplianceControlState.Fail;
        return new ComplianceControl(id, framework, title, description, state, evidenceList.Length, evidenceList, pass ? Array.Empty<string>() : new[] { gap })
        {
            EvidenceLineage = lineage.ToArray()
        };
    }

    private static ComplianceEvidenceReference Reference(string sourceType, Guid sourceId, Guid? accountId, string evidence, DateTime? collectedAtUtc)
        => new($"{sourceType}:{sourceId:D}", sourceType, sourceId.ToString("D"), accountId, collectedAtUtc,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidence))).ToLowerInvariant());

    private static DateTime? LatestScanAt(ComplianceContext context, Guid accountId)
        => context.Scans.Where(scan => scan.CloudAccountId == accountId && scan.CompletedAt.HasValue)
            .MaxBy(scan => scan.CompletedAt)?.CompletedAt;
}
