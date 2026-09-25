namespace CloudEntitle.Application.Services;

using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;

public class ScanService
{
    private readonly IEnumerable<ICloudProvider> _providers;

    public ScanService(IEnumerable<ICloudProvider> providers)
    {
        _providers = providers;
    }

    public async Task<IReadOnlyList<RiskFinding>> RunScanAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var findings = new List<RiskFinding>();

        foreach (var provider in _providers)
        {
            if (provider.ProviderType == CloudProviderType.Azure)
            {
                findings.Add(new RiskFinding
                {
                    Id = Guid.NewGuid(),
                    CloudProvider = CloudProviderType.Azure,
                    Severity = RiskSeverity.High,
                    Title = "Subscription Owner",
                    Description = "High-privilege Azure role detected.",
                    Principal = "example-user",
                    Resource = "subscription-root",
                    Permission = "Owner",
                    Recommendation = "Replace with least-privilege role at resource group or resource scope.",
                    Evidence = "Role assignment found at subscription scope."
                });
            }
        }

        await Task.CompletedTask;
        return findings;
    }
}
