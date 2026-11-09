using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudEntitle.Domain;
using CloudEntitle.EffectiveAccess.Abstractions;
using CloudEntitle.EffectiveAccess.Models;
using CloudEntitle.Normalization.Models;
using CloudEntitle.Normalization.Services;

namespace CloudEntitle.EffectiveAccess.Services;

public sealed class WhatIfPermissionSimulator(IEffectiveAccessEngine engine, IWhatIfScenarioAnalyzer? scenarioAnalyzer = null)
{
    public const int MaximumJsonBytes = 8 * 1024 * 1024;
    public const int MaximumPairs = 10000;
    private const long MaximumSecurityAnalysisWork = 2_000_000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static async Task<WhatIfInput> ImportAsync(string json, CancellationToken token = default)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes)
            throw new ArgumentException("The offline bundle exceeds the 8 MiB limit.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        RejectDuplicateProperties(document.RootElement);
        var bundle = JsonSerializer.Deserialize<OfflineAuthorizationBundle>(json, JsonOptions)
            ?? throw new ArgumentException("An offline authorization bundle is required.");
        if (bundle.SchemaVersion != "1.0" || string.IsNullOrWhiteSpace(bundle.SourceLabel)
            || bundle.Discovery is null || bundle.Resources is null || bundle.GroupMemberships is null || bundle.Limitations is null)
            throw new ArgumentException("Schema 1.0, source label, discovery, resources, memberships and limitations are required.");
        var discovery = bundle.Discovery;
        if (discovery.Accounts is null || discovery.Principals is null || discovery.Roles is null
            || discovery.Policies is null || discovery.Permissions is null || discovery.AuthorizationRecords is null
            || discovery.Accounts.Count != 1 || discovery.CollectedAt == default)
            throw new ArgumentException("A bundle must contain one account and complete explicit collection lists and time.");
        if (discovery.AuthorizationRecords.Count > 20000 || discovery.Permissions.Count > 20000
            || discovery.Principals.Any(item => item is null) || discovery.Accounts.Any(item => item is null)
            || discovery.Roles.Any(item => item is null) || discovery.Policies.Any(item => item is null)
            || discovery.Permissions.Any(item => item is null) || discovery.AuthorizationRecords.Any(item => item is null)
            || bundle.Resources.Any(item => item is null) || bundle.GroupMemberships.Any(item => item is null))
            throw new ArgumentException("Null records or more than 20,000 permissions/authorization statements are not supported.");
        if (discovery.Provider is not (CloudProvider.Azure or CloudProvider.Aws or CloudProvider.Gcp)
            || discovery.Accounts[0].Id == Guid.Empty
            || (CloudProvider)discovery.Accounts[0].CloudProvider != discovery.Provider
            || bundle.Limitations.Any(string.IsNullOrWhiteSpace)
            || discovery.AuthorizationRecords.Any(item => string.IsNullOrWhiteSpace(item.SourceId)
                || string.IsNullOrWhiteSpace(item.SourceType) || !Enum.IsDefined(item.AccessType)
                || !Enum.IsDefined(item.AssignmentType)))
            throw new ArgumentException("Account/provider, source identifiers/types, effects and limitations must be valid.");
        if ((long)discovery.AuthorizationRecords.Count * (discovery.Principals.Count + discovery.Permissions.Count
            + discovery.Roles.Count + discovery.Policies.Count + 1L) > 10000000)
            throw new ArgumentException("The offline bundle exceeds the 10-million normalization/reference work budget; split the export.");
        var normalized = await new EntitlementNormalizationService().NormalizeAsync(discovery, token);
        if (normalized.Errors.Count > 0)
            throw new ArgumentException("Offline records could not be normalized: " + string.Join("; ", normalized.Errors.Take(5)));
        var input = new WhatIfInput(new AccessEvaluationRequest(normalized, bundle.Resources, bundle.GroupMemberships,
            EvaluationTime: discovery.CollectedAt), bundle.SourceLabel, bundle.Limitations);
        Validate(input, discovery.Provider, discovery.Accounts[0].Id);
        return input;
    }

    public async Task<WhatIfReport> SimulateAsync(WhatIfInput input, WhatIfChange change, CancellationToken token = default)
    {
        Validate(input, change.Provider, change.AccountId);
        if (!Enum.IsDefined(change.TargetKind) || change.TargetId == Guid.Empty
            || !Enum.IsDefined(change.Operation)
            || string.IsNullOrWhiteSpace(change.Action) || change.Action.Length > 512
            || change.Action.IndexOfAny(['*', '?', '\r', '\n']) >= 0)
            throw new ArgumentException("An operation, exact action and valid target are required.");
        if (change.Operation == WhatIfOperation.Replace
            && (string.IsNullOrWhiteSpace(change.ReplacementAction) || change.ReplacementAction.Length > 512
                || change.ReplacementAction.IndexOfAny(['*', '?', '\r', '\n']) >= 0
                || ActionsEqual(change.Provider, change.Action, change.ReplacementAction)))
            throw new ArgumentException("Replacement requires a different exact permission action.");
        if (change.Operation != WhatIfOperation.Replace && !string.IsNullOrWhiteSpace(change.ReplacementAction))
            throw new ArgumentException("Replacement action is only valid for a replace operation.");
        var request = input.Evaluation;
        if (request.PrincipalId.HasValue || request.ResourceId.HasValue || request.PermissionId.HasValue
            || request.RoleTrustRelationships?.Count > 0
            || request.Normalized.Entitlements.Any(item => item.AssignmentType == AssignmentType.AssumedRole))
            throw new ArgumentException("Whole-account inputs without prefiltered identities or role-assumption chains are required.");
        var evaluatedAt = request.EvaluationTime ?? DateTime.UtcNow;
        request = request with
        {
            Normalized = request.Normalized with
            {
                Entitlements = request.Normalized.Entitlements.Where(item => item.CreatedAt <= evaluatedAt
                    && (!item.ExpiresAt.HasValue || item.ExpiresAt.Value > evaluatedAt)).ToArray()
            },
            EvaluationTime = evaluatedAt,
            GroupMemberships = request.GroupMemberships.Where(item => item.CreatedAt <= evaluatedAt).ToArray()
        };
        var limitations = input.Limitations.ToList();
        if (request.GroupMemberships.Any(item => (!item.ExpiresAt.HasValue || item.ExpiresAt.Value > evaluatedAt)
            && item.MembershipType is not (MembershipType.Direct or MembershipType.Inherited)))
            limitations.Add("Dynamic or unknown group membership is not evaluated; identity reachability may be incomplete.");
        var accessEvidenceComplete = limitations.Count == 0;
        ValidateTarget(request, change);
        var activeEntitlements = request.Normalized.Entitlements;
        var selected = activeEntitlements.Where(item => IsTarget(item, change)
            && IsPositive(item) && MatchesAction(request, item, change)).ToArray();
        if (change.Operation != WhatIfOperation.Add && selected.Length == 0)
            throw new ArgumentException("No matching positive permission statement exists on the selected target.");
        if (change.Operation == WhatIfOperation.Add && selected.Length > 0)
            throw new ArgumentException("The selected target already contains this permission; use Replace or select another target.");
        if (change.Operation == WhatIfOperation.Add && change.TargetKind != WhatIfTargetKind.Identity)
        {
            selected = activeEntitlements.Where(item => IsTarget(item, change) && IsPositive(item)).ToArray();
            if (selected.Length == 0)
                throw new ArgumentException("The selected role or policy has no positive assignment scope from which to model an added permission.");
        }
        // Expanding AWS/GCP wildcards needs a complete provider action universe; do not replace them with a global deny.
        if (change.Operation != WhatIfOperation.Add && change.Provider != CloudProvider.Azure && selected.Any(item =>
            GetAction(request, item).IndexOfAny(['*', '?']) >= 0))
            throw new NotSupportedException("Changing one action inside an AWS/GCP wildcard requires a complete provider action universe.");
        var principals = request.Normalized.Principals.OrderBy(item => item.Id).ToArray();
        var affectedActions = change.Operation == WhatIfOperation.Replace
            ? new[] { change.Action, change.ReplacementAction! }
            : new[] { change.Action };
        var resources = request.Resources.Where(resource => affectedActions.Any(action =>
                IsResourceCandidate(change with { Action = action }, resource)))
            .OrderBy(item => item.Id).ToArray();
        if ((long)principals.Length * resources.Length > MaximumPairs)
            throw new ArgumentException($"The scenario exceeds {MaximumPairs} identity/resource pairs; split the offline account export.");
        if ((long)principals.Length * resources.Length * request.Normalized.Entitlements.Count > 10000000)
            throw new ArgumentException("The scenario exceeds the 10-million grant/pair work budget; split the offline account export.");
        var additions = change.Operation == WhatIfOperation.Add
            ? CreateAddedEntitlements(request, change, evaluatedAt, null)
            : change.Operation == WhatIfOperation.Replace
                ? CreateAddedEntitlements(request, change, evaluatedAt, selected)
                : Array.Empty<NormalizedEntitlement>();
        var partialWildcardSubtraction = change.Provider == CloudProvider.Azure
            && change.Operation is (WhatIfOperation.Remove or WhatIfOperation.Replace)
            && selected.Any(item => GetAction(request, item).IndexOfAny(['*', '?']) >= 0);
        if (partialWildcardSubtraction)
            limitations.Add("Azure wildcard subtraction is modeled only for the selected action; full risk, toxic-combination and attack-path recalculation is not claimed for the rewritten policy.");
        var removedIds = change.Operation == WhatIfOperation.Add
            ? new HashSet<Guid>()
            : selected.Select(item => item.Id).ToHashSet();
        var before = EnsureActions(request with { EvaluationTime = evaluatedAt }, affectedActions);
        var afterNormalized = before.Normalized with
        {
            Entitlements = before.Normalized.Entitlements.Where(item => !removedIds.Contains(item.Id)).Concat(additions).ToArray()
        };
        var afterActions = change.Operation == WhatIfOperation.Replace
            ? new[] { change.Action, change.ReplacementAction! }
            : new[] { change.Action };
        var after = EnsureActions(before with { Normalized = afterNormalized }, afterActions);
        var rows = new List<WhatIfAccessChange>();
        foreach (var action in affectedActions)
        {
            var actionPermission = FindActionPermission(before, action);
            var contribution = change.Operation switch
            {
                WhatIfOperation.Add when ActionsEqual(change.Provider, action, change.Action) => additions,
                WhatIfOperation.Replace when ActionsEqual(change.Provider, action, change.ReplacementAction!) => additions,
                _ => selected
            };
            if (contribution.Length == 0) continue;
            var candidates = before with { Normalized = before.Normalized with { Entitlements = contribution } };
            foreach (var principal in principals)
            foreach (var resource in resources.Where(item => IsResourceCandidate(change with { Action = action }, item)))
            {
                token.ThrowIfCancellationRequested();
                var previous = await engine.ExplainAsync(before, principal.Id, resource.Id, actionPermission.Id, token);
                var changed = await engine.ExplainAsync(candidates, principal.Id, resource.Id, actionPermission.Id, token);
                var affected = changed.ApplicableEntitlements.Where(item => contribution.Any(source => source.Id == item.Id)).ToArray();
                if (affected.Length == 0) continue;
                var current = await engine.ExplainAsync(EnsureActions(after, new[] { action }),
                    principal.Id, resource.Id, actionPermission.Id, token);
                var complete = previous.EvaluationStatus == EvaluationStatus.FullyEvaluated
                    && current.EvaluationStatus == EvaluationStatus.FullyEvaluated && accessEvidenceComplete
                    && request.Normalized.Errors.Count == 0 && request.Normalized.Warnings.Count == 0
                    && !changed.IsConditional && !affected.Any(item => item.IsConditional);
                var impact = ResolveImpact(change, action, previous, current, complete);
                var alternative = current.Decision == AccessDecision.Allow
                    ? current.ApplicableEntitlements.Where(item => !item.IsExclusion
                        && !affected.Any(candidate => candidate.Id == item.Id)).ToArray()
                    : Array.Empty<NormalizedEntitlement>();
                rows.Add(new WhatIfAccessChange(principal.Id, principal.Name, resource.Id, resource.ResourceId ?? resource.Name,
                    previous.Decision, previous.EvaluationStatus, current.Decision, current.EvaluationStatus, impact,
                    previous.IsInherited || previous.AccessPaths.Any(path => path.Steps.Any(step => step.Type == "Group"))
                        || affected.Any(item => !string.Equals(item.Scope.OriginalScope.TrimEnd('/'),
                            (resource.ResourceId ?? resource.Scope)?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)),
                    change.Operation is (WhatIfOperation.Remove or WhatIfOperation.Replace)
                        && ActionsEqual(change.Provider, action, change.Action)
                        ? affected.Select(item => Origin(before, item)).ToArray()
                        : Array.Empty<WhatIfOrigin>(),
                    alternative.Select(item => Origin(after, item)).ToArray(),
                    previous.AccessPaths, current.AccessPaths,
                    previous.Diagnostics.Concat(current.Diagnostics).Distinct().ToArray())
                {
                    Action = action,
                    AddedOrigins = change.Operation is (WhatIfOperation.Add or WhatIfOperation.Replace)
                        && (change.Operation == WhatIfOperation.Add || ActionsEqual(change.Provider, action, change.ReplacementAction!))
                            ? affected.Select(item => Origin(after, item)).ToArray()
                            : Array.Empty<WhatIfOrigin>()
                });
            }
        }
        var allowedBefore = rows.Count(item => item.Before == AccessDecision.Allow);
        var allowedAfter = rows.Count(item => item.After == AccessDecision.Allow);
        var lost = rows.Count(item => item.Impact == WhatIfImpact.ModeledAccessLost);
        var uncertain = rows.Count(item => item.Impact is WhatIfImpact.PotentialAccessLoss or WhatIfImpact.PotentialAccessGain or WhatIfImpact.Inconclusive);
        decimal? reduction = change.Operation == WhatIfOperation.Remove && uncertain == 0 && limitations.Count == 0 && allowedBefore > 0
            ? Math.Round(100m * lost / allowedBefore, 2) : null;
        var risk = change.Operation == WhatIfOperation.Add
            ? change.Provider == CloudProvider.Azure
                && change.Action.Equals("Microsoft.Storage/storageAccounts/listKeys/action", StringComparison.OrdinalIgnoreCase)
                ? "Adding listKeys may expose storage account keys and enable key-based access outside identity RBAC. This hypothetical grant does not establish that the resource supports the action or that key-based access is enabled."
                : "This hypothetical permission addition may expand access or create toxic combinations and attack paths; provider enforcement and operational need are not verified."
            : change.Provider == CloudProvider.Azure
            && change.Action.Equals("Microsoft.Storage/storageAccounts/listKeys/action", StringComparison.OrdinalIgnoreCase)
            ? "listKeys can expose storage account keys, potentially enabling key-based access outside identity RBAC. Removing this grant does not invalidate existing keys, sessions, SAS tokens or other key-acquisition paths; data-plane exposure is not proven."
            : "The report measures the selected action's modeled identity/resource delta. It does not claim that distinct actions are operationally equivalent.";
        WhatIfSecurityImpact? securityImpact = null;
        if (scenarioAnalyzer is null)
        {
            limitations.Add("The existing risk/toxic-combination/attack-path engines were not available to the simulator; security deltas are not calculated.");
        }
        else if (partialWildcardSubtraction)
        {
            limitations.Add("Security-score comparison is unavailable because the current Azure action-exclusion evaluator cannot preserve unrelated actions in a wildcard statement.");
        }
        else
        {
            var work = (long)principals.Length * request.Resources.Count * before.Normalized.Permissions.Count
                * Math.Max(1, before.Normalized.Entitlements.Count);
            if (work > MaximumSecurityAnalysisWork)
                limitations.Add($"Full risk/toxic-combination/attack-path recalculation exceeded the {MaximumSecurityAnalysisWork:N0} work limit; access deltas are still reported, security scores are unknown.");
            else
            {
                var beforeEvaluation = await engine.EvaluateAsync(before, token);
                var afterEvaluation = await engine.EvaluateAsync(after, token);
                securityImpact = await scenarioAnalyzer.AnalyzeAsync(
                    new(before, beforeEvaluation), new(after, afterEvaluation), token);
                if (!securityImpact.IsComplete)
                    limitations.Add("Risk/toxic-combination/attack-path analysis is incomplete; scores and path counts are partial.");
                limitations.AddRange(securityImpact.Diagnostics);
            }
        }
        return new WhatIfReport(change, input.SourceLabel, evaluatedAt,
            Hash(JsonSerializer.Serialize(input)), rows, allowedBefore, allowedAfter, lost, uncertain, reduction, risk,
            limitations.Concat(new[]
            {
                "Client-supplied offline evidence, not independently attested provider authorization.",
                "Only inventoried resources and principals are included; missing resources are not safe or unaffected.",
                "The exposure percentage measures selected-action modeled pairs, not a total security score or exploit probability.",
                "Existing provider evaluators do not model all SCPs, boundaries, ACLs, PAB, federated sessions or runtime conditions.",
                "Activity/usage data was not included, so workload dependencies and operational impact are unknown.",
                "No cloud policy, local inventory, remediation approval or operational access is changed."
            }).Distinct(StringComparer.Ordinal).ToArray())
        {
            SourceSnapshotAtUtc = input.SourceSnapshotAtUtc,
            SecurityImpact = securityImpact,
            OperationalImpactAssessment = "Known impact is limited to the access pairs listed. No activity/usage evidence was supplied to identify dependent workloads, so operational impact is unknown."
        };
    }

    private static WhatIfImpact ResolveImpact(WhatIfChange change, string action,
        AccessDecisionResult previous, AccessDecisionResult current, bool complete)
    {
        var addsThisAction = change.Operation == WhatIfOperation.Add
            || change.Operation == WhatIfOperation.Replace && ActionsEqual(change.Provider, action, change.ReplacementAction!);
        if (addsThisAction)
        {
            if (previous.Decision == AccessDecision.Allow && current.Decision == AccessDecision.Allow)
                return WhatIfImpact.RetainedByAlternative;
            if (current.Decision == AccessDecision.Allow)
                return complete ? WhatIfImpact.ModeledAccessAdded : WhatIfImpact.PotentialAccessGain;
            if (current.Decision is AccessDecision.Conditional or AccessDecision.Unknown)
                return WhatIfImpact.PotentialAccessGain;
            return WhatIfImpact.Inconclusive;
        }

        return previous.Decision switch
        {
            AccessDecision.Allow when current.Decision == AccessDecision.Allow => WhatIfImpact.RetainedByAlternative,
            AccessDecision.Allow when current.Decision == AccessDecision.NotApplicable && complete => WhatIfImpact.ModeledAccessLost,
            AccessDecision.Deny => WhatIfImpact.AlreadyNotAllowed,
            AccessDecision.Allow or AccessDecision.Conditional => WhatIfImpact.PotentialAccessLoss,
            _ => WhatIfImpact.Inconclusive
        };
    }

    private static void ValidateTarget(AccessEvaluationRequest request, WhatIfChange change)
    {
        var exists = change.TargetKind switch
        {
            WhatIfTargetKind.Role => request.Normalized.Roles.Any(item => item.Id == change.TargetId && item.AccountId == change.AccountId),
            WhatIfTargetKind.Policy => request.Normalized.Policies.Any(item => item.Id == change.TargetId && item.AccountId == change.AccountId),
            WhatIfTargetKind.Identity => request.Normalized.Principals.Any(item => item.Id == change.TargetId
                && item.AccountId == change.AccountId && (CloudProvider)item.CloudProvider == change.Provider),
            _ => false
        };
        if (!exists) throw new ArgumentException("The selected target does not exist in this provider/account snapshot.");
        if (change.TargetKind == WhatIfTargetKind.Identity
            && change.Operation is WhatIfOperation.Remove or WhatIfOperation.Replace
            && request.Normalized.Entitlements.Any(item => item.PrincipalId == change.TargetId
                && item.RoleId is null && item.PolicyId is null && item.AssignmentType == AssignmentType.Direct
                && IsPositive(item) && MatchesAction(request, item, change)))
            return;
        if (change.TargetKind == WhatIfTargetKind.Identity
            && change.Operation is WhatIfOperation.Remove or WhatIfOperation.Replace)
            throw new ArgumentException("Identity-targeted changes can only modify a matching direct permission assignment.");
        if (change.TargetKind == WhatIfTargetKind.Identity && change.Operation == WhatIfOperation.Add
            && string.IsNullOrWhiteSpace(change.ResourceScope))
            throw new ArgumentException("A direct identity permission requires an exact inventoried resource scope.");
    }

    private static bool IsPositive(NormalizedEntitlement item)
        => !item.IsExclusion && (item.AccessType == NormalizedAccessType.Allow
            || item.AccessType == NormalizedAccessType.Conditional && item.ConditionalEffect != AccessType.Deny);

    private static NormalizedEntitlement[] CreateAddedEntitlements(AccessEvaluationRequest request,
        WhatIfChange change, DateTime evaluatedAt, IReadOnlyList<NormalizedEntitlement>? replacementSources)
    {
        var permissionId = SimulationPermissionId(request, change.Provider, change.AccountId,
            change.Operation == WhatIfOperation.Replace ? change.ReplacementAction! : change.Action);
        if (change.TargetKind == WhatIfTargetKind.Identity)
        {
            var matches = request.Resources.Where(resource => resource.AccountId == change.AccountId
                && ResourceMatchesScope(resource, change.ResourceScope!)).ToArray();
            if (matches.Length != 1)
                throw new ArgumentException("Direct identity permission scope must identify exactly one inventoried resource.");
            var resource = matches[0];
            var sourceId = $"simulation:{change.Operation}:{change.TargetId:D}:{change.Action}";
            var entitlementId = GuidUtility.Create(GuidUtility.Namespace,
                $"simulation-entitlement|{sourceId}|{resource.Id:D}|{change.ResourceScope}");
            var evidence = new ProviderEvidence(change.Provider, "WhatIfSimulation", sourceId,
                "Unapplied simulation grant", null, null, change.ResourceScope, "Direct identity grant",
                evaluatedAt, new Dictionary<string, string> { ["simulationOnly"] = "true" });
            return
            [
                new NormalizedEntitlement(entitlementId, change.Provider, change.AccountId, change.TargetId,
                    resource.Id, null, null, permissionId,
                    new(change.Provider, NormalizedScopeType.Resource, change.ResourceScope!, change.ResourceScope,
                        resource.Id.ToString("D"), false),
                    AssignmentType.Direct, NormalizedAccessType.Allow, true, false, false, null,
                    NormalizationStatus.PartiallyNormalized, evidence, evaluatedAt, null,
                    change.Provider == CloudProvider.Aws ? resource.ResourceId : change.ResourceScope)
            ];
        }

        var sources = replacementSources?.ToArray()
            ?? request.Normalized.Entitlements.Where(item => IsTarget(item, change) && IsPositive(item)).ToArray();
        if (!string.IsNullOrWhiteSpace(change.ResourceScope))
            sources = sources.Where(item => ScopeMatches(item.Scope.OriginalScope, change.ResourceScope!)).ToArray();
        if (sources.Length == 0)
            throw new ArgumentException("No assignment scope on the selected role or policy matches the requested change.");

        var uniqueSources = sources.DistinctBy(item => new
        {
            item.PrincipalId, item.ResourceId, item.RoleId, item.PolicyId, item.Scope.OriginalScope,
            item.AssignmentType, item.IsDirect, item.IsInherited, item.IsConditional, item.Condition,
            item.ResourcePattern
        });
        return uniqueSources.Select((source, index) =>
        {
            var action = change.Operation == WhatIfOperation.Replace ? change.ReplacementAction! : change.Action;
            var sourceId = $"simulation:{change.Operation}:{change.TargetId:D}:{index}";
            var evidence = new ProviderEvidence(change.Provider, "WhatIfSimulation", sourceId,
                "Unapplied permission change", null, null, source.Scope.OriginalScope,
                $"{change.Operation} {action}", evaluatedAt,
                new Dictionary<string, string>
                {
                    ["simulationOnly"] = "true",
                    ["sourceEvidenceId"] = source.Evidence.SourceId,
                    ["sourceEvidenceType"] = source.Evidence.SourceType
                });
            var id = GuidUtility.Create(GuidUtility.Namespace,
                $"simulation-entitlement|{change.Provider}|{change.AccountId}|{change.TargetId:D}|{action}|{source.PrincipalId:D}|{source.ResourceId?.ToString("D") ?? "account"}|{source.Scope.OriginalScope}|{source.Evidence.SourceId}");
            return source with
            {
                Id = id,
                PermissionId = permissionId,
                AccessType = NormalizedAccessType.Allow,
                Status = NormalizationStatus.PartiallyNormalized,
                Evidence = evidence,
                CreatedAt = evaluatedAt,
                ExpiresAt = null
            };
        }).ToArray();
    }

    private static AccessEvaluationRequest EnsureActions(AccessEvaluationRequest request, IEnumerable<string> actions)
    {
        var permissions = request.Normalized.Permissions.ToList();
        foreach (var action in actions.Distinct(StringComparer.Ordinal))
        {
            if (permissions.Any(item => ActionsEqual((CloudProvider)request.Normalized.Principals[0].CloudProvider, item.Action ?? item.Name, action)
                && item.AccountId == request.Normalized.Principals[0].AccountId))
                continue;
            var provider = (CloudProvider)request.Normalized.Principals[0].CloudProvider;
            permissions.Add(new CloudPermission
            {
                Id = SimulationPermissionId(request, provider, request.Normalized.Principals[0].AccountId, action),
                AccountId = request.Normalized.Principals[0].AccountId,
                Provider = provider.ToString(),
                Name = action,
                Action = action,
                AccessType = AccessType.Allow,
                Source = "What-if simulation query; not a provider policy"
            });
        }
        return request with { Normalized = request.Normalized with { Permissions = permissions } };
    }

    private static CloudPermission FindActionPermission(AccessEvaluationRequest request, string action)
        => request.Normalized.Permissions.First(item => item.AccountId == request.Normalized.Principals[0].AccountId
            && ActionsEqual((CloudProvider)request.Normalized.Principals[0].CloudProvider, item.Action ?? item.Name, action));

    private static Guid SimulationPermissionId(AccessEvaluationRequest request, CloudProvider provider, Guid accountId, string action)
        => request.Normalized.Permissions.FirstOrDefault(item => item.AccountId == accountId
            && ActionsEqual(provider, item.Action ?? item.Name, action))?.Id
            ?? GuidUtility.Create(GuidUtility.Namespace, $"whatif|{provider}|{accountId}|{action}");

    private static bool ResourceMatchesScope(CloudResource resource, string scope)
        => string.Equals(resource.ResourceId, scope, StringComparison.OrdinalIgnoreCase)
            || string.Equals(resource.ResourceUri, scope, StringComparison.OrdinalIgnoreCase)
            || string.Equals(resource.Scope, scope, StringComparison.OrdinalIgnoreCase);

    private static bool ScopeMatches(string assignmentScope, string requestedScope)
        => string.Equals(assignmentScope.TrimEnd('/'), requestedScope.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static bool ActionsEqual(CloudProvider provider, string first, string second)
        => string.Equals(first, second, provider == CloudProvider.Gcp ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static WhatIfOrigin Origin(AccessEvaluationRequest request, NormalizedEntitlement item)
        => new(item.Evidence.SourceId, item.Evidence.SourceType, item.RoleId, item.PolicyId,
            item.Scope.OriginalScope, GetAction(request, item), item.IsInherited,
            Hash(JsonSerializer.Serialize(item.Evidence)), item.Evidence.OriginalStatement);

    private static bool IsTarget(NormalizedEntitlement item, WhatIfChange change)
        => item.Provider == change.Provider && item.CloudAccountId == change.AccountId
            && (change.TargetKind switch
            {
                WhatIfTargetKind.Role => item.RoleId == change.TargetId,
                WhatIfTargetKind.Policy => item.PolicyId == change.TargetId,
                WhatIfTargetKind.Identity => item.PrincipalId == change.TargetId
                    && item.RoleId is null && item.PolicyId is null && item.AssignmentType == AssignmentType.Direct,
                _ => false
            });
    private static string GetAction(AccessEvaluationRequest request, NormalizedEntitlement item)
        => request.Normalized.Permissions.Single(permission => permission.Id == item.PermissionId).Action
            ?? request.Normalized.Permissions.Single(permission => permission.Id == item.PermissionId).Name;
    private static bool MatchesAction(AccessEvaluationRequest request, NormalizedEntitlement item, WhatIfChange change)
        => EffectiveAccessEngine.ActionMatches(change.Provider, GetAction(request, item), change.Action);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool IsResourceCandidate(WhatIfChange change, CloudResource resource)
    {
        if (change.TargetKind == WhatIfTargetKind.Identity && !string.IsNullOrWhiteSpace(change.ResourceScope))
            return ResourceMatchesScope(resource, change.ResourceScope);
        if (change.Provider != CloudProvider.Azure
            || !change.Action.Equals("Microsoft.Storage/storageAccounts/listKeys/action", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(resource.Type, "Microsoft.Storage/storageAccounts", StringComparison.OrdinalIgnoreCase)) return true;
        const string segment = "/providers/Microsoft.Storage/storageAccounts/";
        var resourceId = resource.ResourceId ?? string.Empty;
        var position = resourceId.IndexOf(segment, StringComparison.OrdinalIgnoreCase);
        return position >= 0 && resourceId[(position + segment.Length)..].TrimEnd('/').Split('/').Length == 1;
    }

    public static void Validate(WhatIfInput input, CloudProvider provider, Guid account)
    {
        if (provider is not (CloudProvider.Azure or CloudProvider.Aws or CloudProvider.Gcp) || account == Guid.Empty)
            throw new ArgumentException("An AWS, Azure or GCP account is required.");
        if (input.Evaluation?.Normalized is not { } normalized || input.Evaluation.Resources is null
            || input.Evaluation.GroupMemberships is null || input.Limitations is null || string.IsNullOrWhiteSpace(input.SourceLabel))
            throw new ArgumentException("The offline evaluation input is incomplete.");
        if (normalized.Principals is null || normalized.Permissions is null || normalized.Entitlements is null
            || normalized.Roles is null || normalized.Policies is null || normalized.Warnings is null || normalized.Errors is null)
            throw new ArgumentException("Explicit normalized inventory and diagnostic lists are required.");
        if (normalized.Principals.Count == 0 || input.Evaluation.Resources.Count == 0
            || normalized.Entitlements.Count > 20000 || normalized.Permissions.Count > 20000)
            throw new ArgumentException("Principal/resource inventory is required; at most 20,000 entitlements and permissions are supported.");
        if (normalized.Principals.Any(item => item is null) || normalized.Permissions.Any(item => item is null)
            || normalized.Entitlements.Any(item => item is null || item.Evidence is null || item.Scope is null)
            || normalized.Roles.Any(item => item is null) || normalized.Policies.Any(item => item is null)
            || input.Evaluation.Resources.Any(item => item is null) || input.Evaluation.GroupMemberships.Any(item => item is null))
            throw new ArgumentException("Null offline records are not supported.");
        if (normalized.Principals.Any(item => item.AccountId != account || (CloudProvider)item.CloudProvider != provider)
            || input.Evaluation.Resources.Any(item => item.AccountId != account)
            || normalized.Permissions.Any(item => item.AccountId != account)
            || normalized.Roles.Any(item => item.AccountId != account)
            || normalized.Policies.Any(item => item.AccountId != account)
            || normalized.Entitlements.Any(item => item.CloudAccountId != account || item.Provider != provider))
            throw new ArgumentException("All records must belong to the same provider/account.");
        if (normalized.Permissions.Any(item => string.IsNullOrWhiteSpace(item.Action ?? item.Name))
            || input.Evaluation.Resources.Any(item => string.IsNullOrWhiteSpace(item.ResourceId ?? item.Scope))
            || input.Limitations.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Explicit permission actions, resource identifiers/scopes and nonempty limitations are required.");
        RequireUnique(normalized.Principals.Select(item => item.Id));
        RequireUnique(input.Evaluation.Resources.Select(item => item.Id));
        RequireUnique(normalized.Permissions.Select(item => item.Id));
        RequireUnique(normalized.Entitlements.Select(item => item.Id));
        RequireUnique(normalized.Roles.Select(item => item.Id));
        RequireUnique(normalized.Policies.Select(item => item.Id));
        var principals = normalized.Principals.Select(item => item.Id).ToHashSet();
        var permissions = normalized.Permissions.Select(item => item.Id).ToHashSet();
        var roles = normalized.Roles.Where(item => item.AccountId == account).Select(item => item.Id).ToHashSet();
        var policies = normalized.Policies.Where(item => item.AccountId == account).Select(item => item.Id).ToHashSet();
        var resources = input.Evaluation.Resources.Select(item => item.Id).ToHashSet();
        if (normalized.Entitlements.Any(item => !principals.Contains(item.PrincipalId) || !permissions.Contains(item.PermissionId)
                || item.RoleId.HasValue && !roles.Contains(item.RoleId.Value)
                || item.PolicyId.HasValue && !policies.Contains(item.PolicyId.Value)
                || item.ResourceId.HasValue && !resources.Contains(item.ResourceId.Value)
                || string.IsNullOrWhiteSpace(item.Evidence.SourceId) || string.IsNullOrWhiteSpace(item.Evidence.SourceType)
                || string.IsNullOrWhiteSpace(item.Scope.OriginalScope))
            || input.Evaluation.GroupMemberships.Any(item => item.CloudAccountId != account
                || !principals.Contains(item.PrincipalId) || !principals.Contains(item.GroupId)))
            throw new ArgumentException("Every grant and group membership must have valid scoped identity, permission and source references.");
    }

    private static void RequireUnique(IEnumerable<Guid> ids)
    {
        var seen = new HashSet<Guid>();
        if (ids.Any(id => id == Guid.Empty || !seen.Add(id)))
            throw new ArgumentException("Empty or duplicate entity identifiers are not valid offline evidence.");
    }
    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new ArgumentException("Duplicate JSON fields are not valid offline evidence.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }
}
