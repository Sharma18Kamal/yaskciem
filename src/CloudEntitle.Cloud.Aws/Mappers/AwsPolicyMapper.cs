using System.Text.Json;
using CloudEntitle.Domain;

namespace CloudEntitle.Cloud.Aws.Mappers;

public static class AwsPolicyMapper
{
    public static IEnumerable<CloudPermission> MapPolicy(Guid accountId, string policyName, string? policyArn, string document)
    {
        using var json = JsonDocument.Parse(document);
        var statements = json.RootElement.TryGetProperty("Statement", out var value)
            ? value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Cast<JsonElement>() : new[] { value }
            : Enumerable.Empty<JsonElement>();
        foreach (var statement in statements)
        {
            var effect = statement.TryGetProperty("Effect", out var effectValue) ? effectValue.GetString() : "Allow";
            foreach (var action in ReadStringValues(statement, "Action"))
            {
                foreach (var resource in ReadStringValues(statement, "Resource").DefaultIfEmpty("*"))
                {
                    yield return Create(accountId, policyName, policyArn, action, resource, effect, "Action");
                }
            }

            foreach (var action in ReadStringValues(statement, "NotAction"))
            {
                foreach (var resource in ReadStringValues(statement, "NotResource").DefaultIfEmpty("*"))
                {
                    yield return Create(accountId, policyName, policyArn, action, resource, effect, "NotAction");
                }
            }
        }
    }

    public static IReadOnlyList<string> MapTrustPrincipals(string trustPolicy)
    {
        using var json = JsonDocument.Parse(trustPolicy);
        return json.RootElement.TryGetProperty("Statement", out var value)
            ? (value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Cast<JsonElement>() : new[] { value }).SelectMany(statement => ReadStringValues(statement.TryGetProperty("Principal", out var principal) ? principal : default, "AWS")).ToArray()
            : Array.Empty<string>();
    }

    private static CloudPermission Create(Guid accountId, string policyName, string? policyArn, string action, string resource, string? effect, string source)
    {
        var service = action.Contains(':') ? action[..action.IndexOf(':')] : null;
        return new CloudPermission
        {
            Id = Guid.NewGuid(), AccountId = accountId, Name = policyName, Provider = "AWS", Service = service,
            Action = action, Resource = resource, Source = $"AWS IAM {source} ({policyArn ?? "inline"})",
            AccessType = string.Equals(effect, "Deny", StringComparison.OrdinalIgnoreCase) ? AccessType.Deny : AccessType.Allow
        };
    }

    private static IEnumerable<string> ReadStringValues(JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Array) return element.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).Where(value => !string.IsNullOrWhiteSpace(value));
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var nested)) return ReadStringValues(nested, property);
        if (element.ValueKind == JsonValueKind.String) return new[] { element.GetString()! };
        return Enumerable.Empty<string>();
    }
}
