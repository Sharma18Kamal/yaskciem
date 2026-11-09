namespace CloudEntitle.Integration.Contracts;

/// <summary>Server-evaluated entitlement for the authenticated Microsoft Entra tenant.</summary>
public sealed record SubscriptionStatus(
    Guid TenantId,
    string Status,
    DateTimeOffset EvaluatedAtUtc,
    DateTimeOffset? TrialStartedAtUtc,
    DateTimeOffset? TrialExpiresAtUtc,
    DateTimeOffset? LicensedUntilUtc,
    bool CanUseLicensedFeatures);
