using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.RiskEngine.Models;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;
using System.Text.Json;

namespace CloudEntitle.RiskEngine.Rules;

public sealed class ToxicPermissionCombinationRule : RiskRuleBase
{
    public override string RuleId => "CIEM-021";
    public override string Name => "Toxic Permission Combination";
    public override RiskSeverity DefaultSeverity => RiskSeverity.High;
    public override RiskCategory Category => RiskCategory.ExcessiveAccess;

    public override Task<IReadOnlyCollection<RiskFindingModel>> EvaluateAsync(
        RiskRuleContext context,
        CancellationToken cancellationToken = default)
    {
        var permissionById = context.Request.Permissions.ToDictionary(item => item.Id);
        var rules = context.Request.ToxicCombinationRules ?? ToxicCombinationRuleDefaults.Create();
        var conditionEvidence = (context.Request.PermissionConditions ?? Array.Empty<PermissionConditionEvidence>())
            .GroupBy(item => item.EffectiveAccessId).ToDictionary(group => group.Key, group => group.Last());
        var conditionAssessments = context.Request.EffectiveAccess.ToDictionary(item => item.Id, item =>
            conditionEvidence.TryGetValue(item.Id, out var evidence)
                ? AuthorizationConditionEvaluator.Evaluate(evidence)
                : item.IsConditional || item.Decision == AccessDecision.Conditional
                    ? new AuthorizationConditionAssessment(AuthorizationConditionState.Unknown,
                        "Conditional access has no supported normalized condition evidence.", "Condition context unavailable.")
                    : new AuthorizationConditionAssessment(AuthorizationConditionState.Satisfied,
                        "No condition is attached to this effective-access record.", "Normalized effective-access record."));
        var accesses = context.Request.EffectiveAccess
            .Where(IsCandidateAccess)
            .Where(item => conditionAssessments[item.Id].State != AuthorizationConditionState.NotSatisfied)
            .Where(item => permissionById.ContainsKey(item.PermissionId))
            .ToArray();
        var findings = new Dictionary<string, RiskFindingModel>(StringComparer.Ordinal);

        foreach (var rule in rules.Where(item => item.Enabled).OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (!Enum.IsDefined(rule.Severity) || !Enum.IsDefined(rule.ResourceRequirement)
                || rule.RiskScore is < 0 or > 100
                || string.IsNullOrWhiteSpace(rule.FirstPermissionPattern)
                || string.IsNullOrWhiteSpace(rule.SecondPermissionPattern))
                continue;

            if (rule.RequireTrustPath)
            {
                EvaluateTrustPaths(rule, accesses, permissionById, conditionAssessments, context, findings, cancellationToken);
                continue;
            }

            var candidates = accesses.Where(item => item.Provider == rule.Provider || !rule.Provider.HasValue).ToArray();
            for (var firstIndex = 0; firstIndex < candidates.Length; firstIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var first = candidates[firstIndex];
                for (var secondIndex = firstIndex + 1; secondIndex < candidates.Length; secondIndex++)
                {
                    var second = candidates[secondIndex];
                    if (first.Id == second.Id || first.PrincipalId != second.PrincipalId
                        || first.CloudAccountId != second.CloudAccountId || first.Provider != second.Provider)
                        continue;

                    var firstPermission = permissionById[first.PermissionId];
                    var secondPermission = permissionById[second.PermissionId];
                    if (!Matches(rule.FirstPermissionPattern, firstPermission.Action, first.Provider)
                        || !Matches(rule.SecondPermissionPattern, secondPermission.Action, second.Provider)
                        || rule.FirstRequiresPrivilegedAccess && !IsPrivileged(first, context, permissionById)
                        || rule.SecondRequiresPrivilegedAccess && !IsPrivileged(second, context, permissionById))
                    {
                        if (!Matches(rule.SecondPermissionPattern, firstPermission.Action, first.Provider)
                            || !Matches(rule.FirstPermissionPattern, secondPermission.Action, second.Provider)
                            || rule.SecondRequiresPrivilegedAccess && !IsPrivileged(first, context, permissionById)
                            || rule.FirstRequiresPrivilegedAccess && !IsPrivileged(second, context, permissionById))
                            continue;
                        (first, second) = (second, first);
                        (firstPermission, secondPermission) = (secondPermission, firstPermission);
                    }

                    var resourceAssessment = AssessResource(rule, first, second, firstPermission, secondPermission, context);
                    if (!resourceAssessment.Matches) continue;
                    var scopeAssessment = AssessScope(first, second, context, rule.RequireSameResource);
                    if (scopeAssessment == ScopeAssessment.Disjoint
                        || rule.RequireSameResource && scopeAssessment != ScopeAssessment.Overlap)
                        continue;

                    var uncertain = IsConditionUnresolved(first, conditionAssessments[first.Id])
                        || IsConditionUnresolved(second, conditionAssessments[second.Id])
                        || first.Decision is not (AccessDecision.Allow or AccessDecision.Conditional)
                        || second.Decision is not (AccessDecision.Allow or AccessDecision.Conditional)
                        || first.EvaluationStatus != EvaluationStatus.FullyEvaluated
                        || second.EvaluationStatus != EvaluationStatus.FullyEvaluated
                        || scopeAssessment == ScopeAssessment.Unknown
                        || resourceAssessment.Confidence == "Low";
                    var confidence = uncertain ? "Low"
                        : first.IsInherited || second.IsInherited ? "Medium" : resourceAssessment.Confidence;
                    var riskScore = scopeAssessment == ScopeAssessment.Unknown
                        ? Math.Min(rule.RiskScore, 45)
                        : uncertain ? Math.Min(rule.RiskScore, 65) : rule.RiskScore;
                    var severity = scopeAssessment == ScopeAssessment.Unknown
                        ? LowerSeverity(rule.Severity, RiskSeverity.Medium)
                        : rule.Severity;
                    var resourceId = resourceAssessment.Resource?.Id
                        ?? (first.ResourceId == second.ResourceId ? first.ResourceId : first.ResourceId ?? second.ResourceId);
                    var evidenceText = $"Rule={rule.Key}; Provider={first.Provider}; Account={first.CloudAccountId:D}; " +
                        $"Principal={first.PrincipalId:D}; FirstAccess={first.Id:D} ({firstPermission.Action}); " +
                        $"SecondAccess={second.Id:D} ({secondPermission.Action}); Scope={scopeAssessment}; " +
                        $"ResourceEvidence={resourceAssessment.Evidence}; ProviderSemantics={ProviderSemantics(first.Provider)}";
                    var finding = Finding(context, first, RiskEvaluationState.Potential, rule.Name, rule.Description,
                        evidenceText, context.Request.AccessPaths.FirstOrDefault(path => path.EffectiveAccessId == first.Id)?.Id,
                        riskScore, severity, new Dictionary<string, string>
                        {
                            ["RuleKey"] = rule.Key,
                            ["RuleEntityId"] = rule.Id.ToString("D"),
                            ["FirstEffectiveAccessId"] = first.Id.ToString("D"),
                            ["SecondEffectiveAccessId"] = second.Id.ToString("D"),
                            ["FirstPermissionId"] = firstPermission.Id.ToString("D"),
                            ["SecondPermissionId"] = secondPermission.Id.ToString("D"),
                            ["FirstAction"] = firstPermission.Action ?? string.Empty,
                            ["SecondAction"] = secondPermission.Action ?? string.Empty,
                            ["FirstScope"] = EffectiveScope(first, firstPermission),
                            ["SecondScope"] = EffectiveScope(second, secondPermission),
                            ["FirstAssignmentType"] = first.AssignmentType.ToString(),
                            ["SecondAssignmentType"] = second.AssignmentType.ToString(),
                            ["FirstIsDirect"] = first.IsDirect.ToString(),
                            ["SecondIsDirect"] = second.IsDirect.ToString(),
                            ["FirstIsInherited"] = first.IsInherited.ToString(),
                            ["SecondIsInherited"] = second.IsInherited.ToString(),
                            ["FirstIsConditional"] = first.IsConditional.ToString(),
                            ["SecondIsConditional"] = second.IsConditional.ToString(),
                            ["FirstEvaluationStatus"] = first.EvaluationStatus.ToString(),
                            ["SecondEvaluationStatus"] = second.EvaluationStatus.ToString(),
                            ["FirstConditionState"] = conditionAssessments[first.Id].State.ToString(),
                            ["SecondConditionState"] = conditionAssessments[second.Id].State.ToString(),
                            ["FirstConditionEvidence"] = conditionAssessments[first.Id].Summary + " " + conditionAssessments[first.Id].EvidenceSource,
                            ["SecondConditionEvidence"] = conditionAssessments[second.Id].Summary + " " + conditionAssessments[second.Id].EvidenceSource,
                            ["ResourceRelation"] = scopeAssessment.ToString(),
                            ["ResourceEvidence"] = resourceAssessment.Evidence,
                            ["Confidence"] = confidence,
                            ["RiskScore"] = riskScore.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["Recommendation"] = rule.Recommendation,
                            ["RequiresValidation"] = "true"
                        });
                    if (!findings.ContainsKey(finding.Fingerprint)) findings[finding.Fingerprint] = finding;
                }
            }
        }

        return Task.FromResult<IReadOnlyCollection<RiskFindingModel>>(findings.Values.ToArray());
    }

    private void EvaluateTrustPaths(ToxicCombinationRule rule, IReadOnlyList<DomainEffectiveAccess> candidates,
        IReadOnlyDictionary<Guid, CloudPermission> permissionById,
        IReadOnlyDictionary<Guid, AuthorizationConditionAssessment> conditionAssessments,
        RiskRuleContext context, IDictionary<string, RiskFindingModel> findings, CancellationToken cancellationToken)
    {
        var validRelationships = (context.Request.ToxicTrustRelationships ?? Array.Empty<ToxicTrustRelationshipEvidence>())
            .Where(item => IsTrustEvidenceValid(item, context.Request.Principals, context.Request.Accounts))
            .ToArray();
        if (validRelationships.Length == 0) return;

        foreach (var sourceAccess in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rule.Provider.HasValue && sourceAccess.Provider != rule.Provider.Value
                || !Matches(rule.FirstPermissionPattern, permissionById[sourceAccess.PermissionId].Action, sourceAccess.Provider)
                || rule.FirstRequiresPrivilegedAccess && !IsPrivileged(sourceAccess, context, permissionById))
                continue;

            foreach (var trustPath in BuildTrustPaths(sourceAccess, validRelationships))
            {
                var requiredSourcePattern = trustPath[0].RequiredSourcePermissionPattern;
                if (trustPath[0].RequiresSourcePermission
                    && (string.IsNullOrWhiteSpace(requiredSourcePattern)
                        || !Matches(requiredSourcePattern, permissionById[sourceAccess.PermissionId].Action, sourceAccess.Provider)))
                    continue;
                var last = trustPath[^1];
                foreach (var targetAccess in candidates.Where(item => item.PrincipalId == last.TargetPrincipalId
                    && item.CloudAccountId == last.TargetCloudAccountId && item.Provider == last.TargetProvider))
                {
                    if (!Matches(rule.SecondPermissionPattern, permissionById[targetAccess.PermissionId].Action, targetAccess.Provider)
                        || rule.SecondRequiresPrivilegedAccess && !IsPrivileged(targetAccess, context, permissionById))
                        continue;

                    var targetPermission = permissionById[targetAccess.PermissionId];
                    var uncertain = trustPath.Any(item => item.Status != TrustRelationshipStatus.Confirmed
                            || item.ConditionState != AuthorizationConditionState.Satisfied)
                        || IsConditionUnresolved(sourceAccess, conditionAssessments[sourceAccess.Id])
                        || IsConditionUnresolved(targetAccess, conditionAssessments[targetAccess.Id])
                        || sourceAccess.EvaluationStatus != EvaluationStatus.FullyEvaluated
                        || targetAccess.EvaluationStatus != EvaluationStatus.FullyEvaluated
                        || sourceAccess.Decision is not (AccessDecision.Allow or AccessDecision.Conditional)
                        || targetAccess.Decision is not (AccessDecision.Allow or AccessDecision.Conditional);
                    var confidence = uncertain ? "Low"
                        : sourceAccess.IsInherited || targetAccess.IsInherited ? "Medium" : "High";
                    var score = uncertain ? Math.Min(rule.RiskScore, 55)
                        : confidence == "Medium" ? Math.Min(rule.RiskScore, 70) : rule.RiskScore;
                    var severity = uncertain ? LowerSeverity(rule.Severity, RiskSeverity.Medium) : rule.Severity;
                    var pathJson = JsonSerializer.Serialize(trustPath.Select(item => new
                    {
                        item.SourceProvider, item.SourceCloudAccountId, item.SourcePrincipalId,
                        item.TargetProvider, item.TargetCloudAccountId, item.TargetPrincipalId,
                        item.RelationshipType, item.Status, item.EvidenceSource, item.EvidenceReference,
                        item.RequiresSourcePermission, item.RequiredSourcePermissionPattern,
                        item.ConditionState, item.ConditionEvidence, item.ObservedAtUtc
                    }));
                    var pathDescription = string.Join(" -> ", trustPath.Select(item =>
                        $"{item.SourceProvider}/{item.SourceCloudAccountId:D}/{item.SourcePrincipalId:D} " +
                        $"-[{item.RelationshipType}; {item.EvidenceSource}; {item.EvidenceReference}]-> " +
                        $"{item.TargetProvider}/{item.TargetCloudAccountId:D}/{item.TargetPrincipalId:D}"));
                    var sourcePermission = permissionById[sourceAccess.PermissionId];
                    var evidenceText = $"Rule={rule.Key}; Cross-account/delegated trust path; source action={sourcePermission.Action}; " +
                        $"target action={targetPermission.Action}; path={pathDescription}; " +
                        $"source condition={conditionAssessments[sourceAccess.Id].Summary}; " +
                        $"target condition={conditionAssessments[targetAccess.Id].Summary}";
                    var finding = Finding(context, sourceAccess, RiskEvaluationState.Potential,
                        rule.Name, rule.Description, evidenceText,
                        context.Request.AccessPaths.FirstOrDefault(path => path.EffectiveAccessId == sourceAccess.Id)?.Id,
                        score, severity, new Dictionary<string, string>
                        {
                            ["RuleKey"] = rule.Key,
                            ["RuleEntityId"] = rule.Id.ToString("D"),
                            ["FirstEffectiveAccessId"] = sourceAccess.Id.ToString("D"),
                            ["SecondEffectiveAccessId"] = targetAccess.Id.ToString("D"),
                            ["FirstPermissionId"] = sourceAccess.PermissionId.ToString("D"),
                            ["SecondPermissionId"] = targetAccess.PermissionId.ToString("D"),
                            ["FirstAction"] = sourcePermission.Action ?? string.Empty,
                            ["SecondAction"] = targetPermission.Action ?? string.Empty,
                            ["FirstScope"] = EffectiveScope(sourceAccess, sourcePermission),
                            ["SecondScope"] = EffectiveScope(targetAccess, targetPermission),
                            ["FirstAssignmentType"] = sourceAccess.AssignmentType.ToString(),
                            ["SecondAssignmentType"] = targetAccess.AssignmentType.ToString(),
                            ["FirstIsDirect"] = sourceAccess.IsDirect.ToString(),
                            ["SecondIsDirect"] = targetAccess.IsDirect.ToString(),
                            ["FirstIsInherited"] = sourceAccess.IsInherited.ToString(),
                            ["SecondIsInherited"] = targetAccess.IsInherited.ToString(),
                            ["FirstIsConditional"] = sourceAccess.IsConditional.ToString(),
                            ["SecondIsConditional"] = targetAccess.IsConditional.ToString(),
                            ["FirstEvaluationStatus"] = sourceAccess.EvaluationStatus.ToString(),
                            ["SecondEvaluationStatus"] = targetAccess.EvaluationStatus.ToString(),
                            ["FirstConditionState"] = conditionAssessments[sourceAccess.Id].State.ToString(),
                            ["SecondConditionState"] = conditionAssessments[targetAccess.Id].State.ToString(),
                            ["FirstConditionEvidence"] = conditionAssessments[sourceAccess.Id].Summary + " " + conditionAssessments[sourceAccess.Id].EvidenceSource,
                            ["SecondConditionEvidence"] = conditionAssessments[targetAccess.Id].Summary + " " + conditionAssessments[targetAccess.Id].EvidenceSource,
                            ["ResourceRelation"] = "TrustPath",
                            ["ResourceEvidence"] = $"Target resource {targetAccess.ResourceId?.ToString("D") ?? "unresolved"} reached through evidenced trust path.",
                            ["TrustPathEvidence"] = pathJson,
                            ["Confidence"] = confidence,
                            ["RiskScore"] = score.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["Recommendation"] = rule.Recommendation,
                            ["RequiresValidation"] = "true"
                        });
                    finding.ResourceId = targetAccess.ResourceId;
                    if (!findings.ContainsKey(finding.Fingerprint)) findings[finding.Fingerprint] = finding;
                }
            }
        }
    }

    private static bool IsTrustEvidenceValid(ToxicTrustRelationshipEvidence relationship,
        IReadOnlyList<CloudPrincipal> principals, IReadOnlyList<CloudAccount> accounts)
        => relationship.Status is TrustRelationshipStatus.Confirmed or TrustRelationshipStatus.Potential
            && relationship.ConditionState != AuthorizationConditionState.NotSatisfied
            && relationship.SourcePrincipalId != Guid.Empty && relationship.TargetPrincipalId != Guid.Empty
            && relationship.SourcePrincipalId != relationship.TargetPrincipalId
            && relationship.SourceCloudAccountId != Guid.Empty && relationship.TargetCloudAccountId != Guid.Empty
            && !string.IsNullOrWhiteSpace(relationship.RelationshipType)
            && !string.IsNullOrWhiteSpace(relationship.EvidenceSource)
            && !string.IsNullOrWhiteSpace(relationship.EvidenceReference)
            && relationship.RelationshipType.Length <= 120
            && relationship.EvidenceSource.Length <= 500
            && relationship.EvidenceReference.Length <= 2000
            && (!relationship.RequiresSourcePermission || !string.IsNullOrWhiteSpace(relationship.RequiredSourcePermissionPattern))
            && (relationship.RequiredSourcePermissionPattern?.Length ?? 0) <= 500
            && (relationship.ConditionEvidence?.Length ?? 0) <= 2000
            && principals.Any(item => item.Id == relationship.SourcePrincipalId
                && item.AccountId == relationship.SourceCloudAccountId && Provider(item.CloudProvider) == relationship.SourceProvider)
            && principals.Any(item => item.Id == relationship.TargetPrincipalId
                && item.AccountId == relationship.TargetCloudAccountId && Provider(item.CloudProvider) == relationship.TargetProvider)
            && accounts.Any(item => item.Id == relationship.SourceCloudAccountId
                && Provider(item.CloudProvider) == relationship.SourceProvider)
            && accounts.Any(item => item.Id == relationship.TargetCloudAccountId
                && Provider(item.CloudProvider) == relationship.TargetProvider);

    private static bool IsConditionUnresolved(DomainEffectiveAccess access, AuthorizationConditionAssessment assessment)
        => (access.IsConditional || access.Decision == AccessDecision.Conditional)
            && assessment.State != AuthorizationConditionState.Satisfied;

    private static IReadOnlyList<IReadOnlyList<ToxicTrustRelationshipEvidence>> BuildTrustPaths(
        DomainEffectiveAccess sourceAccess, IReadOnlyList<ToxicTrustRelationshipEvidence> relationships)
    {
        const int maximumDepth = 4;
        const int maximumPaths = 100;
        var found = new List<IReadOnlyList<ToxicTrustRelationshipEvidence>>();
        var path = new List<ToxicTrustRelationshipEvidence>();
        var visited = new HashSet<(Guid AccountId, Guid PrincipalId, CloudProvider Provider)>
        {
            (sourceAccess.CloudAccountId, sourceAccess.PrincipalId, sourceAccess.Provider)
        };
        Visit(sourceAccess.CloudAccountId, sourceAccess.PrincipalId, sourceAccess.Provider);
        return found;

        void Visit(Guid accountId, Guid principalId, CloudProvider provider)
        {
            if (path.Count >= maximumDepth || found.Count >= maximumPaths) return;
            foreach (var relationship in relationships.Where(item => item.SourceCloudAccountId == accountId
                && item.SourcePrincipalId == principalId && item.SourceProvider == provider))
            {
                var target = (relationship.TargetCloudAccountId, relationship.TargetPrincipalId, relationship.TargetProvider);
                if (!visited.Add(target)) continue;
                path.Add(relationship);
                if (relationship.SourceCloudAccountId != relationship.TargetCloudAccountId
                    || relationship.SourceProvider != relationship.TargetProvider)
                    found.Add(path.ToArray());
                Visit(relationship.TargetCloudAccountId, relationship.TargetPrincipalId, relationship.TargetProvider);
                path.RemoveAt(path.Count - 1);
                visited.Remove(target);
                if (found.Count >= maximumPaths) return;
            }
        }
    }

    private static bool IsCandidateAccess(DomainEffectiveAccess access)
        => access.Decision is AccessDecision.Allow or AccessDecision.Conditional
            && access.EvaluationStatus is not (EvaluationStatus.Invalid or EvaluationStatus.Unsupported)
            && access.AccessType != AccessType.Deny;

    private static bool Matches(string? pattern, string? action, CloudProvider provider)
        => !string.IsNullOrWhiteSpace(pattern) && !string.IsNullOrWhiteSpace(action)
            && pattern.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(candidate => ActivityUsageEvaluator.ActionMatches(provider, candidate, action));

    private static bool IsPrivileged(DomainEffectiveAccess access, RiskRuleContext context,
        IReadOnlyDictionary<Guid, CloudPermission> permissions)
    {
        if (access.IsPrivileged) return true;
        if (access.RoleId is { } roleId && context.Request.Roles.FirstOrDefault(role => role.Id == roleId)?.IsPrivileged == true)
            return true;
        var action = permissions[access.PermissionId].Action;
        return action is "*" or "iam:*" or "AdministratorAccess" or "roles/owner"
            || action?.Contains("administrator", StringComparison.OrdinalIgnoreCase) == true
            || action?.EndsWith("/owner", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static (bool Matches, string Confidence, CloudResource? Resource, string Evidence) AssessResource(
        ToxicCombinationRule rule, DomainEffectiveAccess first, DomainEffectiveAccess second,
        CloudPermission firstPermission, CloudPermission secondPermission, RiskRuleContext context)
    {
        if (rule.ResourceRequirement == ToxicResourceRequirement.Any)
            return (true, "Medium", Resource(context, first.ResourceId) ?? Resource(context, second.ResourceId),
                "Rule does not require a sensitive-resource classification.");

        var resources = new[] { Resource(context, first.ResourceId), Resource(context, second.ResourceId) }
            .Where(item => item is not null).Cast<CloudResource>().DistinctBy(item => item.Id).ToArray();
        foreach (var resource in resources)
        {
            var signals = (context.Request.ResourceSecuritySignals ?? Array.Empty<ResourceSecuritySignal>())
                .Where(signal => signal.ResourceId == resource.Id && signal.IsConfirmed).ToArray();
            var sensitiveStorage = IsStorage(resource)
                && (signals.Any(signal => signal.Type == ResourceSecuritySignalType.SensitiveData) || HasSensitiveTag(resource));
            var criticalDatabase = IsDatabase(resource)
                && (signals.Any(signal => signal.Type is ResourceSecuritySignalType.CriticalAsset or ResourceSecuritySignalType.SensitiveData)
                    || HasCriticalTag(resource) || HasSensitiveTag(resource));
            var matched = rule.ResourceRequirement switch
            {
                ToxicResourceRequirement.SensitiveStorage => sensitiveStorage,
                ToxicResourceRequirement.CriticalDatabase => criticalDatabase,
                ToxicResourceRequirement.SensitiveOrCritical => sensitiveStorage || criticalDatabase,
                _ => true
            };
            if (matched)
                return (true, "High", resource,
                    $"Resource {resource.Id:D} matched {rule.ResourceRequirement}; type={resource.Type}; source signals={string.Join(",", signals.Select(signal => signal.Source))}.");
        }

        _ = firstPermission;
        _ = secondPermission;
        return (false, "Low", null, $"No in-scope resource has confirmed evidence matching {rule.ResourceRequirement}.");
    }

    private static ScopeAssessment AssessScope(DomainEffectiveAccess first, DomainEffectiveAccess second,
        RiskRuleContext context, bool requireSameResource)
    {
        if (first.ResourceId.HasValue && second.ResourceId.HasValue)
            return first.ResourceId == second.ResourceId ? ScopeAssessment.Overlap : ScopeAssessment.Disjoint;

        var firstScope = EffectiveScope(first, Permission(context, first.PermissionId) ?? new CloudPermission());
        var secondScope = EffectiveScope(second, Permission(context, second.PermissionId) ?? new CloudPermission());
        if (string.IsNullOrWhiteSpace(firstScope) || string.IsNullOrWhiteSpace(secondScope))
            return requireSameResource ? ScopeAssessment.Unknown : ScopeAssessment.Unknown;
        if (string.Equals(firstScope, secondScope, StringComparison.OrdinalIgnoreCase))
            return ScopeAssessment.Overlap;
        if (IsAncestor(firstScope, secondScope) || IsAncestor(secondScope, firstScope))
            return ScopeAssessment.Overlap;
        return ScopeAssessment.Disjoint;
    }

    private static string EffectiveScope(DomainEffectiveAccess access, CloudPermission permission)
        => access.Scope ?? permission.Scope ?? permission.Resource ?? string.Empty;

    private static bool IsAncestor(string parent, string child)
    {
        var normalizedParent = parent.TrimEnd('/', ':');
        return child.StartsWith(normalizedParent + "/", StringComparison.OrdinalIgnoreCase)
            || child.StartsWith(normalizedParent + ":", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStorage(CloudResource resource)
        => ContainsAny($"{resource.Type} {resource.ResourceId} {resource.Name}", "storage", "s3", "bucket", "blob", "object");

    private static bool IsDatabase(CloudResource resource)
        => ContainsAny($"{resource.Type} {resource.ResourceId} {resource.Name}", "database", "sql", "rds", "cloudsql", "cosmos", "spanner");

    private static bool HasSensitiveTag(CloudResource resource)
        => resource.Tags.Any(tag => ContainsAny(tag.Key, "sensitivity", "classification", "sensitive")
            && ContainsAny(tag.Value, "sensitive", "confidential", "restricted", "critical", "tier0"));

    private static bool HasCriticalTag(CloudResource resource)
        => resource.Tags.Any(tag => ContainsAny(tag.Key, "critical", "businesscriticality", "criticality", "assettier")
            && ContainsAny(tag.Value, "true", "yes", "critical", "tier0", "crown-jewel", "crown jewel"));

    private static string ProviderSemantics(CloudProvider provider) => provider switch
    {
        CloudProvider.Aws => "AWS IAM action names are matched case-insensitively; role trust and PassRole implications are not inferred without explicit effective permissions.",
        CloudProvider.Azure => "Azure RBAC action patterns are matched case-insensitively; scope inheritance is accepted only from explicit normalized scope evidence.",
        CloudProvider.Gcp => "GCP IAM permission names are matched case-sensitively; service-account impersonation requires an explicit matching permission.",
        _ => "Provider semantics are unsupported."
    };

    private static RiskSeverity LowerSeverity(RiskSeverity severity, RiskSeverity ceiling)
        => (int)severity < (int)ceiling ? severity : ceiling;

    private static bool ContainsAny(string value, params string[] fragments)
        => fragments.Any(fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private enum ScopeAssessment { Overlap, Unknown, Disjoint }
}

public static class ToxicCombinationRuleDefaults
{
    public static IReadOnlyList<ToxicCombinationRule> Create()
    {
        var rules = new List<ToxicCombinationRule>();
        Add(CloudProvider.Aws, "aws.sensitive-storage-public", "Sensitive storage read plus public access control",
            "An identity has both access to sensitive storage and a provider permission that may change its exposure.",
            "s3:GetObject", "s3:PutBucketPolicy", ToxicResourceRequirement.SensitiveStorage, RiskSeverity.High, 78);
        Add(CloudProvider.Azure, "azure.sensitive-storage-public", "Sensitive storage read plus public access control",
            "An identity has both sensitive storage access and a permission that may enable public exposure.",
            "Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read",
            "Microsoft.Storage/storageAccounts/write", ToxicResourceRequirement.SensitiveStorage, RiskSeverity.High, 78);
        Add(CloudProvider.Gcp, "gcp.sensitive-storage-public", "Sensitive storage read plus public access control",
            "An identity has both sensitive object access and a permission that may change bucket policy.",
            "storage.objects.get", "storage.buckets.setIamPolicy", ToxicResourceRequirement.SensitiveStorage, RiskSeverity.High, 78);

        Add(CloudProvider.Aws, "aws.iam-critical-database", "IAM policy modification plus critical database access",
            "An identity can modify IAM policy and access a resource explicitly classified as a critical database.",
            "rds:*|database.*|database.read", "iam:PutRolePolicy", ToxicResourceRequirement.CriticalDatabase, RiskSeverity.Critical, 92);
        Add(CloudProvider.Azure, "azure.role-write-critical-data", "Role assignment modification plus critical data access",
            "An identity can modify role assignments and access an explicitly classified critical database.",
            "Microsoft.Sql/servers/databases/read", "Microsoft.Authorization/roleAssignments/write",
            ToxicResourceRequirement.CriticalDatabase, RiskSeverity.Critical, 92);
        Add(CloudProvider.Gcp, "gcp.iam-critical-data", "IAM policy modification plus critical data access",
            "An identity can change project IAM policy and access an explicitly classified critical database.",
            "cloudsql.instances.get", "resourcemanager.projects.setIamPolicy",
            ToxicResourceRequirement.CriticalDatabase, RiskSeverity.Critical, 92);

        Add(CloudProvider.Aws, "aws.credential-admin", "Credential creation plus administrative privilege",
            "An identity can create persistent credentials and also has an effective privileged grant.",
            "iam:CreateAccessKey", "*", ToxicResourceRequirement.Any, RiskSeverity.Critical, 90,
            secondRequiresPrivileged: true);
        Add(CloudProvider.Azure, "azure.credential-admin", "Credential management plus administrative privilege",
            "An identity can manage workload credentials and also has an effective privileged grant.",
            "microsoft.directory/servicePrincipals/credentials/update", "*", ToxicResourceRequirement.Any,
            RiskSeverity.Critical, 90, secondRequiresPrivileged: true);
        Add(CloudProvider.Gcp, "gcp.credential-admin", "Service-account key creation plus administrative privilege",
            "An identity can create service-account keys and also has an effective privileged grant.",
            "iam.serviceAccountKeys.create", "*", ToxicResourceRequirement.Any, RiskSeverity.Critical, 90,
            secondRequiresPrivileged: true);

        Add(CloudProvider.Aws, "aws.trust-policy-assume-role", "Trust policy modification plus role assumption",
            "An identity can modify role trust configuration and has an effective role-assumption permission.",
            "iam:UpdateAssumeRolePolicy", "sts:AssumeRole", ToxicResourceRequirement.Any, RiskSeverity.High, 78);
        Add(CloudProvider.Azure, "azure.workload-change-identity-access", "Managed identity access plus workload modification",
            "An identity can modify workloads and has permission to assign a managed identity.",
            "Microsoft.ManagedIdentity/userAssignedIdentities/assign/action",
            "Microsoft.Compute/virtualMachines/write", ToxicResourceRequirement.Any, RiskSeverity.High, 75);
        Add(CloudProvider.Gcp, "gcp.impersonation-admin", "Service-account impersonation plus privileged project access",
            "An identity can impersonate a service account and also has effective privileged access.",
            "iam.serviceAccounts.getAccessToken", "*", ToxicResourceRequirement.Any, RiskSeverity.Critical, 90,
            secondRequiresPrivileged: true);
        Add(null, "federated.trust-privileged-target", "Federated trust path to a privileged identity",
            "An evidenced cross-account or cross-cloud federation relationship reaches an identity with privileged effective access.",
            "*",
            "*", ToxicResourceRequirement.Any, RiskSeverity.High, 82,
            secondRequiresPrivileged: true, requireTrustPath: true);
        return rules;

        void Add(CloudProvider? provider, string key, string name, string description, string first, string second,
            ToxicResourceRequirement resourceRequirement, RiskSeverity severity, int score,
            bool firstRequiresPrivileged = false, bool secondRequiresPrivileged = false, bool requireTrustPath = false)
        {
            rules.Add(new ToxicCombinationRule
            {
                Id = StableId(key), Key = key, Name = name, Description = description, Provider = provider,
                FirstPermissionPattern = first, SecondPermissionPattern = second,
                ResourceRequirement = resourceRequirement, Severity = severity, RiskScore = score,
                FirstRequiresPrivilegedAccess = firstRequiresPrivileged,
                SecondRequiresPrivilegedAccess = secondRequiresPrivileged,
                RequireTrustPath = requireTrustPath,
                RequireSameResource = resourceRequirement is ToxicResourceRequirement.SensitiveStorage or ToxicResourceRequirement.CriticalDatabase,
                Recommendation = "Review both grants, their explicit scopes, conditions, trust relationships and resource classification with the resource owner. No permission is changed automatically."
            });
        }
    }

    private static Guid StableId(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
