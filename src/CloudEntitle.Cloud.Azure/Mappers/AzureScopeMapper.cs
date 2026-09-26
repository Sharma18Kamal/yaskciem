using CloudEntitle.Domain.ValueObjects;

namespace CloudEntitle.Cloud.Azure.Mappers;

public static class AzureScopeMapper
{
    public static CloudScope Map(string scope) => new(scope);

    public static string GetScopeType(string scope)
    {
        var segments = scope.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return "Tenant";
        if (scope.Contains("/managementGroups/", StringComparison.OrdinalIgnoreCase)) return "ManagementGroup";
        if (scope.Contains("/subscriptions/", StringComparison.OrdinalIgnoreCase) && segments.Length == 2) return "Subscription";
        if (scope.Contains("/resourceGroups/", StringComparison.OrdinalIgnoreCase)) return "ResourceGroup";
        return "Resource";
    }
}
