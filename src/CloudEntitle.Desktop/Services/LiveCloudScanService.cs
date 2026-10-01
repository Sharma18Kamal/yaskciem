using CloudEntitle.Application.Interfaces;
using CloudEntitle.Application.Services;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;

namespace CloudEntitle.Desktop.Services;

public interface ILiveCloudScanService
{
    LiveCloudScanResult? CurrentResult { get; }
    event EventHandler? ScanCompleted;
    Task<LiveCloudScanResult> ScanAsync(CancellationToken cancellationToken = default);
}

public sealed record LiveCloudScanResult(
    int AccountCount,
    int IdentityCount,
    int ResourceCount,
    int RoleCount,
    int PermissionCount,
    int PrivilegedIdentityCount,
    IReadOnlyList<RiskFinding> Findings,
    DateTime CompletedAt,
    IReadOnlyList<string> ProviderMessages,
    IReadOnlyList<CloudPrincipal> IdentityRecords,
    IReadOnlyList<CloudResource> ResourceRecords);

public sealed class LiveCloudScanService : ILiveCloudScanService
{
    private readonly IReadOnlyList<ICloudProvider> _providers;
    private readonly RiskEngine _riskEngine = new();

    public LiveCloudScanResult? CurrentResult { get; private set; }
    public event EventHandler? ScanCompleted;

    public LiveCloudScanService(IEnumerable<ICloudProvider> providers)
    {
        _providers = providers.ToArray();
    }

    public async Task<LiveCloudScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        var accountsScanned = 0;
        var identitiesScanned = 0;
        var resourcesScanned = 0;
        var rolesScanned = 0;
        var permissionsScanned = 0;
        var privilegedIdentities = 0;
        var identityRecords = new List<CloudPrincipal>();
        var resourceRecords = new List<CloudResource>();
        var findings = new List<RiskFinding>();
        var messages = new List<string>();

        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!await provider.TestConnectionAsync(cancellationToken))
                {
                    messages.Add($"{provider.ProviderType}: no active CLI sign-in. Use Connect Cloud first.");
                    continue;
                }

                var accounts = await provider.DiscoverAccountsAsync(cancellationToken);
                accountsScanned += accounts.Count;
                foreach (var account in accounts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var identities = await provider.DiscoverIdentitiesAsync(account.Id, cancellationToken);
                        identitiesScanned += identities.Count;
                        identityRecords.AddRange(identities);
                        privilegedIdentities += identities.Count(identity => identity.IsPrivileged);

                        rolesScanned += (await provider.DiscoverRolesAsync(account.Id, cancellationToken)).Count;

                        var permissions = await provider.DiscoverPermissionsAsync(account.Id, cancellationToken);
                        permissionsScanned += permissions.Count;
                        findings.AddRange(await _riskEngine.EvaluateAsync(permissions, provider.ProviderType, cancellationToken));

                        if (provider is IResourceProvider resourceProvider)
                        {
                            var resources = await resourceProvider.GetResourcesAsync(account.Id, cancellationToken);
                            resourcesScanned += resources.Count;
                            resourceRecords.AddRange(resources);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        messages.Add($"{provider.ProviderType}/{account.Name}: {exception.Message}");
                    }
                }

                messages.Add($"{provider.ProviderType}: discovered {accounts.Count} account(s).");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                messages.Add($"{provider.ProviderType}: {exception.Message}");
            }
        }

        if (messages.Count == 0)
        {
            messages.Add("No cloud providers are configured.");
        }

        CurrentResult = new LiveCloudScanResult(
            accountsScanned,
            identitiesScanned,
            resourcesScanned,
            rolesScanned,
            permissionsScanned,
            privilegedIdentities,
            findings,
            DateTime.UtcNow,
            messages,
            identityRecords,
            resourceRecords);
        ScanCompleted?.Invoke(this, EventArgs.Empty);
        return CurrentResult;
    }
}
