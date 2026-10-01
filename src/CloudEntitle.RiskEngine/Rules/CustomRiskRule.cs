using CloudEntitle.Domain;
using CloudEntitle.RiskEngine.Models;

namespace CloudEntitle.RiskEngine.Rules;

public sealed class CustomRiskRule : RiskRuleBase
{
    private readonly CustomRiskRuleDefinition _definition;

    public CustomRiskRule(CustomRiskRuleDefinition definition) => _definition = definition;
    public override string RuleId => _definition.RuleId;
    public override string Name => _definition.Name;
    public override RiskSeverity DefaultSeverity => _definition.Severity;
    public override RiskCategory Category => _definition.Category;

    public override Task<IReadOnlyCollection<Domain.Entities.RiskFindingModel>> EvaluateAsync(RiskRuleContext context, CancellationToken cancellationToken = default)
    {
        var findings = new List<Domain.Entities.RiskFindingModel>();
        foreach (var access in Allowed(context))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_definition.Provider.HasValue && access.Provider != _definition.Provider.Value) continue;
            var principal = Principal(context, access.PrincipalId);
            var permission = Permission(context, access.PermissionId);
            var resource = Resource(context, access.ResourceId);
            var entitlement = Entitlement(context, access);
            if (_definition.PermissionName != null && !string.Equals(permission?.Name, _definition.PermissionName, StringComparison.OrdinalIgnoreCase)) continue;
            if (_definition.ActionContains != null && !(permission?.Action?.Contains(_definition.ActionContains, StringComparison.OrdinalIgnoreCase) ?? false)) continue;
            if (_definition.ResourceContains != null && !(permission?.Resource?.Contains(_definition.ResourceContains, StringComparison.OrdinalIgnoreCase) ?? false)) continue;
            if (_definition.ScopeContains != null && !(entitlement?.Scope.OriginalScope.Contains(_definition.ScopeContains, StringComparison.OrdinalIgnoreCase) ?? false)) continue;
            if (_definition.RequiresPrivilegedAccess.HasValue && access.IsPrivileged != _definition.RequiresPrivilegedAccess.Value) continue;
            if (_definition.RequiresExternalIdentity.HasValue && (principal?.IsExternal ?? false) != _definition.RequiresExternalIdentity.Value) continue;
            if (_definition.RequiresServiceIdentity.HasValue && IsService(principal?.IdentityType) != _definition.RequiresServiceIdentity.Value) continue;
            var finding = Finding(context, access, _definition.EvaluationState, _definition.Name, _definition.Description, $"Custom policy matched: {_definition.RuleId}; Permission={permission?.Name}; Resource={resource?.Name}; Scope={entitlement?.Scope.OriginalScope}");
            finding.Recommendation = _definition.Recommendation;
            findings.Add(finding);
        }
        return Task.FromResult<IReadOnlyCollection<Domain.Entities.RiskFindingModel>>(findings);
    }

    private static bool IsService(IdentityType? type) => type is IdentityType.ServiceAccount or IdentityType.ServicePrincipal or IdentityType.ManagedIdentity or IdentityType.Application;
}
