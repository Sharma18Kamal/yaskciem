using CloudEntitle.Application.Interfaces;
using CloudEntitle.Application.Services;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Desktop.ViewModels;

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
    IReadOnlyList<LiveEffectiveAccessRecord> EffectiveAccessRecords,
    IReadOnlyList<LiveScanAccountRecord> AccountRecords);

public sealed record DiscoveredCloudRole(CloudProviderType Provider, CloudRole Role);
public sealed record DiscoveredCloudPermission(CloudProviderType Provider, CloudPermission Permission);
public sealed record LiveScanAccountRecord(string Provider, string AccountName, string AccountId, string Profile, string PermissionSet, string ScanAccess = "Profile connected");

public sealed class LiveCloudScanService : ILiveCloudScanService
{
    private readonly IReadOnlyList<ICloudProvider> _providers;
    private readonly ILiveScanHistoryStore? _historyStore;
    private readonly IKubernetesRbacProvider? _kubernetesRbacProvider;
    private readonly CloudConnectionState? _cloudConnectionState;
    private readonly RiskEngine _riskEngine = new();

    public LiveCloudScanResult? CurrentResult { get; private set; }
    public event EventHandler? ScanCompleted;

    public LiveCloudScanService(IEnumerable<ICloudProvider> providers, ILiveScanHistoryStore? historyStore = null, IKubernetesRbacProvider? kubernetesRbacProvider = null, CloudConnectionState? cloudConnectionState = null)
    {
        _providers = providers.ToArray();
        _historyStore = historyStore;
        _kubernetesRbacProvider = kubernetesRbacProvider;
        _cloudConnectionState = cloudConnectionState;
    }

    public async Task<LiveCloudScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;
        var accountsScanned = 0;
        var accountRecords = new List<LiveScanAccountRecord>();
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
        var connectedProviders = _cloudConnectionState?.ConnectedProviders ?? new HashSet<CloudProviderType>();
        var selectedProvider = connectedProviders.Count == 1 ? connectedProviders.Single() : (CloudProviderType?)null;
        var multipleConnectedProviders = connectedProviders.Count > 1;
        if (selectedProvider.HasValue)
        {
            messages.Add($"{selectedProvider.Value}: scan is restricted to the connected provider.");
        }
        else if (multipleConnectedProviders)
        {
            messages.Add("Multiple cloud CLIs are signed in. Disconnect all but one provider before scanning to avoid mixed-provider results.");
        }

        foreach (var provider in _providers.Where(provider => !multipleConnectedProviders && (!selectedProvider.HasValue || provider.ProviderType == selectedProvider.Value)))
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
                accountRecords.AddRange(accounts.Select(account => new LiveScanAccountRecord(
                    provider.ProviderType.ToString(),
                    account.Name,
                    account.ExternalAccountId ?? account.SubscriptionId ?? account.ProjectId ?? string.Empty,
                    ExtractAwsProfile(account.Description, provider.ProviderType),
                    ExtractPermissionSet(account.Name, account.Description, provider.ProviderType))));
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
                            try
                            {
                                var resources = await resourceProvider.GetResourcesAsync(account.Id, cancellationToken);
                                resourcesScanned += resources.Count;
                                resourceRecords.AddRange(resources);
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception exception)
                            {
                                messages.Add($"{provider.ProviderType}/{account.Name} resource inventory: {exception.Message}");
                            }
                        }

                        if (provider is ICloudEffectiveAccessProvider accessProvider)
                        {
                            var access = await accessProvider.DiscoverEffectiveAccessAsync(account.Id, cancellationToken);
                            effectiveAccessRecords.AddRange(access.Select(item => new LiveEffectiveAccessRecord(
                                provider.ProviderType.ToString(), item.Principal, item.Role, item.Scope, item.Source, item.IsPrivileged, item.AssignmentType, item.LastUsedAt, item.LastChangedAt, item.LastChangedBy)));
                        }

                        if (provider is IAccessAnalyzerFindingsProvider analyzerProvider)
                        {
                            findings.AddRange(await analyzerProvider.DiscoverAccessAnalyzerFindingsAsync(account.Id, cancellationToken));
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

                if (provider is ICloudProviderDiagnostics diagnosticsProvider)
                {
                    messages.AddRange(diagnosticsProvider.DrainProviderMessages());
                }

                if (provider is IEntraDirectoryProvider entraProvider)
                {
                    var entraResult = await entraProvider.DiscoverEntraDirectoryAsync(accounts.FirstOrDefault()?.Id ?? Guid.Empty, cancellationToken);
                    var knownPrincipalIds = identityRecords.Select(identity => identity.PrincipalId).Where(id => id is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var newPrincipals = entraResult.Principals.Where(principal => principal.PrincipalId is null || knownPrincipalIds.Add(principal.PrincipalId)).ToArray();
                    identityRecords.AddRange(newPrincipals);
                    identitiesScanned += newPrincipals.Length;
                    privilegedIdentities += newPrincipals.Count(identity => identity.IsPrivileged);
                    effectiveAccessRecords.AddRange(entraResult.AccessAssignments.Select(item => new LiveEffectiveAccessRecord(
                        provider.ProviderType.ToString(), item.Principal, item.Role, item.Scope, item.Source, item.IsPrivileged, item.AssignmentType, item.LastUsedAt, item.LastChangedAt, item.LastChangedBy)));
                    messages.AddRange(entraResult.ProviderMessages);
                }

                if (provider is ICloudOrganizationProvider organizationProvider)
                {
                    var organizationResult = await organizationProvider.DiscoverOrganizationAccountsAsync(cancellationToken);
                    messages.AddRange(organizationResult.ProviderMessages);
                    foreach (var organizationAccount in organizationResult.Accounts)
                    {
                        var accountId = organizationAccount.ExternalAccountId ?? string.Empty;
                        var profileRecords = accountRecords.Where(item => item.Provider == provider.ProviderType.ToString() && item.AccountId == accountId).ToArray();
                        if (profileRecords.Length > 0)
                        {
                            for (var index = 0; index < accountRecords.Count; index++)
                            {
                                if (accountRecords[index].Provider == provider.ProviderType.ToString() && accountRecords[index].AccountId == accountId)
                                {
                                    accountRecords[index] = accountRecords[index] with { AccountName = organizationAccount.Name };
                                }
                            }
                        }
                        else
                        {
                            var scanAccess = provider.ProviderType == CloudProviderType.Aws
                                ? "Organization roster only; no configured CLI profile"
                                : "Hierarchy only; project scans are listed separately";
                            accountRecords.Add(new LiveScanAccountRecord(
                                provider.ProviderType.ToString(), organizationAccount.Name, accountId, string.Empty, string.Empty,
                                scanAccess));
                        }
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

        if (_kubernetesRbacProvider is not null && !multipleConnectedProviders)
        {
            try
            {
                var kubernetesResult = await _kubernetesRbacProvider.ScanKubernetesRbacAsync(cancellationToken, selectedProvider);
                messages.AddRange(kubernetesResult.ProviderMessages);
                foreach (var cluster in kubernetesResult.Clusters.Where(cluster => !selectedProvider.HasValue || cluster.Cluster.CloudProvider == selectedProvider.Value))
                {
                    var providerType = cluster.Cluster.CloudProvider;
                    accountsScanned++;
                    accountRecords.Add(new LiveScanAccountRecord(providerType.ToString(), cluster.Cluster.Name, cluster.Cluster.ExternalAccountId ?? string.Empty, cluster.Cluster.Description ?? string.Empty, "Kubernetes RBAC", $"{cluster.Cluster.ConnectionStatus}: read-only scan"));
                    resourcesScanned += cluster.Resources.Count;
                    resourceRecords.AddRange(cluster.Resources);
                    identitiesScanned += cluster.Principals.Count;
                    privilegedIdentities += cluster.Principals.Count(principal => principal.IsPrivileged);
                    identityRecords.AddRange(cluster.Principals);
                    rolesScanned += cluster.Roles.Count;
                    roleRecords.AddRange(cluster.Roles.Select(role => new DiscoveredCloudRole(providerType, role)));
                    permissionsScanned += cluster.Permissions.Count;
                    permissionRecords.AddRange(cluster.Permissions.Select(permission => new DiscoveredCloudPermission(providerType, permission)));
                    findings.AddRange(await _riskEngine.EvaluateAsync(cluster.Permissions, providerType, cancellationToken));
                    effectiveAccessRecords.AddRange(cluster.AccessAssignments.Select(item => new LiveEffectiveAccessRecord(
                        providerType.ToString(), item.Principal, item.Role, item.Scope, item.Source, item.IsPrivileged, item.AssignmentType, item.LastUsedAt, item.LastChangedAt, item.LastChangedBy)));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                messages.Add($"Kubernetes: {exception.Message}");
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
            effectiveAccessRecords,
            accountRecords);
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
                result.EffectiveAccessRecords,
                result.AccountRecords);
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

    private static string ExtractAwsProfile(string? accountDescription, CloudProviderType provider)
    {
        if (provider != CloudProviderType.Aws || accountDescription is null) return string.Empty;
        const string marker = "CLI profile: ";
        var markerIndex = accountDescription.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0) return string.Empty;
        var start = markerIndex + marker.Length;
        var end = accountDescription.IndexOf(';', start);
        return end < 0 ? accountDescription[start..].Trim() : accountDescription[start..end].Trim();
    }

    private static string ExtractPermissionSet(string accountName, string? accountDescription, CloudProviderType provider)
    {
        if (provider != CloudProviderType.Aws) return string.Empty;
        const string permissionSetMarker = "permission set: ";
        if (accountDescription is not null)
        {
            var descriptionMarkerIndex = accountDescription.IndexOf(permissionSetMarker, StringComparison.OrdinalIgnoreCase);
            if (descriptionMarkerIndex >= 0) return accountDescription[(descriptionMarkerIndex + permissionSetMarker.Length)..].Trim();
        }
        const string assumedRoleMarker = ":assumed-role/";
        var markerIndex = accountName.LastIndexOf(assumedRoleMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            var role = accountName[(markerIndex + assumedRoleMarker.Length)..].Split('/')[0];
            if (!string.IsNullOrWhiteSpace(role)) return role;
        }
        var open = accountName.LastIndexOf('(');
        var close = accountName.LastIndexOf(')');
        return open >= 0 && close > open ? accountName[(open + 1)..close] : string.Empty;
    }
}
