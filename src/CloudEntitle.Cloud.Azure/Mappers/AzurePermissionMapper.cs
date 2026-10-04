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
                yield return Create(accountId, roleId, action, "Actions");
            }

            foreach (var action in ReadStrings(block, "notActions"))
            {
                yield return Create(accountId, roleId, action, "NotActions", isExclusion: true);
            }

            foreach (var action in ReadStrings(block, "dataActions"))
            {
                yield return Create(accountId, roleId, action, "DataActions");
            }

            foreach (var action in ReadStrings(block, "notDataActions"))
            {
                yield return Create(accountId, roleId, action, "NotDataActions", isExclusion: true);
            }
        }
    }

    private static CloudPermission Create(Guid accountId, string? roleId, string action, string source, bool isExclusion = false) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        Name = roleId ?? "Azure role permission",
        Action = action,
        Resource = "*",
        Source = $"Azure RBAC {source}",
        AccessType = global::CloudEntitle.Domain.AccessType.Allow,
        IsExclusion = isExclusion
    };

    private static IEnumerable<string> ReadStrings(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array) return Enumerable.Empty<string>();
        return values.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).Where(value => !string.IsNullOrWhiteSpace(value));
    }
}
