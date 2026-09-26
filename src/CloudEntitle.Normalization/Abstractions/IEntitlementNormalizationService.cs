using CloudEntitle.Normalization.Models;
using CloudEntitle.Domain;

namespace CloudEntitle.Normalization.Abstractions;

public interface IEntitlementNormalizer
{
    CloudProvider Provider { get; }
    Task<NormalizationResult> NormalizeAsync(ProviderDiscoveryResult source, CancellationToken cancellationToken = default);
}

public interface IEntitlementNormalizationService
{
    Task<NormalizationResult> NormalizeAsync(ProviderDiscoveryResult source, CancellationToken cancellationToken = default);
}

public interface INormalizationValidator
{
    IReadOnlyList<string> Validate(ProviderDiscoveryResult source, NormalizedEntitlement entitlement);
}
