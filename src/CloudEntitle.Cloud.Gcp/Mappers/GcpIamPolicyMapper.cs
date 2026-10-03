using System.Text.Json;
using CloudEntitle.Domain;

namespace CloudEntitle.Cloud.Gcp.Mappers;

public sealed record GcpBinding(string Role, string Member, string? ConditionTitle, string? ConditionExpression, string? Scope = null, bool IsInherited = false, DateTime? LastChangedAt = null);

public static class GcpIamPolicyMapper
{
    public static IReadOnlyList<GcpBinding> MapBindings(JsonElement policy, string? scope = null, bool isInherited = false)
    {
        if (!policy.TryGetProperty("bindings", out var bindings) || bindings.ValueKind != JsonValueKind.Array) return Array.Empty<GcpBinding>();
        return bindings.EnumerateArray().SelectMany(binding =>
        {
            var role = binding.TryGetProperty("role", out var roleValue) ? roleValue.GetString() ?? "unknown-role" : "unknown-role";
            var condition = binding.TryGetProperty("condition", out var conditionValue) && conditionValue.ValueKind == JsonValueKind.Object ? conditionValue : default;
            var title = condition.ValueKind == JsonValueKind.Object && condition.TryGetProperty("title", out var titleValue) ? titleValue.GetString() : null;
            var expression = condition.ValueKind == JsonValueKind.Object && condition.TryGetProperty("expression", out var expressionValue) ? expressionValue.GetString() : null;
            return binding.TryGetProperty("members", out var members) && members.ValueKind == JsonValueKind.Array
                ? members.EnumerateArray().Where(member => member.ValueKind == JsonValueKind.String).Select(member => new GcpBinding(role, member.GetString()!, title, expression, scope, isInherited))
                : Enumerable.Empty<GcpBinding>();
        }).ToArray();
    }

    public static CloudPolicy ToPolicy(Guid accountId, GcpBinding binding, string projectId)
    {
        var condition = binding.ConditionExpression == null ? null : JsonSerializer.Serialize(new { title = binding.ConditionTitle, expression = binding.ConditionExpression });
        return new CloudPolicy { Id = Guid.NewGuid(), AccountId = accountId, Name = binding.Role, ExternalId = $"{binding.Scope ?? $"projects/{projectId}"}/roles/{binding.Role}", PolicyType = "GCP IAM Binding", Document = condition ?? "{}", DocumentVersion = "1", IsManaged = binding.Role.StartsWith("roles/", StringComparison.OrdinalIgnoreCase) };
    }

    public static CloudPermission ToPermission(Guid accountId, GcpBinding binding, string projectId) => new()
    {
        Id = Guid.NewGuid(), AccountId = accountId, Name = binding.Role, Provider = "GCP", Service = binding.Role.Split('/').LastOrDefault(), Action = binding.Role, Resource = $"projects/{projectId}", Scope = binding.Scope ?? $"projects/{projectId}", Source = binding.IsInherited ? $"Inherited GCP IAM binding from {binding.Scope}" : binding.ConditionExpression == null ? "GCP IAM binding" : $"GCP IAM binding condition: {binding.ConditionExpression}", AccessType = AccessType.Allow
    };
}
