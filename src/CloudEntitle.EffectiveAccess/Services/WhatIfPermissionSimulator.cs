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

public sealed class WhatIfPermissionSimulator(IEffectiveAccessEngine engine)
{
    public const int MaximumJsonBytes = 8 * 1024 * 1024;
    public const int MaximumPairs = 10000;
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
            || string.IsNullOrWhiteSpace(change.Action) || change.Action.Length > 512
            || change.Action.IndexOfAny(['*', '?', '\r', '\n']) >= 0)
            throw new ArgumentException("An exact action and a role or policy target are required.");
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
        var selected = request.Normalized.Entitlements.Where(item => IsTarget(item, change)
            && !item.IsExclusion && (item.AccessType == NormalizedAccessType.Allow
                || item.AccessType == NormalizedAccessType.Conditional && item.ConditionalEffect != AccessType.Deny)
            && MatchesAction(request, item, change)).ToArray();
        if (selected.Length == 0)
            throw new ArgumentException("No matching positive permission statement exists on the selected target.");
        // Expanding a wildcard from an incomplete action universe would change unrelated access.
        if (change.Provider != CloudProvider.Azure && selected.Any(item =>
            GetAction(request, item).IndexOfAny(['*', '?']) >= 0))
            throw new NotSupportedException("Removing one action from an AWS/GCP wildcard needs a complete provider action universe. This simulator does not substitute a global deny.");
        var principals = request.Normalized.Principals.OrderBy(item => item.Id).ToArray();
        var resources = request.Resources.Where(item => IsResourceCandidate(change, item)).OrderBy(item => item.Id).ToArray();
        if ((long)principals.Length * resources.Length > MaximumPairs)
            throw new ArgumentException($"The scenario exceeds {MaximumPairs} identity/resource pairs; split the offline account export.");
        if ((long)principals.Length * resources.Length * request.Normalized.Entitlements.Count > 10000000)
            throw new ArgumentException("The scenario exceeds the 10-million grant/pair work budget; split the offline account export.");
        var actionPermission = new CloudPermission
        {
            Id = GuidUtility.Create(GuidUtility.Namespace, $"whatif|{change.Provider}|{change.AccountId}|{change.Action}"),
            AccountId = change.AccountId, Provider = change.Provider.ToString(), Name = change.Action, Action = change.Action
        };
        var before = request with
        {
            Normalized = request.Normalized with { Permissions = request.Normalized.Permissions.Append(actionPermission).ToArray() },
            EvaluationTime = request.EvaluationTime ?? DateTime.UtcNow
        };
        var selectedIds = selected.Select(item => item.Id).ToHashSet();
        // Evaluate only this exact action. Other wildcard actions and the original input remain untouched.
        var after = before with
        {
            Normalized = before.Normalized with
            {
                Entitlements = before.Normalized.Entitlements.Where(item => !selectedIds.Contains(item.Id)).ToArray()
            }
        };
        var rows = new List<WhatIfAccessChange>();
        var selectedOnly = before with { Normalized = before.Normalized with { Entitlements = selected } };
        foreach (var principal in principals)
        foreach (var resource in resources)
        {
            token.ThrowIfCancellationRequested();
            var previous = await engine.ExplainAsync(before, principal.Id, resource.Id, actionPermission.Id, token);
            var candidate = await engine.ExplainAsync(selectedOnly, principal.Id, resource.Id, actionPermission.Id, token);
            var affected = candidate.ApplicableEntitlements.ToArray();
            if (affected.Length == 0) continue;
            var current = await engine.ExplainAsync(after, principal.Id, resource.Id, actionPermission.Id, token);
            var complete = previous.EvaluationStatus == EvaluationStatus.FullyEvaluated
                && current.EvaluationStatus == EvaluationStatus.FullyEvaluated && limitations.Count == 0
                && request.Normalized.Errors.Count == 0 && request.Normalized.Warnings.Count == 0
                && !candidate.IsConditional && !affected.Any(item => item.IsConditional);
            var impact = previous.Decision switch
            {
                AccessDecision.Allow when current.Decision == AccessDecision.Allow && complete => WhatIfImpact.RetainedByAlternative,
                AccessDecision.Allow when current.Decision == AccessDecision.NotApplicable && complete => WhatIfImpact.ModeledAccessLost,
                AccessDecision.Deny => WhatIfImpact.AlreadyNotAllowed,
                AccessDecision.Allow or AccessDecision.Conditional => WhatIfImpact.PotentialAccessLoss,
                _ => WhatIfImpact.Inconclusive
            };
            var alternative = current.Decision == AccessDecision.Allow
                ? current.ApplicableEntitlements.Where(item => !item.IsExclusion).ToArray()
                : Array.Empty<NormalizedEntitlement>();
            rows.Add(new(principal.Id, principal.Name, resource.Id, resource.ResourceId ?? resource.Name,
                previous.Decision, previous.EvaluationStatus, current.Decision, current.EvaluationStatus, impact,
                previous.IsInherited || previous.AccessPaths.Any(path => path.Steps.Any(step => step.Type == "Group"))
                    || affected.Any(item => !string.Equals(item.Scope.OriginalScope.TrimEnd('/'),
                        (resource.ResourceId ?? resource.Scope)?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)),
                affected.Select(item => Origin(before, item)).ToArray(), alternative.Select(item => Origin(after, item)).ToArray(),
                previous.AccessPaths, current.AccessPaths, previous.Diagnostics.Concat(current.Diagnostics).Distinct().ToArray()));
        }
        var allowedBefore = rows.Count(item => item.Before == AccessDecision.Allow);
        var allowedAfter = rows.Count(item => item.After == AccessDecision.Allow);
        var lost = rows.Count(item => item.Impact == WhatIfImpact.ModeledAccessLost);
        var uncertain = rows.Count(item => item.Impact is WhatIfImpact.PotentialAccessLoss or WhatIfImpact.Inconclusive);
        decimal? reduction = uncertain == 0 && limitations.Count == 0 && allowedBefore > 0
            ? Math.Round(100m * lost / allowedBefore, 2) : null;
        var risk = change.Provider == CloudProvider.Azure
            && change.Action.Equals("Microsoft.Storage/storageAccounts/listKeys/action", StringComparison.OrdinalIgnoreCase)
            ? "listKeys can expose storage account keys, potentially enabling key-based access outside identity RBAC. Removing this grant does not invalidate existing keys, sessions, SAS tokens or other key-acquisition paths; data-plane exposure is not proven."
            : "Removing this action reduces only its modeled identity/resource exposure. Action equivalence, operational dependencies, cached credentials and downstream attack paths are not proven.";
        return new(change, input.SourceLabel, before.EvaluationTime!.Value,
            Hash(JsonSerializer.Serialize(input)), rows, allowedBefore, allowedAfter, lost, uncertain, reduction, risk,
            limitations.Concat(new[]
            {
                "Client-supplied offline evidence, not independently attested provider authorization.",
                "Only inventoried resources and principals are included; missing resources are not safe or unaffected.",
                "The percentage measures selected-action modeled pairs, not a total security score or exploit probability.",
                "Existing provider evaluators do not model all SCPs, boundaries, ACLs, PAB, federated sessions or runtime conditions.",
                "No cloud policy, local inventory, remediation approval or operational access is changed."
            }).ToArray());
    }

    private static WhatIfOrigin Origin(AccessEvaluationRequest request, NormalizedEntitlement item)
        => new(item.Evidence.SourceId, item.Evidence.SourceType, item.RoleId, item.PolicyId,
            item.Scope.OriginalScope, GetAction(request, item), item.IsInherited,
            Hash(JsonSerializer.Serialize(item.Evidence)), item.Evidence.OriginalStatement);

    private static bool IsTarget(NormalizedEntitlement item, WhatIfChange change)
        => item.Provider == change.Provider && item.CloudAccountId == change.AccountId
            && (change.TargetKind == WhatIfTargetKind.Role ? item.RoleId == change.TargetId : item.PolicyId == change.TargetId);
    private static string GetAction(AccessEvaluationRequest request, NormalizedEntitlement item)
        => request.Normalized.Permissions.Single(permission => permission.Id == item.PermissionId).Action
            ?? request.Normalized.Permissions.Single(permission => permission.Id == item.PermissionId).Name;
    private static bool MatchesAction(AccessEvaluationRequest request, NormalizedEntitlement item, WhatIfChange change)
        => EffectiveAccessEngine.ActionMatches(change.Provider, GetAction(request, item), change.Action);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool IsResourceCandidate(WhatIfChange change, CloudResource resource)
    {
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
