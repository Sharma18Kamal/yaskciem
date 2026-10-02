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
    IReadOnlyList<CloudResource> ResourceRecords,
    IReadOnlyList<DiscoveredCloudRole> RoleRecords,
    IReadOnlyList<DiscoveredCloudPermission> PermissionRecords,
    DateTime StartedAt,
    IReadOnlyList<LiveEffectiveAccessRecord> EffectiveAccessRecords);

public sealed record DiscoveredCloudRole(CloudProviderType Provider, CloudRole Role);
public sealed record DiscoveredCloudPermission(CloudProviderType Provider, CloudPermission Permission);

public sealed class LiveCloudScanService : ILiveCloudScanService
{
    private readonly IReadOnlyList<ICloudProvider> _providers;
    private readonly ILiveScanHistoryStore? _historyStore;
    private readonly RiskEngine _riskEngine = new();

    public LiveCloudScanResult? CurrentResult { get; private set; }
    public event EventHandler? ScanCompleted;

    public LiveCloudScanService(IEnumerable<ICloudProvider> providers, ILiveScanHistoryStore? historyStore = null)
    {
        _providers = providers.ToArray();
        _historyStore = historyStore;
    }

    public async Task<LiveCloudScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;
        var accountsScanned = 0;
        var identitiesScanned = 0;
        var resourcesScanned = 0;
        var rolesScanned = 0;
        var permissionsScanned = 0;
        var privilegedIdentities = 0;
        var identityRecords = new List<CloudPrincipal>();
        var resourceRecords = new List<CloudResource>();
        var roleRecords = new List<DiscoveredCloudRole>();
        var permissionRecords = new List<DiscoveredCloudPermission>();
        var effectiveAccessRecords = new List<LiveEffectiveAccessRecord>();
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

                        var roles = await provider.DiscoverRolesAsync(account.Id, cancellationToken);
                        rolesScanned += roles.Count;
                        roleRecords.AddRange(roles.Select(role => new DiscoveredCloudRole(provider.ProviderType, role)));

                        var permissions = await provider.DiscoverPermissionsAsync(account.Id, cancellationToken);
                        permissionsScanned += permissions.Count;
                        permissionRecords.AddRange(permissions.Select(permission => new DiscoveredCloudPermission(provider.ProviderType, permission)));
                        findings.AddRange(await _riskEngine.EvaluateAsync(permissions, provider.ProviderType, cancellationToken));

                        if (provider is IResourceProvider resourceProvider)
                        {
                            var resources = await resourceProvider.GetResourcesAsync(account.Id, cancellationToken);
                            resourcesScanned += resources.Count;
                            resourceRecords.AddRange(resources);
                        }

                        if (provider is ICloudEffectiveAccessProvider accessProvider)
                        {
                            var access = await accessProvider.DiscoverEffectiveAccessAsync(account.Id, cancellationToken);
                            effectiveAccessRecords.AddRange(access.Select(item => new LiveEffectiveAccessRecord(
                                provider.ProviderType.ToString(), item.Principal, item.Role, item.Scope, item.Source, item.IsPrivileged)));
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

        var result = new LiveCloudScanResult(
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
            resourceRecords,
            roleRecords,
            permissionRecords,
            startedAt,
            effectiveAccessRecords);
        if (_historyStore is not null)
        {
            var providers = string.Join(", ", messages.Select(message => message.Split(':')[0]).Distinct());
            var historyEntry = new LiveScanHistoryEntry(
                result.StartedAt,
                result.CompletedAt,
                providers,
                result.AccountCount,
                result.IdentityCount,
                result.ResourceCount,
                result.RoleCount,
                result.PermissionCount,
                result.Findings.Count,
                result.EffectiveAccessRecords);
            try
            {
                await _historyStore.RecordAsync(historyEntry, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                result = result with { ProviderMessages = result.ProviderMessages.Append($"Scan history could not be saved: {exception.Message}").ToArray() };
            }
        }

        CurrentResult = result;
        ScanCompleted?.Invoke(this, EventArgs.Empty);
        return CurrentResult;
    }
}
