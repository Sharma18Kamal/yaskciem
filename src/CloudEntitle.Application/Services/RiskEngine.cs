namespace CloudEntitle.Application.Services;

using CloudEntitle.Domain;

public class RiskEngine
{
    private readonly IReadOnlyList<RiskRule> _rules;

    public RiskEngine(IEnumerable<RiskRule>? rules = null)
    {
        _rules = (rules ?? CreateDefaultRules()).Where(rule => rule.IsEnabled).ToArray();
    }

    public IReadOnlyList<RiskRule> Rules => _rules;

    public static IReadOnlyList<RiskRule> DefaultRules => CreateDefaultRules();

    public Task<IReadOnlyList<RiskFinding>> EvaluateAsync(
        IEnumerable<CloudPermission> permissions,
        CloudProviderType providerType,
        CancellationToken cancellationToken = default)
    {
        var findings = new List<RiskFinding>();

        foreach (var permission in permissions ?? Enumerable.Empty<CloudPermission>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var matchingRule = _rules.FirstOrDefault(rule =>
                rule.CloudProvider == providerType && Matches(rule, permission));

            if (matchingRule is null)
            {
                continue;
            }

            findings.Add(new RiskFinding
            {
                Id = Guid.NewGuid(),
                CloudProvider = providerType,
                Severity = matchingRule.Severity,
                Title = matchingRule.Name,
                Description = matchingRule.Description,
                Principal = "detected-principal",
                Resource = permission.Resource ?? "unknown-resource",
                Permission = permission.Name,
                Evidence = permission.Source ?? "Provider scan",
                Recommendation = "Reduce the permission scope to least privilege and remove direct administrative access where possible.",
                CreatedAt = DateTime.UtcNow
            });
        }

        return Task.FromResult<IReadOnlyList<RiskFinding>>(findings);
    }

    private static bool Matches(RiskRule rule, CloudPermission permission)
    {
        var expression = rule.RuleExpression ?? string.Empty;
        var name = permission.Name ?? string.Empty;
        var action = permission.Action ?? string.Empty;

        if (expression.StartsWith("permission-name:", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(name, expression["permission-name:".Length..], StringComparison.OrdinalIgnoreCase);
        }

        if (expression.StartsWith("action-contains:", StringComparison.OrdinalIgnoreCase))
        {
            return action.Contains(expression["action-contains:".Length..], StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static IReadOnlyList<RiskRule> CreateDefaultRules()
        => new[]
        {
            new RiskRule
            {
                CloudProvider = CloudProviderType.Azure,
                Name = "Azure Owner permission",
                Description = "An Azure Owner permission grants full control over the subscription or resource scope.",
                Severity = RiskSeverity.High,
                RuleExpression = "permission-name:Owner"
            },
            new RiskRule
            {
                CloudProvider = CloudProviderType.Aws,
                Name = "AWS AdministratorAccess permission",
                Description = "An AWS AdministratorAccess permission grants broad administrative control.",
                Severity = RiskSeverity.Critical,
                RuleExpression = "permission-name:AdministratorAccess"
            },
            new RiskRule
            {
                CloudProvider = CloudProviderType.Gcp,
                Name = "GCP owner permission",
                Description = "A GCP owner permission grants full control over the project.",
                Severity = RiskSeverity.Critical,
                RuleExpression = "permission-name:roles/owner"
            }
        };
}
