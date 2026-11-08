using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudEntitle.Integration.Contracts;

namespace CloudEntitle.Integration.Services;

public sealed record EffectiveDecisionEvaluation(
    string Decision, string Coverage, string ContextSha256, IReadOnlyList<string> PolicyVersions,
    IReadOnlyList<string> Blockers);

/// <summary>Fail-closed interpretation of the public GCP v3beta allow/deny/PAB response schema.</summary>
public static class GcpEffectiveDecisionEvaluator
{
    public const string VerificationProfile = "gcp-policy-troubleshooter-v3beta-project-unconditional-v1";
    public const string Endpoint = "https://policytroubleshooter.googleapis.com/v3beta/iam:troubleshoot";

    public static EffectiveDecisionEvaluation Evaluate(string responseJson, CaptureEffectiveDecisionRequest request,
        IReadOnlyList<string>? verifiedResourceHierarchy = null)
    {
        using var document = JsonDocument.Parse(responseJson, new JsonDocumentOptions { MaxDepth = 64 });
        var root = document.RootElement;
        var blockers = new HashSet<string>(StringComparer.Ordinal);
        var versions = new SortedSet<string>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("A native JSON object is required.");
        foreach (var field in root.EnumerateObject())
            if (field.Name is not ("overallAccessState" or "accessTuple" or "allowPolicyExplanation"
                or "denyPolicyExplanation" or "pabPolicyExplanation"))
                blockers.Add("provider_response_schema_changed");
        RejectAmbiguity(root, blockers);
        var state = Text(root, "overallAccessState");
        var decision = state switch { "CAN_ACCESS" => "GRANTED", "CANNOT_ACCESS" => "NOT_GRANTED", _ => "UNKNOWN" };
        if (decision == "UNKNOWN") blockers.Add("provider_overall_decision_unknown");
        if (!root.TryGetProperty("accessTuple", out var tuple) || tuple.ValueKind != JsonValueKind.Object
            || Text(tuple, "principal") != request.Principal || Text(tuple, "permission") != request.Permission
            || Text(tuple, "fullResourceName") != request.FullResourceName
            || Text(tuple, "permissionFqdn") != "cloudresourcemanager.googleapis.com/projects.setIamPolicy")
            blockers.Add("provider_tuple_mismatch");
        var context = "{\"effectiveTags\":[]}";
        if (tuple.ValueKind == JsonValueKind.Object && tuple.TryGetProperty("conditionContext", out var conditionContext))
        {
            if (conditionContext.ValueKind != JsonValueKind.Object) blockers.Add("invalid_provider_context");
            else
            {
                if (conditionContext.TryGetProperty("effectiveTags", out var tags))
                {
                    if (tags.ValueKind != JsonValueKind.Array) blockers.Add("invalid_effective_tags");
                    else context = "{\"effectiveTags\":[" + string.Join(",",
                        tags.EnumerateArray().Select(CanonicalJson).Order(StringComparer.Ordinal)) + "]}";
                }
                foreach (var property in conditionContext.EnumerateObject())
                    if (property.Name != "effectiveTags"
                        && !(property.Name is "resource" or "destination" or "request"
                            && property.Value.ValueKind == JsonValueKind.Object && !property.Value.EnumerateObject().Any()))
                        blockers.Add("unsupported_nonempty_condition_context");
            }
        }

        JsonElement allows = default, denies = default;
        if (!Object(root, "allowPolicyExplanation", out var allow)
            || Text(allow, "allowAccessState") is not ("ALLOW_ACCESS_STATE_GRANTED" or "ALLOW_ACCESS_STATE_NOT_GRANTED")
            || !Array(allow, "explainedPolicies", out allows) || allows.GetArrayLength() == 0)
            blockers.Add("allow_policy_coverage_incomplete");
        else foreach (var policy in allows.EnumerateArray())
        {
            var resource = Text(policy, "fullResourceName");
            if (Text(policy, "allowAccessState") is not ("ALLOW_ACCESS_STATE_GRANTED" or "ALLOW_ACCESS_STATE_NOT_GRANTED"))
                blockers.Add("allow_policy_state_missing");
            if (resource.Length == 0 || !Object(policy, "policy", out var body)
                || Text(body, "etag").Length == 0)
                blockers.Add("allow_policy_version_or_visibility_missing");
            else versions.Add($"allow|{resource}|{Text(body, "etag")}");
            if (Object(policy, "policy", out var visible) && Array(visible, "bindings", out var bindings)
                && bindings.GetArrayLength() > 0
                && (!Array(policy, "bindingExplanations", out var explanations)
                    || explanations.GetArrayLength() != bindings.GetArrayLength()))
                blockers.Add("allow_binding_coverage_incomplete");
            if (Array(policy, "bindingExplanations", out var evaluatedBindings))
                foreach (var binding in evaluatedBindings.EnumerateArray())
                    if (Text(binding, "allowAccessState") is not ("ALLOW_ACCESS_STATE_GRANTED" or "ALLOW_ACCESS_STATE_NOT_GRANTED")
                        || Text(binding, "rolePermission") is not ("ROLE_PERMISSION_INCLUDED" or "ROLE_PERMISSION_NOT_INCLUDED")
                        || !Object(binding, "combinedMembership", out var membership)
                        || Text(membership, "membership") is not ("MEMBERSHIP_MATCHED" or "MEMBERSHIP_NOT_MATCHED"))
                        blockers.Add("allow_binding_evaluation_incomplete");
        }

        if (!Object(root, "denyPolicyExplanation", out var deny)
            || Text(deny, "denyAccessState") is not ("DENY_ACCESS_STATE_DENIED" or "DENY_ACCESS_STATE_NOT_DENIED")
            || !Array(deny, "explainedResources", out denies) || denies.GetArrayLength() == 0)
            blockers.Add("deny_policy_coverage_incomplete");
        else foreach (var resource in denies.EnumerateArray())
        {
            var name = Text(resource, "fullResourceName");
            if (Text(resource, "denyAccessState") is not ("DENY_ACCESS_STATE_DENIED" or "DENY_ACCESS_STATE_NOT_DENIED"))
                blockers.Add("deny_resource_state_missing");
            if (name.Length == 0 || !Array(resource, "explainedPolicies", out var policies))
                blockers.Add("deny_resource_visibility_missing");
            else
            {
                versions.Add($"deny-resource|{name}");
                foreach (var policy in policies.EnumerateArray())
                {
                    AddVersion(policy, "policy", "deny", versions, blockers);
                    if (Text(policy, "denyAccessState") is not ("DENY_ACCESS_STATE_DENIED" or "DENY_ACCESS_STATE_NOT_DENIED"))
                        blockers.Add("deny_policy_state_missing");
                    if (!Object(policy, "policy", out var body) || !Array(body, "rules", out var rules)
                        || !Array(policy, "ruleExplanations", out var rulesEvaluated)
                        || rules.GetArrayLength() != rulesEvaluated.GetArrayLength())
                        blockers.Add("deny_rule_coverage_incomplete");
                    else foreach (var rule in rulesEvaluated.EnumerateArray())
                        if (Text(rule, "denyAccessState") is not ("DENY_ACCESS_STATE_DENIED" or "DENY_ACCESS_STATE_NOT_DENIED"))
                            blockers.Add("deny_rule_state_missing");
                }
            }
        }

        if (!Object(root, "pabPolicyExplanation", out var pab)
            || Text(pab, "principalAccessBoundaryAccessState") is not
                ("PAB_ACCESS_STATE_ALLOWED" or "PAB_ACCESS_STATE_NOT_ALLOWED" or "PAB_ACCESS_STATE_NOT_ENFORCED")
            || !Array(pab, "explainedBindingsAndPolicies", out var pabs))
            blockers.Add("principal_access_boundary_coverage_incomplete");
        else
        {
            if (pabs.GetArrayLength() == 0)
            {
                versions.Add("pab-none");
                if (Text(pab, "principalAccessBoundaryAccessState") != "PAB_ACCESS_STATE_NOT_ENFORCED")
                    blockers.Add("pab_policy_coverage_incomplete");
            }
            foreach (var pair in pabs.EnumerateArray())
            {
                if (Text(pair, "bindingAndPolicyAccessState") is not
                    ("PAB_ACCESS_STATE_ALLOWED" or "PAB_ACCESS_STATE_NOT_ALLOWED" or "PAB_ACCESS_STATE_NOT_ENFORCED"))
                    blockers.Add("pab_pair_state_missing");
                if (!Object(pair, "explainedPolicyBinding", out var binding))
                    blockers.Add("pab_binding_visibility_missing");
                else
                {
                    AddVersion(binding, "policyBinding", "pab-binding", versions, blockers);
                    if (Text(binding, "policyBindingState") is not ("POLICY_BINDING_STATE_ENFORCED" or "POLICY_BINDING_STATE_NOT_ENFORCED"))
                        blockers.Add("pab_binding_state_missing");
                }
                if (!Object(pair, "explainedPolicy", out var policy))
                    blockers.Add("pab_policy_visibility_missing");
                else
                {
                    AddVersion(policy, "policy", "pab", versions, blockers);
                    if (Text(policy, "policyAccessState") is not
                        ("PAB_ACCESS_STATE_ALLOWED" or "PAB_ACCESS_STATE_NOT_ALLOWED" or "PAB_ACCESS_STATE_NOT_ENFORCED")
                        || !Object(policy, "policyVersion", out var enforcement)
                        || Text(enforcement, "enforcementState") is not
                            ("PAB_POLICY_ENFORCEMENT_STATE_ENFORCED" or "PAB_POLICY_ENFORCEMENT_STATE_NOT_ENFORCED")
                        || !enforcement.TryGetProperty("version", out var version) || !version.TryGetInt32(out var number) || number <= 0)
                        blockers.Add("pab_enforcement_version_missing");
                    else if (Object(policy, "policy", out var versionedPolicy))
                        versions.Add($"pab-enforcementversion|{Text(versionedPolicy, "name")}|{number}");
                    if (!Object(policy, "policy", out var body) || !Object(body, "details", out var details)
                        || !Array(details, "rules", out var rules) || !Array(policy, "explainedRules", out var explained)
                        || rules.GetArrayLength() != explained.GetArrayLength())
                        blockers.Add("pab_rule_coverage_incomplete");
                    else foreach (var rule in explained.EnumerateArray())
                        if (Text(rule, "ruleAccessState") is not
                            ("PAB_ACCESS_STATE_ALLOWED" or "PAB_ACCESS_STATE_NOT_ALLOWED" or "PAB_ACCESS_STATE_NOT_ENFORCED")
                            || Text(rule, "combinedResourceInclusionState") is not
                                ("RESOURCE_INCLUSION_STATE_INCLUDED" or "RESOURCE_INCLUSION_STATE_NOT_INCLUDED"))
                            blockers.Add("pab_rule_state_missing");
                }
            }
        }
        if (verifiedResourceHierarchy is null || verifiedResourceHierarchy.Count == 0)
            blockers.Add("resource_hierarchy_unverified");
        else foreach (var ancestor in verifiedResourceHierarchy)
        {
            if (allows.ValueKind != JsonValueKind.Array
                || !allows.EnumerateArray().Any(p => Text(p, "fullResourceName") == ancestor))
                blockers.Add("allow_ancestor_coverage_incomplete");
            if (denies.ValueKind != JsonValueKind.Array
                || !denies.EnumerateArray().Any(p => Text(p, "fullResourceName") == ancestor))
                blockers.Add("deny_ancestor_coverage_incomplete");
        }
        if (decision == "GRANTED" && (Text(allow, "allowAccessState") != "ALLOW_ACCESS_STATE_GRANTED"
            || Text(deny, "denyAccessState") != "DENY_ACCESS_STATE_NOT_DENIED"
            || Text(pab, "principalAccessBoundaryAccessState") is not
                ("PAB_ACCESS_STATE_ALLOWED" or "PAB_ACCESS_STATE_NOT_ENFORCED")))
            blockers.Add("provider_policy_decision_inconsistent");
        if (decision == "NOT_GRANTED" && Text(allow, "allowAccessState") == "ALLOW_ACCESS_STATE_GRANTED"
            && Text(deny, "denyAccessState") == "DENY_ACCESS_STATE_NOT_DENIED"
            && Text(pab, "principalAccessBoundaryAccessState") is "PAB_ACCESS_STATE_ALLOWED" or "PAB_ACCESS_STATE_NOT_ENFORCED")
            blockers.Add("provider_policy_decision_inconsistent");
        return new(decision, blockers.Count == 0 ? "CompleteVerified" : "Partial",
            Sha256(context), versions.ToArray(), blockers.Order(StringComparer.Ordinal).ToArray());
    }

    private static void AddVersion(JsonElement owner, string field, string kind, SortedSet<string> versions,
        HashSet<string> blockers)
    {
        if (!Object(owner, field, out var policy) || Text(policy, "name").Length == 0 || Text(policy, "etag").Length == 0)
            blockers.Add(kind + "_policy_version_or_visibility_missing");
        else versions.Add($"{kind}|{Text(policy, "name")}|{Text(policy, "etag")}");
    }

    private static void RejectAmbiguity(JsonElement element, HashSet<string> blockers)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) blockers.Add("duplicate_provider_json_property");
                if (property.Name is "condition" or "conditionExplanation" or "denialCondition" or "bindingCondition")
                    blockers.Add("conditional_policy_not_supported");
                if (property.Name is "error" or "errors" && property.Value.ValueKind != JsonValueKind.Null
                    && !(property.Value.ValueKind == JsonValueKind.Array && property.Value.GetArrayLength() == 0))
                    blockers.Add("provider_evaluation_error");
                if (property.Value.ValueKind == JsonValueKind.String
                    && (property.Name.EndsWith("State", StringComparison.Ordinal) || property.Name is "membership" or "rolePermission")
                    && (property.Value.GetString()!.Contains("UNKNOWN", StringComparison.Ordinal)
                        || property.Value.GetString()!.Contains("UNSPECIFIED", StringComparison.Ordinal)))
                    blockers.Add("provider_component_unknown");
                RejectAmbiguity(property.Value, blockers);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectAmbiguity(item, blockers);
    }

    public static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string CanonicalJson(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Serialize(p.Name) + ":" + CanonicalJson(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(CanonicalJson)) + "]",
        _ => value.GetRawText()
    };
    private static string Text(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    private static bool Object(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;
    }
    private static bool Array(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Array;
    }
}
