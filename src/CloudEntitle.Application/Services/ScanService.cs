namespace CloudEntitle.Application.Services;

using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;

public class ScanService
{
    private readonly IEnumerable<ICloudProvider> _providers;
    private readonly RiskEngine _riskEngine;

    public ScanService(IEnumerable<ICloudProvider> providers, RiskEngine? riskEngine = null)
    {
        _providers = providers;
        _riskEngine = riskEngine ?? new RiskEngine();
    }

    public IReadOnlyList<ICloudProvider> Providers => _providers.ToArray();

    public async Task<IReadOnlyList<RiskFinding>> RunScanAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var findings = new List<RiskFinding>();

        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            findings.AddRange(await RunProviderScanAsync(provider, accountId, cancellationToken));
        }

        return findings;
    }

    public async Task<IReadOnlyList<RiskFinding>> RunProviderScanAsync(
        ICloudProvider provider,
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var permissions = await provider.DiscoverPermissionsAsync(accountId, cancellationToken);
        return await _riskEngine.EvaluateAsync(permissions, provider.ProviderType, cancellationToken);
    }
}
