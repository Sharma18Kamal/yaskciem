using System.Text.Json;
using CloudEntitle.Domain;

namespace CloudEntitle.Cloud.Azure.Mappers;

public static class AzurePermissionMapper
{
    public static IEnumerable<CloudPermission> MapRolePermissions(Guid accountId, JsonElement roleDefinition)
    {
        var roleId = roleDefinition.TryGetProperty("name", out var name) ? name.GetString() : null;
        var properties = roleDefinition.TryGetProperty("properties", out var value) ? value : roleDefinition;
        var permissionBlocks = properties.TryGetProperty("permissions", out var permissions) && permissions.ValueKind == JsonValueKind.Array
            ? permissions.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

        foreach (var block in permissionBlocks)
        {
            foreach (var action in ReadStrings(block, "actions"))
            {
                yield return Create(accountId, roleId, action, "Allow", "Actions");
            }

            foreach (var action in ReadStrings(block, "notActions"))
            {
                yield return Create(accountId, roleId, action, "Deny", "NotActions");
            }

            foreach (var action in ReadStrings(block, "dataActions"))
            {
                yield return Create(accountId, roleId, action, "Allow", "DataActions");
            }

            foreach (var action in ReadStrings(block, "notDataActions"))
            {
                yield return Create(accountId, roleId, action, "Deny", "NotDataActions");
            }
        }
    }

    private static CloudPermission Create(Guid accountId, string? roleId, string action, string accessType, string source) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        Name = roleId ?? "Azure role permission",
        Action = action,
        Resource = "*",
        Source = $"Azure RBAC {source}",
        AccessType = accessType == "Allow" ? global::CloudEntitle.Domain.AccessType.Allow : global::CloudEntitle.Domain.AccessType.Deny
    };

    private static IEnumerable<string> ReadStrings(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array) return Enumerable.Empty<string>();
        return values.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).Where(value => !string.IsNullOrWhiteSpace(value));
    }
}
