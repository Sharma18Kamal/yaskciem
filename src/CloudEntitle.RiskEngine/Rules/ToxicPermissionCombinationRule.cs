using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.RiskEngine.Models;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;

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
        var findings = new List<RiskFindingModel>();
        var granted = context.Request.EffectiveAccess
            .Where(access => access.Decision == AccessDecision.Allow)
            .ToArray();

        foreach (var targetAccess in granted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var principalAccess = granted.Where(access =>
                access.PrincipalId == targetAccess.PrincipalId
                && access.CloudAccountId == targetAccess.CloudAccountId
                && access.Provider == targetAccess.Provider
                && access.Id != targetAccess.Id).ToArray();

            var resource = Resource(context, targetAccess.ResourceId);
            if (resource is null) continue;

            var resourceSignals = (context.Request.ResourceSecuritySignals ?? Array.Empty<ResourceSecuritySignal>())
                .Where(signal => signal.ResourceId == resource.Id && signal.IsConfirmed)
                .ToArray();

            if (IsSensitiveStorage(resource, resourceSignals)
                && IsStorageRead(Permission(context, targetAccess.PermissionId)?.Action)
                && principalAccess.Any(access => CanMakePublic(access, targetAccess, context)))
            {
                AddFinding(
                    context,
                    findings,
                    targetAccess,
                    "Sensitive storage access plus public-access control",
                    "The identity can read a sensitive storage resource and can change access controls that may expose resources publicly.",
                    "Sensitive storage read and public-access control permissions are both effective for this identity and account.",
                    RiskSeverity.High,
                    principalAccess.First(access => CanMakePublic(access, targetAccess, context)));
            }

            if (IsCriticalDatabase(resource, resourceSignals)
                && principalAccess.Any(access => CanModifyIam(access, context)))
            {
                AddFinding(
                    context,
                    findings,
                    targetAccess,
                    "IAM modification plus critical database access",
                    "The identity can access a critical database and can modify IAM policy or role assignments in the same cloud account.",
                    "Critical database access and IAM policy modification permissions are both effective for this identity and account.",
                    RiskSeverity.Critical,
                    principalAccess.First(access => CanModifyIam(access, context)));
            }

            if (CanCreateCredentials(targetAccess, context)
                && principalAccess.Any(access => IsAdministrative(access, context)))
            {
                AddFinding(
                    context,
                    findings,
                    targetAccess,
                    "Credential creation plus administrative privilege",
                    "The identity can create credentials and also has administrative privileges, enabling persistence or privilege expansion.",
                    "Credential creation and administrative permissions are both effective for this identity and account.",
                    RiskSeverity.Critical,
                    principalAccess.First(access => IsAdministrative(access, context)));
            }
        }

        return Task.FromResult<IReadOnlyCollection<RiskFindingModel>>(findings);
    }

    private void AddFinding(
        RiskRuleContext context,
        ICollection<RiskFindingModel> findings,
        DomainEffectiveAccess anchor,
        string combination,
        string description,
        string evidence,
        RiskSeverity severity,
        DomainEffectiveAccess companion)
    {
        var path = context.Request.AccessPaths.FirstOrDefault(item => item.EffectiveAccessId == anchor.Id);
        var detail = $"{evidence} Combination={combination}; AnchorEffectiveAccess={anchor.Id}; CompanionEffectiveAccess={companion.Id}";
        findings.Add(Finding(
            context,
            anchor,
            RiskEvaluationState.Potential,
            combination,
            description,
            detail,
            path?.Id,
            priorityScore: severity == RiskSeverity.Critical ? 80 : 65,
            severity: severity,
            evidenceDetails: new Dictionary<string, string>
            {
                ["Combination"] = combination,
                ["AnchorEffectiveAccessId"] = anchor.Id.ToString("D"),
                ["CompanionEffectiveAccessId"] = companion.Id.ToString("D"),
                ["RequiresValidation"] = "true"
            }));
    }

    private static bool IsSensitiveStorage(CloudResource resource, IReadOnlyList<ResourceSecuritySignal> signals)
        => IsStorageResource(resource)
            && (signals.Any(signal => signal.Type == ResourceSecuritySignalType.SensitiveData)
                || HasSensitiveTag(resource));

    private static bool IsStorageResource(CloudResource resource)
        => ContainsAny($"{resource.Type} {resource.ResourceId} {resource.Name}",
            "storage", "s3", "bucket", "blob", "object");

    private static bool IsCriticalDatabase(CloudResource resource, IReadOnlyList<ResourceSecuritySignal> signals)
        => ContainsAny($"{resource.Type} {resource.ResourceId} {resource.Name}",
                "database", "sql", "rds", "cloudsql", "cosmos", "spanner")
            && (signals.Any(signal => signal.Type is ResourceSecuritySignalType.CriticalAsset or ResourceSecuritySignalType.SensitiveData)
                || HasCriticalTag(resource));

    private static bool HasSensitiveTag(CloudResource resource)
        => resource.Tags.Any(tag =>
            ContainsAny(tag.Key, "sensitivity", "classification", "sensitive")
            && ContainsAny(tag.Value, "sensitive", "confidential", "restricted", "critical", "tier0"));

    private static bool HasCriticalTag(CloudResource resource)
        => resource.Tags.Any(tag =>
            ContainsAny(tag.Key, "critical", "businesscriticality", "criticality", "assettier")
            && ContainsAny(tag.Value, "true", "yes", "critical", "tier0", "crown-jewel", "crown jewel"));

    private static bool IsStorageRead(string? action)
        => !string.IsNullOrWhiteSpace(action)
            && ContainsAny(action, "read", "getobject", "getblob", "objects.get", "objects.list", "listbucket", "blobdatareader");

    private static bool CanMakePublic(
        DomainEffectiveAccess access,
        DomainEffectiveAccess targetAccess,
        RiskRuleContext context)
    {
        if (access.PrincipalId != targetAccess.PrincipalId
            || access.CloudAccountId != targetAccess.CloudAccountId
            || access.Provider != targetAccess.Provider
            || access.ResourceId.HasValue && access.ResourceId != targetAccess.ResourceId)
            return false;

        return ContainsAny(Permission(context, access.PermissionId)?.Action,
            "putbucketpolicy", "putbucketacl", "putobjectacl", "putaccesspointpolicy",
            "publicaccess", "publicnetworkaccess", "allowblobpublicaccess",
            "storageaccounts/write", "containers/write", "setiampolicy");
    }

    private static bool CanModifyIam(DomainEffectiveAccess access, RiskRuleContext context)
        => ContainsAny(Permission(context, access.PermissionId)?.Action,
            "putrolepolicy", "attachrolepolicy", "createpolicyversion", "updateroletrust",
            "roleassignments/write", "roledefinitions/write", "setiampolicy",
            "iam.roles.update", "iam.roles.create", "iam.policies.update", "iam.policies.create",
            "iam:passrole");

    private static bool CanCreateCredentials(DomainEffectiveAccess access, RiskRuleContext context)
        => ContainsAny(Permission(context, access.PermissionId)?.Action,
            "createaccesskey", "createcredentials", "serviceaccountkeys.create",
            "passwordcredentials", "credentials/write", "serviceprincipals/credentials");

    private static bool IsAdministrative(DomainEffectiveAccess access, RiskRuleContext context)
        => access.IsPrivileged
            || ContainsAny(Permission(context, access.PermissionId)?.Action,
                "administratoraccess", "admin", "owner", "iam:*", "actions:*", "datapolicy:*", "allresources");

    private static bool ContainsAny(string? value, params string[] fragments)
        => !string.IsNullOrWhiteSpace(value)
            && fragments.Any(fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
