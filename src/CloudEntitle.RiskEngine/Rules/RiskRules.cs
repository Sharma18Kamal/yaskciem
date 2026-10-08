using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Domain.ValueObjects;
using CloudEntitle.RiskEngine.Abstractions;
using CloudEntitle.RiskEngine.Models;
using CloudEntitle.Normalization.Models;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;
using DomainAccessPath = CloudEntitle.Domain.Entities.AccessPath;

namespace CloudEntitle.RiskEngine.Rules;

public abstract class RiskRuleBase : IRiskRule
{
    public abstract string RuleId { get; }
    public abstract string Name { get; }
    public abstract RiskSeverity DefaultSeverity { get; }
    public abstract RiskCategory Category { get; }
    public abstract Task<IReadOnlyCollection<RiskFindingModel>> EvaluateAsync(RiskRuleContext context, CancellationToken cancellationToken = default);

    protected static IEnumerable<DomainEffectiveAccess> Allowed(RiskRuleContext context) => context.Request.EffectiveAccess.Where(access => access.Decision == AccessDecision.Allow || access.Decision == AccessDecision.Conditional);
    protected static CloudPrincipal? Principal(RiskRuleContext context, Guid id) => context.Request.Principals.FirstOrDefault(item => item.Id == id);
    protected static CloudPermission? Permission(RiskRuleContext context, Guid id) => context.Request.Permissions.FirstOrDefault(item => item.Id == id);
    protected static CloudResource? Resource(RiskRuleContext context, Guid? id) => id.HasValue ? context.Request.Resources.FirstOrDefault(item => item.Id == id) : null;
    protected static NormalizedEntitlement? Entitlement(RiskRuleContext context, DomainEffectiveAccess access) => context.Request.Entitlements.FirstOrDefault(item => item.PrincipalId == access.PrincipalId && item.PermissionId == access.PermissionId);
    protected RiskFindingModel Finding(RiskRuleContext context, DomainEffectiveAccess access, RiskEvaluationState state, string title, string description, string evidence, Guid? pathId = null, int priorityScore = 0, RiskSeverity? severity = null, IReadOnlyDictionary<string, string>? evidenceDetails = null)
    {
        var principal = Principal(context, access.PrincipalId);
        var resource = Resource(context, access.ResourceId);
        var permission = Permission(context, access.PermissionId);
        var provider = access.Provider;
        var now = context.EvaluatedAt;
        var fingerprint = string.Join('|', RuleId, provider, access.CloudAccountId, access.PrincipalId, access.ResourceId, access.PermissionId, pathId, evidence);
        var ruleGuid = Stable(RuleId);
        return new RiskFindingModel
        {
            Id = Stable(fingerprint), RuleId = ruleGuid, CloudProvider = provider, CloudAccountId = access.CloudAccountId, PrincipalId = access.PrincipalId, ResourceId = access.ResourceId, EffectiveAccessId = access.Id, AccessPathId = pathId, PermissionId = access.PermissionId, Severity = severity ?? DefaultSeverity, Status = FindingStatus.Open, Category = Category, EvaluationState = state, PriorityScore = priorityScore, RuleVersion = "1.0", Fingerprint = fingerprint, Title = title, Description = description, Evidence = new RiskEvidence($"Principal={principal?.Name ?? "unknown"}; Resource={resource?.Name ?? "unknown"}; Permission={permission?.Name ?? "unknown"}; {evidence}", evidenceDetails), CreatedAt = now, UpdatedAt = now, DetectedAt = now, LastEvaluatedAt = now
        };
    }

    protected static Guid Stable(string value) => GuidUtility.Create(GuidUtility.Namespace, value);
    protected static CloudProvider Provider(CloudProviderType provider) => provider switch
    {
        CloudProviderType.Azure => CloudProvider.Azure,
        CloudProviderType.Aws => CloudProvider.Aws,
        CloudProviderType.Gcp => CloudProvider.Gcp,
        _ => CloudProvider.Kubernetes
    };
}

public sealed class PrivilegedIdentityRule : RiskRuleBase { public override string RuleId => "CIEM-001"; public override string Name => "Privileged Identity"; public override RiskSeverity DefaultSeverity => RiskSeverity.High; public override RiskCategory Category => RiskCategory.Privilege; public override Task<IReadOnlyCollection<RiskFindingModel>> EvaluateAsync(RiskRuleContext c, CancellationToken t = default) => Task.FromResult<IReadOnlyCollection<RiskFindingModel>>(Allowed(c).Where(a => a.IsPrivileged).Select(a => Finding(c, a, RiskEvaluationState.Confirmed, Name, "The principal has privileged effective access.", "EffectiveAccess.IsPrivileged=true")).ToArray()); }
public sealed class ExcessivePermissionRule : RiskRuleBase
{
    public override string RuleId => "CIEM-002";
    public override string Name => "Excessive Permission";
    public override RiskSeverity DefaultSeverity => RiskSeverity.Medium;
    public override RiskCategory Category => RiskCategory.ExcessiveAccess;

    public override Task<IReadOnlyCollection<RiskFindingModel>> EvaluateAsync(RiskRuleContext context, CancellationToken cancellationToken = default)
    {
        var findings = new List<RiskFindingModel>();
        var windowEnd = context.EvaluatedAt.ToUniversalTime();
        var windowStart = windowEnd.AddDays(-Math.Max(1, context.Options.UsageObservationDays));
        foreach (var access in context.Request.EffectiveAccess.Where(item => item.Decision == AccessDecision.Allow))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var action = Permission(context, access.PermissionId)?.Action;
            if (action is null || (!action.Contains("write", StringComparison.OrdinalIgnoreCase) && !action.Contains("delete", StringComparison.OrdinalIgnoreCase))) continue;
            var coverage = context.Request.ActivityCoverage?.FirstOrDefault(item => item.CloudAccountId == access.CloudAccountId && item.Provider == access.Provider);
            var complete = ActivityUsageEvaluator.Covers(coverage, windowStart, windowEnd, requiresActions: true, requiresResourceIds: access.ResourceId.HasValue, requiresRoleIds: access.RoleId.HasValue);
            var matchingMutation = context.Request.ActivityRecords.Any(activity =>
                activity.CloudAccountId == access.CloudAccountId
                && activity.PrincipalId == access.PrincipalId
                && (!access.RoleId.HasValue || activity.RoleId == access.RoleId)
                && activity.Success
                && activity.Timestamp.ToUniversalTime() >= windowStart
                && activity.Timestamp.ToUniversalTime() <= windowEnd
                && (!access.ResourceId.HasValue || activity.ResourceId == access.ResourceId)
                && activity.Action.Contains("write", StringComparison.OrdinalIgnoreCase)
                && ActivityUsageEvaluator.ActionMatches(access.Provider, action, activity.Action));
            if (matchingMutation) continue;

            var state = complete ? RiskEvaluationState.Potential
                : coverage?.Status == ActivityCoverageStatus.Unsupported ? RiskEvaluationState.Unsupported : RiskEvaluationState.Unknown;
            var title = complete ? Name : "Mutation Activity Unavailable";
            var description = complete
                ? "The granted write/delete permission was not observed during a complete activity window."
                : "Activity coverage is insufficient to assess use of this write/delete permission.";
            var evidence = $"Action={action}; ObservationWindow={windowStart:O}..{windowEnd:O}; Coverage={coverage?.Status.ToString() ?? "Unavailable"}; Details={coverage?.Details ?? "No activity coverage."}";
            findings.Add(Finding(context, access, state, title, description, evidence));
        }

        return Task.FromResult<IReadOnlyCollection<RiskFindingModel>>(findings);
    }
}
public sealed class WildcardPermissionRule : RiskRuleBase { public override string RuleId => "CIEM-003"; public override string Name => "Wildcard Permission"; public override RiskSeverity DefaultSeverity => RiskSeverity.High; public override RiskCategory Category => RiskCategory.ExcessiveAccess; public override Task<IReadOnlyCollection<RiskFindingModel>> EvaluateAsync(RiskRuleContext c, CancellationToken t = default) => Task.FromResult<IReadOnlyCollection<RiskFindingModel>>(Allowed(c).Where(a => { var p = Permission(c, a.PermissionId); return p?.Action == "*" || p?.Action?.EndsWith(":*", StringComparison.Ordinal) == true; }).Select(a => Finding(c, a, RiskEvaluationState.Confirmed, Name, "The effective permission contains a wildcard action.", $"Action={Permission(c, a.PermissionId)?.Action}")).ToArray()); }
public sealed class BroadScopeRule : RiskRuleBase { public override string RuleId => "CIEM-004"; public override string Name => "Broad Scope"; public override RiskSeverity DefaultSeverity => RiskSeverity.Medium; public override RiskCategory Category => RiskCategory.Scope; public override Task<IReadOnlyCollection<RiskFindingModel>> EvaluateAsync(RiskRuleContext c, CancellationToken t = default) => Task.FromResult<IReadOnlyCollection<RiskFindingModel>>(Allowed(c).Where(a => Entitlement(c, a)?.Scope.Type is NormalizedScopeType.Organization or NormalizedScopeType.ManagementGroup or NormalizedScopeType.Account or NormalizedScopeType.Subscription or NormalizedScopeType.Folder or NormalizedScopeType.Project).Select(a => Finding(c, a, RiskEvaluationState.Potential, Name, "Access is granted at a broad hierarchy scope.", $"Scope={Entitlement(c, a)?.Scope.OriginalScope}")).ToArray()); }
public sealed class DormantPrivilegedIdentityRule : RiskRuleBase { public override string RuleId => "CIEM-005"; public override string Name => "Dormant Privileged Identity"; public override RiskSeverity DefaultSeverity => RiskSeverity.High; public override RiskCategory Category => RiskCategory.Dormancy; public override Task<IReadOnlyCollection<RiskFindingModel>> EvaluateAsync(RiskRuleContext c, CancellationToken t = default) => Task.FromResult<IReadOnlyCollection<RiskFindingModel>>(Allowed(c).Where(a => a.IsPrivileged).Select(a => (a, p: Principal(c, a.PrincipalId))).Where(x => x.p != null && x.p.LastActivityAt == null || x.p?.LastActivityAt < c.EvaluatedAt.AddDays(-c.Options.DormancyDays)).Select(x => Finding(c, x.a, x.p!.LastActivityAt == null ? RiskEvaluationState.Unknown : RiskEvaluationState.Confirmed, Name, "The privileged identity has insufficient or stale activity evidence.", $"LastActivity={x.p!.LastActivityAt?.ToString("O") ?? "Unknown"}")).ToArray()); }
public sealed class ExternalPrivilegedIdentityRule : RiskRuleBase { public override string RuleId => "CIEM-006"; public override string Name => "External Privileged Identity"; public override RiskSeverity DefaultSeverity => RiskSeverity.High; public override RiskCategory Category => RiskCategory.ExternalAccess; public override Task<IReadOnlyCollection<RiskFindingModel>> EvaluateAsync(RiskRuleContext c, CancellationToken t = default) => Task.FromResult<IReadOnlyCollection<RiskFindingModel>>(Allowed(c).Where(a => a.IsPrivileged && Principal(c, a.PrincipalId)?.IsExternal == true).Select(a => Finding(c, a, RiskEvaluationState.Confirmed, Name, "An external identity has privileged effective access.", "Principal.IsExternal=true")).ToArray()); }
public sealed class PrivilegedServiceAccountRule : RiskRuleBase { public override string RuleId => "CIEM-007"; public override string Name => "Privileged Service Account"; public override RiskSeverity DefaultSeverity => RiskSeverity.High; public override RiskCategory Category => RiskCategory.ServiceIdentity; public override Task<IReadOnlyCollection<RiskFindingModel>> EvaluateAsync(RiskRuleContext c, CancellationToken t = default) => Task.FromResult<IReadOnlyCollection<RiskFindingModel>>(Allowed(c).Where(a => a.IsPrivileged && Principal(c, a.PrincipalId)?.IdentityType is IdentityType.ServiceAccount or IdentityType.ServicePrincipal or IdentityType.ManagedIdentity).Select(a => Finding(c, a, RiskEvaluationState.Confirmed, Name, "A non-human identity has privileged effective access.", $"IdentityType={Principal(c, a.PrincipalId)?.IdentityType}")).ToArray()); }
public sealed class UnusedEntitlementRule : RiskRuleBase
{
    public override string RuleId => "CIEM-008";
    public override string Name => "Unused Entitlement";
    public override RiskSeverity DefaultSeverity => RiskSeverity.Low;
    public override RiskCategory Category => RiskCategory.Dormancy;

    public override Task<IReadOnlyCollection<RiskFindingModel>> EvaluateAsync(RiskRuleContext context, CancellationToken cancellationToken = default)
    {
        var findings = new List<RiskFindingModel>();
        var windowEnd = context.EvaluatedAt.ToUniversalTime();
        var windowStart = windowEnd.AddDays(-Math.Max(1, context.Options.UsageObservationDays));
        foreach (var access in context.Request.EffectiveAccess.Where(item => item.Decision == AccessDecision.Allow))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var coverage = context.Request.ActivityCoverage?.FirstOrDefault(item => item.CloudAccountId == access.CloudAccountId && item.Provider == access.Provider);
            var permission = Permission(context, access.PermissionId);
            var hasResourceScopedAccess = access.ResourceId.HasValue;
            var fullyCovered = ActivityUsageEvaluator.Covers(coverage, windowStart, windowEnd, requiresActions: true, requiresResourceIds: hasResourceScopedAccess, requiresRoleIds: access.RoleId.HasValue);
            var matchingActions = context.Request.ActivityRecords.Where(activity =>
                activity.CloudAccountId == access.CloudAccountId
                && activity.PrincipalId == access.PrincipalId
                && activity.Success
                && activity.Timestamp.ToUniversalTime() >= windowStart
                && activity.Timestamp.ToUniversalTime() <= windowEnd
                && permission?.Action is string actionPattern
                && ActivityUsageEvaluator.ActionMatches(access.Provider, actionPattern, activity.Action)).ToArray();
            var matchingUse = matchingActions.Any(activity => (!access.RoleId.HasValue || activity.RoleId == access.RoleId) && (!hasResourceScopedAccess || activity.ResourceId == access.ResourceId));
            if (matchingUse) continue;

            var unattributedUse = (hasResourceScopedAccess && matchingActions.Any(activity => !activity.ResourceId.HasValue))
                || (access.RoleId.HasValue && matchingActions.Any(activity => !activity.RoleId.HasValue));
            var state = fullyCovered && !unattributedUse
                ? RiskEvaluationState.Confirmed
                : coverage?.Status == ActivityCoverageStatus.Unsupported
                    ? RiskEvaluationState.Unsupported
                    : RiskEvaluationState.Unknown;
            var coverageDetails = coverage is null
                ? "Activity coverage was not supplied."
                : $"Coverage={coverage.Status}; ObservedFrom={coverage.ObservedFromUtc:O}; ObservedThrough={coverage.ObservedThroughUtc:O}; Details={coverage.Details ?? "not provided"}";
            var evidence = $"ObservationWindow={windowStart:O}..{windowEnd:O}; MatchingSuccessfulActions={matchingActions.Length}; {coverageDetails}";
            if (unattributedUse) evidence += "; matching activity lacked resource attribution.";
            findings.Add(Finding(context, access, state, state == RiskEvaluationState.Confirmed ? Name : "Usage Evidence Unavailable",
                state == RiskEvaluationState.Confirmed
                    ? "No successful matching action was observed during a complete activity window."
                    : "Activity coverage is insufficient to determine whether this entitlement is unused.", evidence));
        }

        return Task.FromResult<IReadOnlyCollection<RiskFindingModel>>(findings);
    }
}

public sealed class UnusedRoleAssignmentRule : RiskRuleBase
{
    public override string RuleId => "CIEM-016";
    public override string Name => "Unused Role Assignment";
    public override RiskSeverity DefaultSeverity => RiskSeverity.Low;
    public override RiskCategory Category => RiskCategory.Dormancy;

    public override Task<IReadOnlyCollection<RiskFindingModel>> EvaluateAsync(RiskRuleContext context, CancellationToken cancellationToken = default)
    {
        var findings = new List<RiskFindingModel>();
        var windowEnd = context.EvaluatedAt.ToUniversalTime();
        var windowStart = windowEnd.AddDays(-Math.Max(1, context.Options.UsageObservationDays));
        foreach (var roleAccess in Allowed(context).Where(item => item.RoleId.HasValue).GroupBy(item => (item.CloudAccountId, item.Provider, item.RoleId)).Select(group => group.First()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var coverage = context.Request.ActivityCoverage?.FirstOrDefault(item => item.CloudAccountId == roleAccess.CloudAccountId && item.Provider == roleAccess.Provider);
            var roleUsage = context.Request.ActivityRecords.Any(activity => activity.CloudAccountId == roleAccess.CloudAccountId
                && activity.RoleId == roleAccess.RoleId
                && activity.Success
                && activity.Timestamp.ToUniversalTime() >= windowStart
                && activity.Timestamp.ToUniversalTime() <= windowEnd);
            if (roleUsage) continue;

            var complete = ActivityUsageEvaluator.Covers(coverage, windowStart, windowEnd, requiresRoleIds: true);
            var state = complete ? RiskEvaluationState.Confirmed
                : coverage?.Status == ActivityCoverageStatus.Unsupported ? RiskEvaluationState.Unsupported : RiskEvaluationState.Unknown;
            var evidence = $"RoleId={roleAccess.RoleId}; ObservationWindow={windowStart:O}..{windowEnd:O}; Coverage={coverage?.Status.ToString() ?? "Unavailable"}; Details={coverage?.Details ?? "No role-attributed activity coverage."}";
            findings.Add(Finding(context, roleAccess, state, state == RiskEvaluationState.Confirmed ? Name : "Role Activity Unavailable",
                state == RiskEvaluationState.Confirmed
                    ? "No successful use of this role was observed during a complete role-activity window."
                    : "Role-attributed activity coverage is insufficient to determine whether this role is unused.", evidence));
        }

        return Task.FromResult<IReadOnlyCollection<RiskFindingModel>>(findings);
    }
}

public sealed class ExploitableAttackPathRule : RiskRuleBase
{
    public override string RuleId => "CIEM-017";
    public override string Name => "Exploitable Identity-to-Workload Path";
    public override RiskSeverity DefaultSeverity => RiskSeverity.Medium;
    public override RiskCategory Category => RiskCategory.ExternalAccess;

    public override Task<IReadOnlyCollection<RiskFindingModel>> EvaluateAsync(RiskRuleContext context, CancellationToken cancellationToken = default)
    {
        var findings = new List<RiskFindingModel>();
        foreach (var access in Allowed(context).Where(item => item.ResourceId.HasValue))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var principal = Principal(context, access.PrincipalId);
            var resource = Resource(context, access.ResourceId);
            var path = context.Request.AccessPaths.FirstOrDefault(item => item.EffectiveAccessId == access.Id);
            if (principal is null || resource is null || path is null) continue;

            var signals = (context.Request.ResourceSecuritySignals ?? Array.Empty<ResourceSecuritySignal>())
                .Where(signal => signal.ResourceId == resource.Id).ToArray();
            var publicSignals = signals.Where(signal => signal.Type == ResourceSecuritySignalType.PublicExposure).ToArray();
            var sensitiveSignals = signals.Where(signal => signal.Type == ResourceSecuritySignalType.SensitiveData).ToArray();
            var vulnerabilities = signals.Where(signal => signal.Type == ResourceSecuritySignalType.WorkloadVulnerability).ToArray();
            var criticalSignals = signals.Where(signal => signal.Type == ResourceSecuritySignalType.CriticalAsset).ToArray();
            var publicTag = HasTagValue(resource, new[] { "PubliclyAccessible", "InternetFacing", "PublicAccess", "Exposure" }, new[] { "true", "yes", "public", "internet", "internet-facing" });
            var sensitiveTag = HasTagValue(resource, new[] { "Sensitivity", "DataClassification" }, new[] { "sensitive", "confidential", "restricted", "high", "critical", "tier0" });
            var criticalTag = HasTagValue(resource, new[] { "CriticalAsset", "BusinessCriticality", "Criticality", "AssetTier" }, new[] { "true", "yes", "critical", "tier0", "crown-jewel", "crown jewel" });
            var isNonHuman = principal.IdentityType is IdentityType.ServiceAccount or IdentityType.ServicePrincipal or IdentityType.ManagedIdentity or IdentityType.Application;
            var permissionAction = Permission(context, access.PermissionId)?.Action ?? string.Empty;
            var isDangerous = permissionAction.Contains("admin", StringComparison.OrdinalIgnoreCase)
                || permissionAction.Contains("write", StringComparison.OrdinalIgnoreCase)
                || permissionAction.Contains("delete", StringComparison.OrdinalIgnoreCase);
            var hasRiskyIdentity = access.IsPrivileged || principal.IsPrivileged || principal.IsExternal || isNonHuman || isDangerous;
            var publicWeight = Math.Max(SignalWeight(publicSignals, context), TagWeight(publicTag, resource.UpdatedAt, context));
            var sensitiveWeight = Math.Max(SignalWeight(sensitiveSignals, context), TagWeight(sensitiveTag, resource.UpdatedAt, context));
            var criticalWeight = Math.Max(SignalWeight(criticalSignals, context), TagWeight(criticalTag, resource.UpdatedAt, context));
            var vulnerabilityWeight = SignalWeight(vulnerabilities, context);
            var hasRiskyResource = publicWeight > 0 || sensitiveWeight > 0 || criticalWeight > 0 || vulnerabilityWeight > 0;
            if (!hasRiskyIdentity || !hasRiskyResource) continue;

            var vulnerabilityScore = vulnerabilities.Select(signal => (int)Math.Round(VulnerabilityPoints(signal) * SignalConfidence(signal, context))).DefaultIfEmpty(0).Max();
            var points = (access.IsPrivileged || principal.IsPrivileged ? 25 : 0)
                + (principal.IsExternal ? 20 : 0)
                + (isNonHuman ? 20 : 0)
                + (isDangerous ? 10 : 0)
                + (int)Math.Round(25 * publicWeight)
                + (int)Math.Round(20 * sensitiveWeight)
                + (int)Math.Round(25 * criticalWeight)
                + vulnerabilityScore
                + (path.PathStatus == AccessPathStatus.Complete ? 10 : path.PathStatus == AccessPathStatus.Partial ? 4 : 0);
            var coverageStatus = Enum.GetValues<ResourceSecuritySignalType>()
                .ToDictionary(type => type, type => CoverageFreshness(type, resource.Id, context));
            var signalFreshness = signals.Select(signal => $"{signal.Type}:{SignalFreshness(signal, context)}").ToArray();
            var identityFreshness = DateFreshness(principal.UpdatedAt, context);
            var accessFreshness = DateFreshness(access.CalculatedAt, context);
            var pathFreshness = DateFreshness(path.CreatedAt, context);
            var missingCoveragePenalty = coverageStatus.Values.Count(status => status != "Current") * 6;
            var staleSignalPenalty = signalFreshness.Count(status => status.EndsWith(":Stale", StringComparison.Ordinal)) * 5;
            var identityAccessFreshnessPenalty = new[] { identityFreshness, accessFreshness }.Sum(status => status switch { "Current" => 0, "Stale" => 8, _ => 4 });
            var pathFreshnessPenalty = pathFreshness == "Current" ? 0 : pathFreshness == "Stale" ? 8 : 4;
            var score = Math.Max(0, Math.Min(100, points) - missingCoveragePenalty - staleSignalPenalty - identityAccessFreshnessPenalty - pathFreshnessPenalty);
            if (score < 30) continue;

            var severity = score >= 80 ? RiskSeverity.Critical : score >= 60 ? RiskSeverity.High : score >= 40 ? RiskSeverity.Medium : RiskSeverity.Low;
            var allEvidenceCurrent = path.PathStatus == AccessPathStatus.Complete
                && pathFreshness == "Current"
                && identityFreshness == "Current"
                && accessFreshness == "Current"
                && signalFreshness.All(status => status.EndsWith(":Current", StringComparison.Ordinal))
                && coverageStatus.Values.All(status => status == "Current");
            var hasStaleEvidence = pathFreshness == "Stale"
                || identityFreshness == "Stale"
                || accessFreshness == "Stale"
                || signalFreshness.Any(status => status.EndsWith(":Stale", StringComparison.Ordinal))
                || coverageStatus.Values.Any(status => status == "Stale");
            var state = allEvidenceCurrent ? RiskEvaluationState.Confirmed
                : hasStaleEvidence || coverageStatus.Values.Any(status => status is "Partial" or "Unavailable")
                    ? RiskEvaluationState.Potential
                    : RiskEvaluationState.Unknown;
            var signalSummary = string.Join("; ", signals.Select(signal => $"{signal.Type}={signal.Name} from {signal.Source} ({SignalFreshness(signal, context)})"));
            var identitySummary = $"IdentityType={principal.IdentityType}; Privileged={access.IsPrivileged || principal.IsPrivileged}; External={principal.IsExternal}";
            var coverageSummary = string.Join(", ", coverageStatus.Select(item => $"{item.Key}={item.Value}"));
            var evidence = $"PriorityScore={score}; {identitySummary}; IdentityProfileFreshness={identityFreshness}; EffectiveAccessFreshness={accessFreshness}; PublicExposure={publicWeight > 0}; SensitiveAsset={sensitiveWeight > 0}; CriticalAsset={criticalWeight > 0}; VulnerabilityPoints={vulnerabilityScore}; PathStatus={path.PathStatus}; ModeledAccessPathFreshness={pathFreshness}; EvidenceCoverage={coverageSummary}; Signals={signalSummary}";
            var evidenceDetails = new Dictionary<string, string>
            {
                ["IdentityType"] = principal.IdentityType.ToString(),
                ["EffectiveAccessDecision"] = access.Decision.ToString(),
                ["EffectiveAccessEvaluation"] = access.EvaluationStatus.ToString(),
                ["IdentityProfileFreshness"] = identityFreshness,
                ["EffectiveAccessFreshness"] = accessFreshness,
                ["AccessPath"] = "Modeled from effective-access evidence; not observed attacker activity.",
                ["AccessPathStatus"] = path.PathStatus.ToString(),
                ["AccessPathFreshness"] = pathFreshness,
                ["EvidenceCoverage"] = coverageSummary,
                ["EvidenceFreshness"] = string.Join(", ", signalFreshness)
            };
            findings.Add(Finding(context, access, state, Name,
                "A risky identity can reach a critical, exposed, sensitive, or vulnerable resource.", evidence, path.Id, score, severity, evidenceDetails));
        }

        return Task.FromResult<IReadOnlyCollection<RiskFindingModel>>(findings);
    }

    private static int VulnerabilityPoints(ResourceSecuritySignal signal)
    {
        if (signal.IsExploitable) return 35;
        if (signal.CvssScore >= 9) return 30;
        if (signal.CvssScore >= 7) return 22;
        return signal.Severity switch { RiskSeverity.Critical => 28, RiskSeverity.High => 18, RiskSeverity.Medium => 10, _ => 4 };
    }

    private static double SignalWeight(IEnumerable<ResourceSecuritySignal> signals, RiskRuleContext context)
        => signals.Select(signal => SignalConfidence(signal, context)).DefaultIfEmpty(0).Max();

    private static double SignalConfidence(ResourceSecuritySignal signal, RiskRuleContext context)
    {
        var freshness = SignalFreshness(signal, context);
        var confidence = freshness switch { "Current" => 1.0, "Stale" => 0.45, _ => 0.65 };
        return signal.IsConfirmed ? confidence : confidence * 0.5;
    }

    private static double TagWeight(bool present, DateTime? observedAtUtc, RiskRuleContext context)
        => !present ? 0 : DateFreshness(observedAtUtc, context) switch
        {
            "Current" => 1.0,
            "Stale" => 0.45,
            _ => 0.65
        };

    private static string SignalFreshness(ResourceSecuritySignal signal, RiskRuleContext context)
    {
        if (!signal.IsConfirmed || signal.ObservedAtUtc is null) return "Unknown";
        return DateFreshness(signal.ObservedAtUtc.Value, context);
    }

    private static string CoverageFreshness(ResourceSecuritySignalType type, Guid resourceId, RiskRuleContext context)
    {
        var coverage = context.Request.ResourceSecurityCoverage?
            .Where(item => item.ResourceId == resourceId && item.SignalType == type)
            .OrderByDescending(item => item.ObservedThroughUtc)
            .FirstOrDefault();
        if (coverage is null) return "Unknown";
        if (coverage.Status != SecurityEvidenceCoverageStatus.Complete) return coverage.Status.ToString();
        return DateFreshness(coverage.ObservedThroughUtc, context);
    }

    private static string DateFreshness(DateTime? observedAtUtc, RiskRuleContext context)
    {
        if (observedAtUtc is null || observedAtUtc.Value == default || observedAtUtc.Value.ToUniversalTime() > context.EvaluatedAt.ToUniversalTime().AddMinutes(5)) return "Unknown";
        return context.EvaluatedAt.ToUniversalTime() - observedAtUtc.Value.ToUniversalTime() <= TimeSpan.FromDays(Math.Max(1, context.Options.EvidenceFreshnessWindowDays))
            ? "Current"
            : "Stale";
    }

    private static bool HasTagValue(CloudResource resource, IReadOnlyList<string> keys, IReadOnlyList<string> acceptedValues)
        => resource.Tags.Any(tag => keys.Contains(tag.Key, StringComparer.OrdinalIgnoreCase)
            && acceptedValues.Contains(tag.Value.Trim(), StringComparer.OrdinalIgnoreCase));
}
public sealed class ProductionAdministrativeAccessRule : RiskRuleBase { public override string RuleId => "CIEM-009"; public override string Name => "Production Administrative Access"; public override RiskSeverity DefaultSeverity => RiskSeverity.High; public override RiskCategory Category => RiskCategory.Scope; public override Task<IReadOnlyCollection<RiskFindingModel>> EvaluateAsync(RiskRuleContext c, CancellationToken t = default) => Task.FromResult<IReadOnlyCollection<RiskFindingModel>>(Allowed(c).Where(a => a.IsPrivileged && Resource(c, a.ResourceId)?.Tags.TryGetValue("Environment", out var environment) == true && string.Equals(environment, "Production", StringComparison.OrdinalIgnoreCase)).Select(a => Finding(c, a, RiskEvaluationState.Confirmed, Name, "Privileged effective access reaches a production resource.", "Resource tag Environment=Production")).ToArray()); }
public sealed class ExcessiveServicePermissionRule : RiskRuleBase { public override string RuleId => "CIEM-010"; public override string Name => "Potentially Excessive Service Permissions"; public override RiskSeverity DefaultSeverity => RiskSeverity.Medium; public override RiskCategory Category => RiskCategory.ServiceIdentity; public override Task<IReadOnlyCollection<RiskFindingModel>> EvaluateAsync(RiskRuleContext c, CancellationToken t = default) => Task.FromResult<IReadOnlyCollection<RiskFindingModel>>(Allowed(c).Where(a => Principal(c, a.PrincipalId)?.IdentityType is IdentityType.ServiceAccount or IdentityType.ServicePrincipal or IdentityType.ManagedIdentity).Where(a => Permission(c, a.PermissionId)?.Action?.Contains("write", StringComparison.OrdinalIgnoreCase) == true || Permission(c, a.PermissionId)?.Action?.Contains("delete", StringComparison.OrdinalIgnoreCase) == true).Select(a => Finding(c, a, RiskEvaluationState.Potential, Name, "A service identity has write/delete capability requiring review.", "Service permission breadth exceeds read-only operations evidence.")).ToArray()); }
public sealed class RedundantAccessPathRule : RiskRuleBase { public override string RuleId => "CIEM-011"; public override string Name => "Redundant Access Paths"; public override RiskSeverity DefaultSeverity => RiskSeverity.Low; public override RiskCategory Category => RiskCategory.AccessPathComplexity; public override Task<IReadOnlyCollection<RiskFindingModel>> EvaluateAsync(RiskRuleContext c, CancellationToken t = default) => Task.FromResult<IReadOnlyCollection<RiskFindingModel>>(c.Request.AccessPaths.GroupBy(a => a.EffectiveAccessId).Where(g => g.Key.HasValue && g.Count() > 1).SelectMany(g => g.Take(1).Select(path => { var access = c.Request.EffectiveAccess.FirstOrDefault(a => a.Id == g.Key); return access == null ? null : Finding(c, access, RiskEvaluationState.Confirmed, Name, "Multiple independent access paths provide the same effective access.", $"PathCount={g.Count()}", path.Id); })).Where(x => x != null)!.ToArray()!); }
public sealed class PrivilegedGroupMembershipRule : RiskRuleBase { public override string RuleId => "CIEM-012"; public override string Name => "Privileged Group Membership"; public override RiskSeverity DefaultSeverity => RiskSeverity.Medium; public override RiskCategory Category => RiskCategory.Privilege; public override Task<IReadOnlyCollection<RiskFindingModel>> EvaluateAsync(RiskRuleContext c, CancellationToken t = default) => Task.FromResult<IReadOnlyCollection<RiskFindingModel>>(Allowed(c).Where(a => a.IsPrivileged && a.AssignmentType == AssignmentType.Group).Select(a => Finding(c, a, RiskEvaluationState.Potential, Name, "Privileged access is obtained through group membership.", "AssignmentType=Group")).ToArray()); }
public sealed class NestedPrivilegedAccessRule : RiskRuleBase { public override string RuleId => "CIEM-013"; public override string Name => "Nested Privileged Access"; public override RiskSeverity DefaultSeverity => RiskSeverity.Medium; public override RiskCategory Category => RiskCategory.AccessPathComplexity; public override Task<IReadOnlyCollection<RiskFindingModel>> EvaluateAsync(RiskRuleContext c, CancellationToken t = default) => Task.FromResult<IReadOnlyCollection<RiskFindingModel>>(c.Request.AccessPaths.Where(path => path.Nodes.Count(node => node.NodeType == "NestedGroup") > 0).Select(path => c.Request.EffectiveAccess.FirstOrDefault(a => a.Id == path.EffectiveAccessId)).Where(a => a != null && a.IsPrivileged).Select(a => Finding(c, a!, RiskEvaluationState.Potential, Name, "Privileged access depends on nested group relationships.", "NestedGroup node present")).ToArray()); }
public sealed class BroadResourceWildcardRule : RiskRuleBase { public override string RuleId => "CIEM-014"; public override string Name => "Broad Resource Wildcard"; public override RiskSeverity DefaultSeverity => RiskSeverity.High; public override RiskCategory Category => RiskCategory.Scope; public override Task<IReadOnlyCollection<RiskFindingModel>> EvaluateAsync(RiskRuleContext c, CancellationToken t = default) => Task.FromResult<IReadOnlyCollection<RiskFindingModel>>(Allowed(c).Where(a => Permission(c, a.PermissionId)?.Resource is "*" or "global" || Entitlement(c, a)?.ResourcePattern is "*" or "global").Select(a => Finding(c, a, RiskEvaluationState.Confirmed, Name, "The effective permission applies to a wildcard resource scope.", $"ResourcePattern={Permission(c, a.PermissionId)?.Resource ?? Entitlement(c, a)?.ResourcePattern}")).ToArray()); }
public sealed class LongPrivilegeChainRule : RiskRuleBase { public override string RuleId => "CIEM-015"; public override string Name => "Long Privilege Chain"; public override RiskSeverity DefaultSeverity => RiskSeverity.Informational; public override RiskCategory Category => RiskCategory.AccessPathComplexity; public override Task<IReadOnlyCollection<RiskFindingModel>> EvaluateAsync(RiskRuleContext c, CancellationToken t = default) => Task.FromResult<IReadOnlyCollection<RiskFindingModel>>(c.Request.AccessPaths.Where(path => path.Nodes.Count > c.Options.MaxPathDepth).Select(path => c.Request.EffectiveAccess.FirstOrDefault(a => a.Id == path.EffectiveAccessId)).Where(a => a != null).Select(a => Finding(c, a!, RiskEvaluationState.Confirmed, Name, "The effective access path exceeds the configured complexity threshold.", $"NodeCount={c.Request.AccessPaths.First(path => path.EffectiveAccessId == a!.Id).Nodes.Count}")).ToArray()); }

internal static class GuidUtility { public static readonly Guid Namespace = Guid.Parse("7c31cbf1-3b9f-4cc2-b9d2-0f3f0f02c6af"); public static Guid Create(Guid ns, string name) { var hash = System.Security.Cryptography.SHA1.HashData(ns.ToByteArray().Concat(System.Text.Encoding.UTF8.GetBytes(name)).ToArray()); var bytes = hash.Take(16).ToArray(); bytes[6] = (byte)((bytes[6] & 15) | 80); bytes[8] = (byte)((bytes[8] & 63) | 128); return new Guid(bytes); } }
