using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Domain.ValueObjects;
using CloudEntitle.RiskEngine.Models;

namespace CloudEntitle.RiskEngine.Rules;

public sealed class StaleCredentialRule : RiskRuleBase
{
    public override string RuleId => "CIEM-022";
    public override string Name => "Stale Identity Credential";
    public override RiskSeverity DefaultSeverity => RiskSeverity.High;
    public override RiskCategory Category => RiskCategory.Identity;

    public override Task<IReadOnlyCollection<RiskFindingModel>> EvaluateAsync(
        RiskRuleContext context,
        CancellationToken cancellationToken = default)
    {
        var maximumAgeDays = Math.Max(1, context.Options.MaxCredentialAgeDays);
        var staleBeforeUtc = context.EvaluatedAt.ToUniversalTime().AddDays(-maximumAgeDays);
        var findings = new List<RiskFindingModel>();

        foreach (var principal in context.Request.Principals.Where(item => item.CredentialInventoryComplete == true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expired = principal.CredentialExpiresAtUtc.HasValue
                && principal.CredentialExpiresAtUtc.Value.ToUniversalTime() <= context.EvaluatedAt.ToUniversalTime();
            var stale = principal.CredentialLastRotatedAtUtc.HasValue
                && principal.CredentialLastRotatedAtUtc.Value.ToUniversalTime() <= staleBeforeUtc;
            if (!expired && !stale) continue;

            var provider = Provider(principal.CloudProvider);
            var rotation = principal.CredentialLastRotatedAtUtc?.ToUniversalTime();
            var expiry = principal.CredentialExpiresAtUtc?.ToUniversalTime();
            var title = expired ? "Expired identity credential" : "Stale identity credential";
            var evidence = $"CredentialInventoryComplete=true; LastRotatedAt={rotation?.ToString("O") ?? "Unknown"}; ExpiresAt={expiry?.ToString("O") ?? "Unknown"}; MaximumAgeDays={maximumAgeDays}; Source={principal.CredentialEvidenceSource ?? "not reported"}";
            var fingerprint = $"{RuleId}|{provider}|{principal.AccountId}|{principal.PrincipalId ?? principal.Id.ToString("D")}|{rotation:O}|{expiry:O}";
            findings.Add(new RiskFindingModel
            {
                Id = Stable(fingerprint),
                RuleId = Stable(RuleId),
                CloudProvider = provider,
                CloudAccountId = principal.AccountId,
                PrincipalId = principal.Id,
                Severity = expired ? RiskSeverity.High : RiskSeverity.Medium,
                Status = FindingStatus.Open,
                Title = title,
                Description = expired
                    ? "The complete credential inventory reports a credential that has expired."
                    : $"The complete credential inventory reports a credential that has not been rotated within {maximumAgeDays} days.",
                Evidence = new RiskEvidence($"Identity={principal.Name}; Type={principal.IdentityType}; {evidence}"),
                Recommendation = "Review credential owners and workload dependencies, then rotate or remove the credential through the provider workflow.",
                CreatedAt = context.EvaluatedAt,
                UpdatedAt = context.EvaluatedAt,
                DetectedAt = context.EvaluatedAt,
                LastEvaluatedAt = context.EvaluatedAt,
                EvaluationState = RiskEvaluationState.Confirmed,
                Category = Category,
                Fingerprint = fingerprint
            });
        }

        return Task.FromResult<IReadOnlyCollection<RiskFindingModel>>(findings);
    }
}
