using System.Text.Json;
using System.Text.Json.Serialization;
using CloudEntitle.Domain;
using CloudEntitle.RiskEngine.Models;

namespace CloudEntitle.RiskEngine.Services;

public sealed class PolicyAsCodeRuleLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };

    public IReadOnlyList<CustomRiskRuleDefinition> Load(string json)
    {
        List<CustomRiskRuleDefinition>? definitions;
        try
        {
            definitions = JsonSerializer.Deserialize<List<CustomRiskRuleDefinition>>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Policy-as-code rule document is invalid.", exception);
        }
        if (definitions == null) throw new InvalidDataException("Policy-as-code rule document is empty or invalid.");
        Validate(definitions);
        return definitions;
    }

    public IReadOnlyList<CustomRiskRuleDefinition> LoadFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Policy-as-code rule file was not found.", path);
        return Load(File.ReadAllText(path));
    }

    private static void Validate(IEnumerable<CustomRiskRuleDefinition> definitions)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            if (string.IsNullOrWhiteSpace(definition.RuleId) || !definition.RuleId.StartsWith("CUSTOM-", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Custom rule IDs must be non-empty and start with CUSTOM-.");
            if (!seen.Add(definition.RuleId)) throw new InvalidDataException($"Duplicate custom rule ID: {definition.RuleId}");
            if (string.IsNullOrWhiteSpace(definition.Name)) throw new InvalidDataException($"Custom rule {definition.RuleId} must have a name.");
            if (definition.PermissionName == null && definition.ActionContains == null && definition.ResourceContains == null && definition.ScopeContains == null && definition.RequiresPrivilegedAccess == null && definition.RequiresExternalIdentity == null && definition.RequiresServiceIdentity == null) throw new InvalidDataException($"Custom rule {definition.RuleId} has no match criteria.");
        }
    }
}
