using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;

namespace CloudEntitle.Integration.Services;

public sealed partial class EntitlementEvidenceCollector(ICloudCommandRunner runner) : IEntitlementEvidenceCollector
{
    private const int MaximumGrants = 20_000;
    private const int MaximumDefinitions = 128;

    public async Task<EntitlementEvidenceSnapshot> CollectAsync(EntitlementEvidenceQuery query, CancellationToken token = default)
    {
        ValidateQuery(query);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var started = DateTimeOffset.UtcNow;
        var grants = new List<ObservedPermissionGrant>();
        var limitations = new List<string>
        {
            "Provider documents observed during this read, not an atomic snapshot or historical validity interval. Changes during/after collection are unknown.",
            "Partial authorization coverage. Resource policies, organization controls, deny policies, boundaries, session policies, conditional evaluation and uncollected group/federation relationships can change effective access. No unused or automatic-removal conclusion is permitted."
        };
        try
        {
            switch (query.Provider)
            {
                case CloudProvider.Azure: await AzureAsync(query, grants, limitations, timeout.Token); break;
                case CloudProvider.Aws: await AwsAsync(query, grants, limitations, timeout.Token); break;
                case CloudProvider.Gcp: await GcpAsync(query, grants, limitations, timeout.Token); break;
            }
            timeout.Token.ThrowIfCancellationRequested();
            return new(Guid.NewGuid(), query.Provider, query.AccountId, started, DateTimeOffset.UtcNow,
                grants.Distinct().ToArray(), limitations);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Permission document collection exceeded five minutes. No evidence snapshot was published."); }
    }

    private async Task<JsonDocument> ReadAsync(string command, IReadOnlyList<string> args, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result = await runner.RunAsync(command, args, token);
        token.ThrowIfCancellationRequested();
        if (!result.IsSuccess) throw new InvalidOperationException($"{command} permission document read failed. Verify CLI connection and required read permissions; no snapshot was published.");
        if (result.StandardOutput.Length > 16 * 1024 * 1024)
            throw new InvalidOperationException("Permission document response exceeds 16 MiB characters. Narrow the account source; no snapshot was published.");
        return JsonDocument.Parse(result.StandardOutput);
    }

    private static void Add(List<ObservedPermissionGrant> grants, ObservedPermissionGrant grant)
    {
        if (grants.Count >= MaximumGrants) throw new InvalidOperationException("Permission evidence exceeds 20,000 statements. No truncated success snapshot was published.");
        grants.Add(grant);
    }

    private static string Required(JsonElement value, string name) => Text(value, name)
        ?? throw new InvalidOperationException($"Permission evidence is missing {name}.");
    private static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.String ? child.GetString() : null;
    private static JsonElement Array(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var child) || child.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"Permission evidence has no valid {name} array.");
        return child;
    }
    private static IEnumerable<string> Strings(JsonElement value, string name, bool optional = false)
    {
        if (!value.TryGetProperty(name, out var child))
            return optional ? [] : throw new InvalidOperationException($"Permission evidence is missing {name}.");
        if (child.ValueKind == JsonValueKind.String) return [child.GetString()!];
        if (child.ValueKind != JsonValueKind.Array || child.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            throw new InvalidOperationException($"Permission evidence has invalid {name} values.");
        return child.EnumerateArray().Select(item => item.GetString()!).ToArray();
    }
    private static bool HasCondition(JsonElement value) => value.TryGetProperty("condition", out var condition) && condition.ValueKind != JsonValueKind.Null
        || value.TryGetProperty("Condition", out condition) && condition.ValueKind != JsonValueKind.Null;
    private static string Hash(JsonElement value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.GetRawText())));

    private static void ValidateQuery(EntitlementEvidenceQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Provider is not (CloudProvider.Azure or CloudProvider.Aws or CloudProvider.Gcp)
            || string.IsNullOrWhiteSpace(query.AccountId) || query.Profile is null
            || query.Provider == CloudProvider.Azure && !Guid.TryParse(query.AccountId, out _)
            || query.Provider == CloudProvider.Aws && !Regex.IsMatch(query.AccountId, @"^\d{12}$")
            || query.Provider == CloudProvider.Gcp && !Regex.IsMatch(query.AccountId, @"^[a-z][a-z0-9-]{4,61}[a-z0-9]$")
            || query.Profile.Length > 128 || query.Profile.Any(char.IsControl))
            throw new ArgumentException("An exact Azure subscription, AWS account or GCP project and safe CLI profile are required.");
    }

    private async Task AzureAsync(EntitlementEvidenceQuery query, List<ObservedPermissionGrant> grants, List<string> limitations, CancellationToken token)
    {
        using var account = await ReadAsync("az", ["account", "show", "--subscription", query.AccountId, "--output", "json"], token);
        if (!Required(account.RootElement, "id").Equals(query.AccountId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Azure CLI subscription does not match the selected binding.");
        using var assignments = await ReadAsync("az", ["role", "assignment", "list", "--all", "--subscription", query.AccountId, "--output", "json"], token);
        if (assignments.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Invalid Azure assignment list.");
        var definitions = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in assignments.RootElement.EnumerateArray())
        {
            var scope = Required(assignment, "scope");
            if (!scope.Equals($"/subscriptions/{query.AccountId}", StringComparison.OrdinalIgnoreCase)
                && !scope.StartsWith($"/subscriptions/{query.AccountId}/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Azure assignment scope is outside the selected subscription.");
            var roleId = Required(assignment, "roleDefinitionId");
            var name = roleId.Split('/').Last();
            if (!Guid.TryParse(name, out _)) throw new InvalidOperationException("Invalid Azure role definition identity.");
            if (!definitions.TryGetValue(roleId, out var definition))
            {
                if (definitions.Count >= MaximumDefinitions) throw new InvalidOperationException("More than 128 role definitions; no partial success snapshot was published.");
                using var result = await ReadAsync("az", ["role", "definition", "list", "--name", name, "--subscription", query.AccountId, "--output", "json"], token);
                if (result.RootElement.ValueKind != JsonValueKind.Array || result.RootElement.GetArrayLength() != 1)
                    throw new InvalidOperationException("Azure role definition did not resolve uniquely.");
                definition = result.RootElement[0].Clone();
                if (!Required(definition, "name").Equals(name, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Azure returned a different role definition.");
                definitions.Add(roleId, definition);
            }
            var blocks = Array(definition, "permissions");
            var blockIndex = 0;
            foreach (var block in blocks.EnumerateArray())
            {
                foreach (var field in new[] { "actions", "notActions", "dataActions", "notDataActions" })
                    foreach (var action in Strings(block, field))
                        Add(grants, new(Required(assignment, "principalId"), Required(assignment, "principalId"), roleId,
                            Required(assignment, "id"), Hash(definition), HashPair(definition, assignment), $"{blockIndex}:{field}",
                            scope, action, scope, "Allow", field is "notActions" or "notDataActions", HasCondition(assignment),
                            field is "dataActions" or "notDataActions"));
                blockIndex++;
            }
        }
        limitations.Add("Azure subscription RBAC assignments and role Actions/NotActions/DataActions/NotDataActions only. Exclusions are not deny assignments. Entra group expansion, management-group inheritance, PIM schedules, deny assignments and ABAC evaluation are not collected.");
    }

    private async Task GcpAsync(EntitlementEvidenceQuery query, List<ObservedPermissionGrant> grants, List<string> limitations, CancellationToken token)
    {
        using var project = await ReadAsync("gcloud", ["projects", "describe", query.AccountId, "--format=json"], token);
        if (Required(project.RootElement, "projectId") != query.AccountId) throw new InvalidOperationException("GCP project differs from the selected binding.");
        using var policy = await ReadAsync("gcloud", ["projects", "get-iam-policy", query.AccountId, "--format=json"], token);
        var rolePermissions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var binding in policy.RootElement.TryGetProperty("bindings", out _) ? Array(policy.RootElement, "bindings").EnumerateArray().ToArray() : [])
        {
            var role = Required(binding, "role");
            if (!rolePermissions.TryGetValue(role, out var definition))
            {
                if (rolePermissions.Count >= MaximumDefinitions) throw new InvalidOperationException("More than 128 GCP roles; no snapshot was published.");
                IReadOnlyList<string> args;
                if (Regex.IsMatch(role, @"^roles/[A-Za-z0-9_.]+$"))
                    args = ["iam", "roles", "describe", role, "--format=json"];
                else
                {
                    var match = Regex.Match(role, @"^(projects|organizations)/([a-zA-Z0-9-]+)/roles/([a-zA-Z0-9_.]+)$");
                    if (!match.Success) throw new InvalidOperationException("Invalid GCP role identity.");
                    args = ["iam", "roles", "describe", match.Groups[3].Value,
                        match.Groups[1].Value == "projects" ? "--project" : "--organization", match.Groups[2].Value, "--format=json"];
                }
                using var result = await ReadAsync("gcloud", args, token);
                definition = result.RootElement.Clone();
                if (Required(definition, "name") != role) throw new InvalidOperationException("GCP returned another role.");
                rolePermissions.Add(role, definition);
            }
            foreach (var member in Strings(binding, "members"))
                foreach (var action in Strings(definition, "includedPermissions"))
                    Add(grants, new(member, member, role, $"projects/{query.AccountId}/iamPolicy",
                        $"{Required(policy.RootElement, "etag")}:{Hash(definition)}", HashPair(policy.RootElement, definition), role, $"projects/{query.AccountId}",
                        action, $"projects/{query.AccountId}", "Allow", false, HasCondition(binding), false));
        }
        limitations.Add("GCP project IAM and included role permissions only. Folder/organization inheritance, resource IAM, groups, deny policies, principal access boundaries, impersonation conditions and audit logging completeness are not established.");
    }

    private async Task AwsAsync(EntitlementEvidenceQuery query, List<ObservedPermissionGrant> grants, List<string> limitations, CancellationToken token)
    {
        IReadOnlyList<string> Args(params string[] args) => (string.IsNullOrWhiteSpace(query.Profile) ? args : new[] { "--profile", query.Profile }.Concat(args))
            .Append("--no-cli-pager").ToArray();
        using var identity = await ReadAsync("aws", Args("sts", "get-caller-identity", "--output", "json"), token);
        if (Required(identity.RootElement, "Account") != query.AccountId) throw new InvalidOperationException("AWS CLI identity differs from the selected binding.");
        using var result = await ReadAsync("aws", Args("iam", "get-account-authorization-details", "--output", "json"), token);
        var root = result.RootElement;
        if (root.TryGetProperty("IsTruncated", out var truncated) && truncated.ValueKind == JsonValueKind.True
            || Text(root, "NextToken") is not null || Text(root, "Marker") is not null)
            throw new InvalidOperationException("AWS authorization result is truncated. No complete-document snapshot was published.");
        var managed = OptionalItems(root, "Policies").ToDictionary(item => Required(item, "Arn"), item => item, StringComparer.Ordinal);
        var users = OptionalItems(root, "UserDetailList").ToArray();
        foreach (var list in new[] { ("UserDetailList", "UserPolicyList"), ("RoleDetailList", "RolePolicyList"), ("GroupDetailList", "GroupPolicyList") })
            foreach (var principal in OptionalItems(root, list.Item1))
            {
                var sourcePrincipal = Required(principal, "Arn");
                if (!sourcePrincipal.StartsWith($"arn:aws:iam::{query.AccountId}:", StringComparison.Ordinal))
                    throw new InvalidOperationException("AWS principal is outside the selected public-cloud account.");
                var targets = list.Item1 != "GroupDetailList" ? new[] { sourcePrincipal } : users.Where(user =>
                    Strings(user, "GroupList", optional: true).Contains(Required(principal, "GroupName"), StringComparer.Ordinal))
                    .Select(user => Required(user, "Arn")).Append(sourcePrincipal).ToArray();
                foreach (var inline in OptionalItems(principal, list.Item2))
                    ParsePolicy(inline.GetProperty("PolicyDocument"), $"{sourcePrincipal}/inline/{Required(inline, "PolicyName")}", "inline-observed", targets, sourcePrincipal);
                foreach (var attached in OptionalItems(principal, "AttachedManagedPolicies"))
                {
                    var arn = Required(attached, "PolicyArn");
                    if (!managed.TryGetValue(arn, out var policy)) throw new InvalidOperationException("Attached AWS policy document is missing.");
                    var version = Required(policy, "DefaultVersionId");
                    var versions = Array(policy, "PolicyVersionList").EnumerateArray().Where(item => Text(item, "VersionId") == version).ToArray();
                    if (versions.Length != 1) throw new InvalidOperationException("AWS default policy version did not resolve uniquely.");
                    ParsePolicy(versions[0].GetProperty("Document"), arn, version, targets, sourcePrincipal);
                }
            }
        limitations.Add("AWS identity inline/managed policies and observed IAM user group membership only. Trust policies, SCP/RCP, permissions boundaries, session/resource policies and cross-account evaluation are not included. NotAction/NotResource statements remain unsupported, never converted to Allow.");

        static IEnumerable<JsonElement> OptionalItems(JsonElement parent, string name)
            => parent.TryGetProperty(name, out _) ? Array(parent, name).EnumerateArray().ToArray() : [];

        void ParsePolicy(JsonElement document, string id, string version, string[] targets, string sourcePrincipal)
        {
            if (document.ValueKind == JsonValueKind.String)
            {
                var serialized = document.GetString()!;
                using var decoded = JsonDocument.Parse(serialized.TrimStart().StartsWith('{') ? serialized : Uri.UnescapeDataString(serialized));
                document = decoded.RootElement.Clone();
            }
            if (document.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("AWS policy document must be decoded JSON.");
            if (!document.TryGetProperty("Statement", out var statements)) throw new InvalidOperationException("AWS policy has no statements.");
            var items = statements.ValueKind == JsonValueKind.Array ? statements.EnumerateArray().ToArray()
                : statements.ValueKind == JsonValueKind.Object ? [statements] : throw new InvalidOperationException("Invalid AWS statements.");
            var index = 0;
            foreach (var statement in items)
            {
                if (statement.TryGetProperty("NotAction", out _) || statement.TryGetProperty("NotResource", out _))
                { index++; continue; }
                var effect = Required(statement, "Effect");
                if (effect is not ("Allow" or "Deny")) throw new InvalidOperationException("Invalid AWS policy effect.");
                foreach (var target in targets)
                    foreach (var action in Strings(statement, "Action"))
                        foreach (var resource in Strings(statement, "Resource"))
                            Add(grants, new(target, sourcePrincipal, sourcePrincipal, id, version, Hash(document),
                                Text(statement, "Sid") ?? index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                $"arn:aws:iam::{query.AccountId}:root", action, resource, effect, false, HasCondition(statement), false));
                index++;
            }
        }
    }

    private static string HashPair(JsonElement left, JsonElement right)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{left.GetRawText()}\n{right.GetRawText()}")));
}
