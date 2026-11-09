using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.RiskEngine.Models;

namespace CloudEntitle.RiskEngine.Rules;

public sealed class PermissionUsageRiskRule : RiskRuleBase
{
    public override string RuleId => "CIEM-USAGE-001";
    public override string Name => "Privileged Permission Not Observed";
    public override RiskSeverity DefaultSeverity => RiskSeverity.Medium;
    public override RiskCategory Category => RiskCategory.ExcessiveAccess;

    public override Task<IReadOnlyCollection<RiskFindingModel>> EvaluateAsync(RiskRuleContext context,
        CancellationToken cancellationToken = default)
    {
        var findings = new List<RiskFindingModel>();
        var windowEnd = context.EvaluatedAt.ToUniversalTime();
        var days = Math.Clamp(context.Options.UsageObservationDays, 30, 90);
        var windowStart = windowEnd.AddDays(-days);
        foreach (var access in Allowed(context))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var permission = Permission(context, access.PermissionId);
            var principal = Principal(context, access.PrincipalId);
            var role = access.RoleId is { } roleId ? context.Request.Roles.FirstOrDefault(item => item.Id == roleId) : null;
            if (permission is null || principal is null || !(access.IsPrivileged || principal.IsPrivileged || role?.IsPrivileged == true))
                continue;
            var action = permission.Action;
            if (string.IsNullOrWhiteSpace(action)) continue;
            var coverage = context.Request.ActivityCoverage?.FirstOrDefault(item =>
                item.CloudAccountId == access.CloudAccountId && item.Provider == access.Provider);
            if (!ActivityUsageEvaluator.Covers(coverage, windowStart, windowEnd, requiresActions: true,
                requiresResourceIds: access.ResourceId.HasValue))
                continue;
            var observed = context.Request.ActivityRecords.Any(activity => activity.CloudAccountId == access.CloudAccountId
                && activity.PrincipalId == access.PrincipalId && activity.Success
                && activity.PermissionMappingStatus == PermissionActionMappingStatus.ExactProviderEvidence
                && activity.Timestamp.ToUniversalTime() >= windowStart && activity.Timestamp.ToUniversalTime() <= windowEnd
                && (!access.ResourceId.HasValue || activity.ResourceId == access.ResourceId)
                && ActivityUsageEvaluator.ActionMatches(access.Provider, action, activity.PermissionAction ?? activity.Action));
            if (observed) continue;

            var evidence = $"Permission={action}; Identity={principal.Name} ({principal.Id:D}); Resource={Resource(context, access.ResourceId)?.Name ?? "account scope"} ({access.ResourceId?.ToString("D") ?? "unscoped"}); Window={windowStart:O}..{windowEnd:O}; Coverage={coverage!.Status}; Source={coverage.Details}; Attribution=exact provider permission mapping required.";
            var finding = Finding(context, access, RiskEvaluationState.Potential, Name,
                "A privileged permission was not observed during a complete action-observation window. This is a review signal, not proof the permission is unnecessary.",
                evidence, priorityScore: 60, severity: RiskSeverity.Medium,
                evidenceDetails: new Dictionary<string, string>
                {
                    ["confidence"] = UsageConfidenceLevel.Medium.ToString(),
                    ["observationWindowDays"] = days.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["collectionStatus"] = coverage.Status.ToString(),
                    ["automaticRevocation"] = "Disabled"
                });
            var stableFingerprint = string.Join('|', RuleId, access.Provider, access.CloudAccountId,
                access.PrincipalId, access.ResourceId, access.PermissionId, days);
            finding.Fingerprint = stableFingerprint;
            finding.Id = Stable(stableFingerprint);
            findings.Add(finding);
        }
        return Task.FromResult<IReadOnlyCollection<RiskFindingModel>>(findings);
    }
}
