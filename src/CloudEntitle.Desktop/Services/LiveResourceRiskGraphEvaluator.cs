using System.Security.Cryptography;
using System.Text;
using CloudEntitle.AccessPath.Models;
using CloudEntitle.AccessPath.Services;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Application.Services;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Normalization.Models;
using CloudEntitle.RiskEngine.Models;
using CloudEntitle.Integration.Contracts;
using GraphRiskEngine = CloudEntitle.RiskEngine.Services.RiskEngine;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;
using DomainAccessPath = CloudEntitle.Domain.Entities.AccessPath;

namespace CloudEntitle.Desktop.Services;

public sealed record LiveResourceAccessAssignment(CloudProviderType Provider, Guid AccountId, CloudEffectiveAccessAssignment Assignment);

public sealed record LiveResourceRiskGraphResult(
    IReadOnlyList<DomainAccessPath> AccessPaths,
    IReadOnlyList<RiskFindingModel> Findings,
    IReadOnlyList<string> Diagnostics);

public sealed class LiveResourceRiskGraphEvaluator
{
    private readonly AccessPathEngine _accessPathEngine = new();
    private readonly GraphRiskEngine _riskEngine = new();

    public async Task<LiveResourceRiskGraphResult> EvaluateDocumentsAsync(CloudAccount account,
        IReadOnlyList<CloudPrincipal> principals, IReadOnlyList<CloudResource> resources,
        EntitlementEvidenceSnapshot documents, CancellationToken token = default)
    {
        if (account.Provider != documents.Provider || !string.Equals(account.ExternalId, documents.AccountId,
            documents.Provider == CloudProvider.Azure ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Permission document risk evaluation requires matching non-demo account inventory.");
        var permissions = new List<CloudPermission>();
        var entitlements = new List<NormalizedEntitlement>();
        var access = new List<DomainEffectiveAccess>();
        var scopedPrincipals = principals.Where(item => item.AccountId == account.Id && (int)item.CloudProvider == (int)documents.Provider).ToArray();
        var scopedResources = resources.Where(item => item.AccountId == account.Id).ToArray();
        foreach (var grant in documents.Grants.Where(item => item.Effect == "Allow" && !item.IsExclusion && !item.HasCondition))
        {
            token.ThrowIfCancellationRequested();
            if (documents.Grants.Any(block => block.Principal == grant.Principal && block.SourceId == grant.SourceId
                && (block.IsExclusion || block.Effect == "Deny" || block.HasCondition))) continue;
            var matches = scopedPrincipals.Where(item => PrincipalMatches(documents.Provider, grant.Principal, item)).ToArray();
            if (matches.Length != 1) continue;
            var principal = matches[0];
            var targets = scopedResources.Where(resource => DocumentResourceMatches(documents.Provider, grant, resource)).ToArray();
            var permissionId = StableId($"document-permission|{documents.Provider}|{documents.AccountId}|{grant.SourceId}|{grant.StatementId}|{grant.Action}|{grant.ResourcePattern}");
            if (permissions.All(item => item.Id != permissionId))
                permissions.Add(new CloudPermission
                {
                    Id = permissionId, AccountId = account.Id, Action = grant.Action, Name = grant.Action,
                    Scope = grant.Scope, Resource = grant.ResourcePattern, Source = grant.SourceId, AccessType = AccessType.Allow
                });
            var evidence = new ProviderEvidence(documents.Provider, "ObservedPermissionDocument", grant.SourceId, grant.Role,
                null, null, grant.Scope, "Observed document statement", documents.CollectedAtUtc.UtcDateTime,
                new Dictionary<string, string> { ["sourceSha256"] = grant.SourceSha256, ["sourceVersion"] = grant.SourceVersion, ["evaluation"] = "partial" });
            foreach (var resource in targets)
            {
                if (access.Count >= 20_000) throw new InvalidOperationException("Document risk candidate limit exceeded; narrow the inventory. No complete risk result was produced.");
                var id = StableId($"document-access|{permissionId}|{principal.Id}|{resource.Id}");
                if (access.Any(item => item.Id == id)) continue;
                entitlements.Add(new(id, documents.Provider, account.Id, principal.Id, resource.Id, null, null, permissionId,
                    new(documents.Provider, NormalizedScopeType.Resource, grant.Scope, grant.Scope, resource.ResourceId, false),
                    AssignmentType.Policy, NormalizedAccessType.Allow, true, false, false, null, NormalizationStatus.PartiallyNormalized,
                    evidence, documents.CollectedAtUtc.UtcDateTime, null, grant.ResourcePattern));
                access.Add(new()
                {
                    Id = id, Provider = documents.Provider, CloudAccountId = account.Id, PrincipalId = principal.Id,
                    ResourceId = resource.Id, PermissionId = permissionId, Decision = AccessDecision.Allow,
                    EvaluationStatus = EvaluationStatus.PartiallyEvaluated, Scope = grant.Scope, AssignmentType = AssignmentType.Policy,
                    AccessType = AccessType.Allow, IsDirect = true, IsPrivileged = grant.Action is "*" or "iam:*",
                    Evidence = $"Observed source {grant.SourceId}; version {grant.SourceVersion}; SHA256 {grant.SourceSha256}. Partial candidate, not effective authorization.",
                    AccessSource = grant.SourceId, CalculatedAt = documents.CollectedAtUtc.UtcDateTime
                });
            }
        }
        var mapped = scopedResources.Select(resource => ResourceSecuritySignalMapper.MapTags(resource, account.CloudProvider)).ToArray();
        var signals = mapped.SelectMany(item => item.Signals).ToArray();
        var coverage = mapped.SelectMany(item => item.Coverage).ToArray();
        var normalized = new NormalizationResult(scopedPrincipals, [], [], permissions, entitlements, [],
            ["Observed documents are partial candidates, not effective authorization."], new(entitlements.Count, 0, entitlements.Count, 0, 0));
        var paths = await _accessPathEngine.BuildAsync(new(access, normalized, scopedResources, [], ResourceSecuritySignals: signals,
            ResourceSecurityCoverage: coverage, EvaluatedAt: documents.CollectedAtUtc.UtcDateTime), token);
        var risk = await _riskEngine.EvaluateAsync(new([account], scopedPrincipals, scopedResources, [], [], permissions,
            entitlements, access, paths.Paths, [], EvaluatedAt: documents.CollectedAtUtc.UtcDateTime,
            Options: new(EnabledRules: new HashSet<string> { "CIEM-017", "CIEM-018", "CIEM-019", "CIEM-020", "CIEM-021" }),
            ResourceSecuritySignals: signals, ResourceSecurityCoverage: coverage), token);
        foreach (var finding in risk.Findings)
            finding.EvaluationState = RiskEvaluationState.Potential;
        return new(paths.Paths, risk.Findings,
            documents.Limitations.Concat(paths.Diagnostics.Select(item => item.Message)).Concat(risk.Diagnostics.Select(item => item.Message))
                .Append("Document-based review candidates only. Uncollected denies, runtime conditions and resource controls may block these paths; no confirmed exploitation or remediation approval is asserted.").ToArray());
    }

    private static bool PrincipalMatches(CloudProvider provider, string external, CloudPrincipal principal)
    {
        var comparison = provider == CloudProvider.Azure ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(external, principal.ExternalId, comparison) || string.Equals(external, principal.PrincipalId, comparison)) return true;
        return provider == CloudProvider.Gcp && external.StartsWith("serviceAccount:", StringComparison.Ordinal)
            && (string.Equals(external["serviceAccount:".Length..], principal.ExternalId, StringComparison.Ordinal)
                || string.Equals(external["serviceAccount:".Length..], principal.Email, StringComparison.Ordinal));
    }

    private static bool DocumentResourceMatches(CloudProvider provider, ObservedPermissionGrant grant, CloudResource resource)
    {
        var external = string.IsNullOrWhiteSpace(resource.ExternalId) ? resource.ResourceUri : resource.ExternalId;
        if (string.IsNullOrWhiteSpace(external)) return false;
        if (provider == CloudProvider.Aws)
            return System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(grant.ResourcePattern, external, ignoreCase: false);
        if (provider == CloudProvider.Azure)
            return external.Equals(grant.Scope, StringComparison.OrdinalIgnoreCase)
                || external.StartsWith(grant.Scope.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
        return external.Equals(grant.Scope, StringComparison.Ordinal)
            || external.StartsWith(grant.Scope + "/", StringComparison.Ordinal);
    }

    public async Task<LiveResourceRiskGraphResult> EvaluateAsync(
        IReadOnlyList<CloudAccount> accounts,
        IReadOnlyList<CloudPrincipal> sourcePrincipals,
        IReadOnlyList<CloudResource> resources,
        IReadOnlyList<CloudRole> sourceRoles,
        IReadOnlyList<CloudPermission> sourcePermissions,
        IReadOnlyList<LiveResourceAccessAssignment> assignments,
        IReadOnlyList<ResourceSecuritySignal> signals,
        IReadOnlyList<ResourceSecurityEvidenceCoverage> coverage,
        DateTime evaluatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        var principals = sourcePrincipals.ToDictionary(principal => principal.Id);
        var roles = sourceRoles.ToDictionary(role => role.Id);
        var graphPermissions = new List<CloudPermission>();
        var entitlements = new List<NormalizedEntitlement>();
        var effectiveAccess = new List<DomainEffectiveAccess>();
        var diagnostics = new List<string>();

        foreach (var item in assignments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.AccountId == Guid.Empty || !accounts.Any(account => account.Id == item.AccountId)) continue;
            if (item.Assignment.AssignmentType.Contains("Role usage", StringComparison.OrdinalIgnoreCase)
                || item.Assignment.AssignmentType.Contains("Eligible", StringComparison.OrdinalIgnoreCase)) continue;
            if (!TryGetProvider(item.Provider, out var provider)) continue;

            var principal = FindPrincipal(sourcePrincipals, item.Assignment.Principal);
            if (principal is null)
            {
                principal = new CloudPrincipal
                {
                    Id = StableId($"principal|{item.Provider}|{item.AccountId}|{item.Assignment.Principal}"),
                    AccountId = item.AccountId,
                    CloudProvider = item.Provider,
                    Name = item.Assignment.Principal,
                    PrincipalId = item.Assignment.Principal,
                    IdentityType = InferIdentityType(item.Assignment.Principal),
                    IsPrivileged = item.Assignment.IsPrivileged,
                    IsActive = true,
                    CreatedAt = evaluatedAtUtc,
                    UpdatedAt = evaluatedAtUtc
                };
                principals[principal.Id] = principal;
            }

            var role = sourceRoles.FirstOrDefault(candidate => candidate.AccountId == item.AccountId
                && (candidate.Name.Equals(item.Assignment.Role, StringComparison.OrdinalIgnoreCase)
                    || candidate.RoleId.Equals(item.Assignment.Role, StringComparison.OrdinalIgnoreCase)));
            if (role is null)
            {
                role = new CloudRole
                {
                    Id = StableId($"role|{item.Provider}|{item.AccountId}|{item.Assignment.Role}"),
                    AccountId = item.AccountId,
                    Name = item.Assignment.Role,
                    RoleId = item.Assignment.Role,
                    IsPrivileged = item.Assignment.IsPrivileged,
                    CreatedAt = evaluatedAtUtc,
                    UpdatedAt = evaluatedAtUtc
                };
                roles[role.Id] = role;
            }

            var targets = resources.Where(resource => resource.AccountId == item.AccountId && ScopeIncludes(item.Assignment.Scope, resource)).ToArray();
            if (targets.Length == 0)
            {
                diagnostics.Add($"No inventoried resource matched {item.Provider} access scope '{item.Assignment.Scope}'.");
                continue;
            }

            var permissionId = StableId($"permission|{item.Provider}|{item.AccountId}|{item.Assignment.Role}|{item.Assignment.Scope}");
            var permission = new CloudPermission
            {
                Id = permissionId,
                AccountId = item.AccountId,
                Name = item.Assignment.Role,
                Action = item.Assignment.Role,
                Resource = item.Assignment.Scope,
                Scope = item.Assignment.Scope,
                Source = item.Assignment.Source,
                Provider = provider.ToString(),
                AccessType = AccessType.Allow,
                Description = "Role-level provider assignment; individual actions are not evaluated by this live graph bridge."
            };
            graphPermissions.Add(permission);
            var assignmentType = ParseAssignmentType(item.Assignment.AssignmentType);
            var evidence = new ProviderEvidence(provider, "LiveEffectiveAccessAssignment", item.Assignment.Source,
                item.Assignment.Role, null, null, item.Assignment.Scope, item.Assignment.AssignmentType,
                evaluatedAtUtc, new Dictionary<string, string>
                {
                    ["roleLevel"] = "true",
                    ["evaluation"] = "partial",
                    ["lastChangedAtUtc"] = item.Assignment.LastChangedAt?.ToUniversalTime().ToString("O") ?? string.Empty,
                    ["lastChangedBy"] = item.Assignment.LastChangedBy ?? string.Empty
                });
            var scope = new NormalizedScope(provider, ClassifyScope(item.Assignment.Scope), item.Assignment.Scope,
                item.Assignment.Scope, null, assignmentType == AssignmentType.Inherited);

            foreach (var resource in targets)
            {
                var effectiveId = StableId($"effective|{item.Provider}|{item.AccountId}|{principal.Id}|{role.Id}|{permissionId}|{resource.Id}");
                entitlements.Add(new NormalizedEntitlement(
                    StableId($"entitlement|{effectiveId}"), provider, item.AccountId, principal.Id, resource.Id, role.Id, null,
                    permissionId, scope, assignmentType, NormalizedAccessType.Allow, assignmentType is AssignmentType.Direct or AssignmentType.Role,
                    assignmentType == AssignmentType.Inherited, false, null, NormalizationStatus.PartiallyNormalized, evidence,
                    evaluatedAtUtc, null, resource.ResourceId ?? item.Assignment.Scope));
                effectiveAccess.Add(new DomainEffectiveAccess
                {
                    Id = effectiveId,
                    Provider = provider,
                    CloudAccountId = item.AccountId,
                    PrincipalId = principal.Id,
                    ResourceId = resource.Id,
                    PermissionId = permissionId,
                    RoleId = role.Id,
                    Scope = item.Assignment.Scope,
                    AccessType = AccessType.Allow,
                    AssignmentType = assignmentType,
                    IsDirect = assignmentType is AssignmentType.Direct or AssignmentType.Role,
                    IsInherited = assignmentType == AssignmentType.Inherited,
                    IsPrivileged = item.Assignment.IsPrivileged || role.IsPrivileged || principal.IsPrivileged,
                    Decision = AccessDecision.Allow,
                    EvaluationStatus = EvaluationStatus.PartiallyEvaluated,
                    AccessSource = item.Assignment.Source,
                    Evidence = item.Assignment.Source,
                    EvaluationDetails = "Modeled from a provider role assignment; individual permission actions and runtime conditions were not evaluated.",
                    CalculatedAt = evaluatedAtUtc
                });
            }
        }

        var normalized = new NormalizationResult(principals.Values.ToArray(), roles.Values.ToArray(), Array.Empty<CloudPolicy>(),
            sourcePermissions.Concat(graphPermissions).ToArray(), entitlements, Array.Empty<NormalizationWarning>(),
            new[] { "Live role assignments are modeled as partial entitlements; permission-level evaluation is not asserted." },
            new(entitlements.Count, entitlements.Count, entitlements.Count, 0, 0));
        var accessPathResult = await _accessPathEngine.BuildAsync(new AccessPathRequest(
            effectiveAccess, normalized, resources, Array.Empty<GroupMembership>(),
            ResourceSecuritySignals: signals, ResourceSecurityCoverage: coverage, EvaluatedAt: evaluatedAtUtc), cancellationToken);
        if (accessPathResult.Diagnostics.Count > 0)
            diagnostics.AddRange(accessPathResult.Diagnostics.Select(diagnostic => diagnostic.Message));

        var options = new RiskRuleOptions(EnabledRules: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CIEM-017", "CIEM-018", "CIEM-019", "CIEM-020", "CIEM-022"
        });
        var riskResult = await _riskEngine.EvaluateAsync(new RiskEvaluationRequest(
            accounts, principals.Values.ToArray(), resources, roles.Values.ToArray(), Array.Empty<CloudPolicy>(),
            sourcePermissions.Concat(graphPermissions).ToArray(), entitlements, effectiveAccess, accessPathResult.Paths,
            Array.Empty<ActivityRecord>(), EvaluatedAt: evaluatedAtUtc, Options: options,
            ResourceSecuritySignals: signals, ResourceSecurityCoverage: coverage), cancellationToken);
        diagnostics.AddRange(riskResult.Diagnostics.Select(diagnostic => diagnostic.Message));
        return new LiveResourceRiskGraphResult(accessPathResult.Paths, riskResult.Findings, diagnostics);
    }

    private static CloudPrincipal? FindPrincipal(IEnumerable<CloudPrincipal> principals, string identity)
        => principals.FirstOrDefault(principal => principal.Name.Equals(identity, StringComparison.OrdinalIgnoreCase)
            || string.Equals(principal.PrincipalId, identity, StringComparison.OrdinalIgnoreCase)
            || string.Equals(principal.Email, identity, StringComparison.OrdinalIgnoreCase));

    private static bool ScopeIncludes(string scope, CloudResource resource)
    {
        if (string.IsNullOrWhiteSpace(scope) || scope.Equals("AWS account", StringComparison.OrdinalIgnoreCase)) return true;
        var resourceScopes = new[] { resource.ResourceId, resource.ResourceUri, resource.Scope }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        return resourceScopes.Any(value => value!.Equals(scope, StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(scope.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)
            || scope.Contains("/", StringComparison.Ordinal) && value.Contains(scope.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
    }

    private static IdentityType InferIdentityType(string identity)
        => identity.StartsWith("group:", StringComparison.OrdinalIgnoreCase) ? IdentityType.Group
            : identity.Contains("serviceAccount:", StringComparison.OrdinalIgnoreCase) || identity.Contains(".gserviceaccount.com", StringComparison.OrdinalIgnoreCase) ? IdentityType.ServiceAccount
            : identity.Contains("assumed-role", StringComparison.OrdinalIgnoreCase) || identity.Contains(":role/", StringComparison.OrdinalIgnoreCase) ? IdentityType.Role
            : identity.Contains("serviceprincipal", StringComparison.OrdinalIgnoreCase) || identity.Contains("spn:", StringComparison.OrdinalIgnoreCase) ? IdentityType.ServicePrincipal
            : IdentityType.User;

    private static AssignmentType ParseAssignmentType(string value)
        => value.Contains("inherited", StringComparison.OrdinalIgnoreCase) ? AssignmentType.Inherited
            : value.Contains("group", StringComparison.OrdinalIgnoreCase) ? AssignmentType.Group
            : value.Contains("policy", StringComparison.OrdinalIgnoreCase) ? AssignmentType.Policy
            : value.Contains("role", StringComparison.OrdinalIgnoreCase) || value.Contains("assigned", StringComparison.OrdinalIgnoreCase) || value.Contains("active (pim)", StringComparison.OrdinalIgnoreCase) ? AssignmentType.Role
            : AssignmentType.Direct;

    private static NormalizedScopeType ClassifyScope(string scope)
        => scope.Contains("subscriptions", StringComparison.OrdinalIgnoreCase) ? NormalizedScopeType.Subscription
            : scope.Contains("projects/", StringComparison.OrdinalIgnoreCase) ? NormalizedScopeType.Project
            : scope.Contains("resourceGroups/", StringComparison.OrdinalIgnoreCase) ? NormalizedScopeType.ResourceGroup
            : scope.StartsWith("arn:", StringComparison.OrdinalIgnoreCase) ? NormalizedScopeType.Resource
            : NormalizedScopeType.Unknown;

    private static bool TryGetProvider(CloudProviderType providerType, out CloudProvider provider)
        => Enum.TryParse(providerType.ToString(), true, out provider);

    private static Guid StableId(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }
}
