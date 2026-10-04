using CloudEntitle.Application.Interfaces;
using CloudEntitle.Application.Services;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Desktop.ViewModels;
using ApplicationRiskEngine = CloudEntitle.Application.Services.RiskEngine;

namespace CloudEntitle.Desktop.Services;

public interface ILiveCloudScanService
{
    LiveCloudScanResult? CurrentResult { get; }
    event EventHandler? ScanCompleted;
    Task<IReadOnlyList<LiveScanAccountRecord>> DiscoverAccountsAsync(CancellationToken cancellationToken = default);
    Task<LiveCloudScanResult> ScanAsync(CancellationToken cancellationToken = default);
    Task<LiveCloudScanResult> ScanAsync(IReadOnlySet<string>? selectedAccountKeys, CancellationToken cancellationToken = default);
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
    IReadOnlyList<LiveScanAccountRecord> AccountRecords)
{
    public IReadOnlyList<ScanCoverageRecord> Coverage { get; init; } = Array.Empty<ScanCoverageRecord>();
    public IReadOnlyList<KubernetesWorkloadTrace> WorkloadTraces { get; init; } = Array.Empty<KubernetesWorkloadTrace>();
    public IReadOnlyList<PostureChangeRecord> PostureChanges { get; init; } = Array.Empty<PostureChangeRecord>();
    public PostureTrendRecord? PostureTrend { get; init; }
    public string NotificationDelivery { get; init; } = "Not attempted";
}

public sealed record DiscoveredCloudRole(CloudProviderType Provider, CloudRole Role);
public sealed record DiscoveredCloudPermission(CloudProviderType Provider, CloudPermission Permission);
public sealed record LiveScanAccountRecord(string Provider, string AccountName, string AccountId, string Profile, string PermissionSet, string ScanAccess = "Profile connected")
{
    public Guid CloudAccountId { get; init; }
    public string SelectionKey => CreateSelectionKey(Provider, AccountId, Profile);

    public static string CreateSelectionKey(string provider, string accountId, string profile = "")
        => $"{provider.Trim().ToUpperInvariant()}|{accountId.Trim().ToUpperInvariant()}|{profile.Trim().ToUpperInvariant()}";
}

public sealed class LiveCloudScanService : ILiveCloudScanService
{
    private readonly IReadOnlyList<ICloudProvider> _providers;
    private readonly ILiveScanHistoryStore? _historyStore;
    private readonly IKubernetesRbacProvider? _kubernetesRbacProvider;
    private readonly CloudConnectionState? _cloudConnectionState;
    private readonly PostureHistoryService _postureHistoryService = new();
    private readonly IPostureNotificationChannel _postureNotificationChannel;
    private readonly ApplicationRiskEngine _riskEngine = new();
    private readonly SemaphoreSlim _scanGate = new(1, 1);

    public LiveCloudScanResult? CurrentResult { get; private set; }
    public event EventHandler? ScanCompleted;

    public LiveCloudScanService(IEnumerable<ICloudProvider> providers, ILiveScanHistoryStore? historyStore = null, IKubernetesRbacProvider? kubernetesRbacProvider = null, CloudConnectionState? cloudConnectionState = null, IPostureNotificationChannel? postureNotificationChannel = null)
    {
        _providers = providers.ToArray();
        _historyStore = historyStore;
        _kubernetesRbacProvider = kubernetesRbacProvider;
        _cloudConnectionState = cloudConnectionState;
        _postureNotificationChannel = postureNotificationChannel ?? new DisabledPostureNotificationChannel();
    }

    public async Task<IReadOnlyList<LiveScanAccountRecord>> DiscoverAccountsAsync(CancellationToken cancellationToken = default)
    {
        var providers = _providers;
        var connected = _cloudConnectionState?.ConnectedProviders;
        if (connected is { Count: > 1 })
            throw new InvalidOperationException("Disconnect all but one cloud provider before discovering scan accounts.");
        if (connected is { Count: 1 })
            providers = _providers.Where(provider => provider.ProviderType == connected.Single()).ToArray();

        var readyProviders = new List<ICloudProvider>();
        foreach (var provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await provider.TestConnectionAsync(cancellationToken)) readyProviders.Add(provider);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }

        if (readyProviders.Count == 0)
        {
            _cloudConnectionState?.Update(Array.Empty<CloudProviderType>());
            return Array.Empty<LiveScanAccountRecord>();
        }
        if (readyProviders.Count > 1)
        {
            _cloudConnectionState?.Update(readyProviders.Select(provider => provider.ProviderType));
            throw new InvalidOperationException("More than one provider CLI is connected. Disconnect all but one provider before discovering accounts.");
        }

        var selectedProvider = readyProviders[0];
        _cloudConnectionState?.Update(new[] { selectedProvider.ProviderType });
        var accounts = await selectedProvider.DiscoverAccountsAsync(cancellationToken);
        return accounts.Select(account => ToAccountRecord(selectedProvider.ProviderType, account)).ToArray();
    }

    public Task<LiveCloudScanResult> ScanAsync(CancellationToken cancellationToken = default)
        => ScanAsync(null, cancellationToken);

    public async Task<LiveCloudScanResult> ScanAsync(IReadOnlySet<string>? selectedAccountKeys, CancellationToken cancellationToken = default)
    {
        await _scanGate.WaitAsync(cancellationToken);
        try
        {
            return await ScanCoreAsync(selectedAccountKeys, cancellationToken);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private async Task<LiveCloudScanResult> ScanCoreAsync(IReadOnlySet<string>? selectedAccountKeys, CancellationToken cancellationToken)
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
        var coverage = new List<ScanCoverageRecord>();
        var workloadTraces = new List<KubernetesWorkloadTrace>();
        IReadOnlyList<LiveScanHistoryEntry> previousScans = Array.Empty<LiveScanHistoryEntry>();
        if (_historyStore is not null)
        {
            try
            {
                previousScans = await _historyStore.GetRecentAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                messages.Add($"Posture history could not be read: {exception.Message}");
            }
        }
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
            coverage.AddRange(_providers.Select(provider => new ScanCoverageRecord(provider.ProviderType.ToString(), "Provider scan", ScanCoverageStatus.Skipped, 0, "Disconnect all but one provider to run a provider-scoped scan.")));
        }

        foreach (var provider in _providers.Where(provider => !multipleConnectedProviders && (!selectedProvider.HasValue || provider.ProviderType == selectedProvider.Value)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!await provider.TestConnectionAsync(cancellationToken))
                {
                    if (_cloudConnectionState?.ConnectedProviders.Contains(provider.ProviderType) == true)
                        _cloudConnectionState.Update(Array.Empty<CloudProviderType>());
                    messages.Add($"{provider.ProviderType}: no active CLI sign-in. Use Connect Cloud first.");
                    coverage.Add(new ScanCoverageRecord(provider.ProviderType.ToString(), "CLI sign-in", ScanCoverageStatus.Unavailable, 0, "No active CLI sign-in was detected."));
                    continue;
                }

                _cloudConnectionState?.Update(new[] { provider.ProviderType });
                var accounts = await CollectAsync(provider.ProviderType.ToString(), string.Empty, "Cloud accounts", () => provider.DiscoverAccountsAsync(cancellationToken), coverage, messages, cancellationToken);
                var selectedAccounts = accounts.Where(account => selectedAccountKeys is null
                    || selectedAccountKeys.Contains(ToAccountRecord(provider.ProviderType, account).SelectionKey)).ToArray();
                var selectedIds = selectedAccounts.Select(account => account.Id).ToHashSet();
                accountsScanned += selectedAccounts.Length;
                accountRecords.AddRange(accounts.Select(account => ToAccountRecord(provider.ProviderType, account,
                    selectedIds.Contains(account.Id) ? "Scanned" : "Not selected for this scan")));
                foreach (var account in accounts.Where(account => !selectedIds.Contains(account.Id)))
                {
                    var skippedScope = $"{account.Name} ({GetStableAccountId(account)})";
                    coverage.Add(new ScanCoverageRecord(provider.ProviderType.ToString(), $"{skippedScope}: Account selection", ScanCoverageStatus.Skipped, 0, "Account was not selected for this scan."));
                }
                if (selectedAccounts.Length == 0 && selectedAccountKeys is { Count: > 0 })
                    messages.Add($"{provider.ProviderType}: none of the selected accounts are available in this scan.");
                foreach (var account in selectedAccounts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var scanScope = $"{account.Name} ({account.ExternalAccountId ?? account.SubscriptionId ?? account.ProjectId ?? account.Id.ToString()})";
                    var identities = await CollectAsync(provider.ProviderType.ToString(), scanScope, "Identities", () => provider.DiscoverIdentitiesAsync(account.Id, cancellationToken), coverage, messages, cancellationToken);
                    identitiesScanned += identities.Count;
                    identityRecords.AddRange(identities);
                    privilegedIdentities += identities.Count(identity => identity.IsPrivileged);

                    var roles = await CollectAsync(provider.ProviderType.ToString(), scanScope, "Roles", () => provider.DiscoverRolesAsync(account.Id, cancellationToken), coverage, messages, cancellationToken);
                    rolesScanned += roles.Count;
                    roleRecords.AddRange(roles.Select(role => new DiscoveredCloudRole(provider.ProviderType, role)));

                    var permissions = await CollectAsync(provider.ProviderType.ToString(), scanScope, "Permissions", () => provider.DiscoverPermissionsAsync(account.Id, cancellationToken), coverage, messages, cancellationToken);
                    permissionsScanned += permissions.Count;
                    permissionRecords.AddRange(permissions.Select(permission => new DiscoveredCloudPermission(provider.ProviderType, permission)));
                    var accountFindings = await _riskEngine.EvaluateAsync(permissions, provider.ProviderType, cancellationToken);
                    foreach (var finding in accountFindings)
                    {
                        finding.CloudAccountId = account.Id;
                        findings.Add(finding);
                    }

                    if (provider is IResourceProvider resourceProvider)
                    {
                        var resources = await CollectAsync(provider.ProviderType.ToString(), scanScope, "Resources", () => resourceProvider.GetResourcesAsync(account.Id, cancellationToken), coverage, messages, cancellationToken);
                        resourcesScanned += resources.Count;
                        resourceRecords.AddRange(resources);
                    }
                    else
                    {
                        coverage.Add(new ScanCoverageRecord(provider.ProviderType.ToString(), $"{scanScope}: Resources", ScanCoverageStatus.Unsupported, 0, "This provider adapter does not expose resource inventory."));
                    }

                    if (provider is ICloudEffectiveAccessProvider accessProvider)
                    {
                        var access = await CollectAsync(provider.ProviderType.ToString(), scanScope, "Effective access", () => accessProvider.DiscoverEffectiveAccessAsync(account.Id, cancellationToken), coverage, messages, cancellationToken);
                        effectiveAccessRecords.AddRange(access.Select(item => new LiveEffectiveAccessRecord(
                            provider.ProviderType.ToString(), item.Principal, item.Role, item.Scope, item.Source, item.IsPrivileged, item.AssignmentType, item.LastUsedAt, item.LastChangedAt, item.LastChangedBy, account.ExternalAccountId ?? account.SubscriptionId ?? account.ProjectId ?? account.Id.ToString())));
                    }
                    else
                    {
                        coverage.Add(new ScanCoverageRecord(provider.ProviderType.ToString(), $"{scanScope}: Effective access", ScanCoverageStatus.Unsupported, 0, "This provider adapter does not expose effective-access assignments."));
                    }

                    if (provider is IAccessAnalyzerFindingsProvider analyzerProvider)
                    {
                        var analyzerFindings = await CollectAsync(provider.ProviderType.ToString(), scanScope, "Access analyzer", () => analyzerProvider.DiscoverAccessAnalyzerFindingsAsync(account.Id, cancellationToken), coverage, messages, cancellationToken);
                        foreach (var finding in analyzerFindings) finding.CloudAccountId = account.Id;
                        findings.AddRange(analyzerFindings);
                    }
                    else if (provider.ProviderType == CloudProviderType.Aws)
                    {
                        coverage.Add(new ScanCoverageRecord(provider.ProviderType.ToString(), $"{scanScope}: Access analyzer", ScanCoverageStatus.Unsupported, 0, "This AWS adapter does not expose Access Analyzer findings."));
                    }
                }

                if (provider is ICloudProviderDiagnostics diagnosticsProvider)
                {
                    messages.AddRange(diagnosticsProvider.DrainProviderMessages());
                }

                if (provider is IEntraDirectoryProvider entraProvider)
                {
                    try
                    {
                        var entraResult = await entraProvider.DiscoverEntraDirectoryAsync(accounts.FirstOrDefault()?.Id ?? Guid.Empty, cancellationToken);
                        var knownPrincipalIds = identityRecords.Select(identity => identity.PrincipalId).Where(id => id is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var newPrincipals = entraResult.Principals.Where(principal => principal.PrincipalId is null || knownPrincipalIds.Add(principal.PrincipalId)).ToArray();
                        identityRecords.AddRange(newPrincipals);
                        identitiesScanned += newPrincipals.Length;
                        privilegedIdentities += newPrincipals.Count(identity => identity.IsPrivileged);
                        effectiveAccessRecords.AddRange(entraResult.AccessAssignments.Select(item => new LiveEffectiveAccessRecord(
                            provider.ProviderType.ToString(), item.Principal, item.Role, item.Scope, item.Source, item.IsPrivileged, item.AssignmentType, item.LastUsedAt, item.LastChangedAt, item.LastChangedBy, accounts.FirstOrDefault()?.ExternalAccountId ?? accounts.FirstOrDefault()?.SubscriptionId ?? accounts.FirstOrDefault()?.TenantId)));
                        var partial = entraResult.ProviderMessages.Count > 0;
                        coverage.Add(new ScanCoverageRecord(provider.ProviderType.ToString(), "Entra directory", entraResult.Principals.Count > 0 ? partial ? ScanCoverageStatus.Partial : ScanCoverageStatus.Collected : partial ? ScanCoverageStatus.Denied : ScanCoverageStatus.NoData, entraResult.Principals.Count, string.Join("; ", entraResult.ProviderMessages.DefaultIfEmpty("Users, groups, service principals, and managed identities."))));
                        coverage.Add(new ScanCoverageRecord(provider.ProviderType.ToString(), "Entra PIM", entraResult.AccessAssignments.Count > 0 ? partial ? ScanCoverageStatus.Partial : ScanCoverageStatus.Collected : partial ? ScanCoverageStatus.Denied : ScanCoverageStatus.NoData, entraResult.AccessAssignments.Count, string.Join("; ", entraResult.ProviderMessages.DefaultIfEmpty("Active and eligible directory-role assignments."))));
                        messages.AddRange(entraResult.ProviderMessages);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception exception)
                    {
                        var status = StatusForException(exception);
                        coverage.Add(new ScanCoverageRecord(provider.ProviderType.ToString(), "Entra directory and PIM", status, 0, exception.Message));
                        messages.Add($"{provider.ProviderType}: Entra directory and PIM: {exception.Message}");
                    }
                }
                else if (provider.ProviderType == CloudProviderType.Azure)
                {
                    coverage.Add(new ScanCoverageRecord(provider.ProviderType.ToString(), "Entra directory and PIM", ScanCoverageStatus.Unsupported, 0, "This Azure provider adapter does not expose Entra directory or PIM discovery."));
                }

                if (provider is ICloudOrganizationProvider organizationProvider)
                {
                    try
                    {
                        var organizationResult = await organizationProvider.DiscoverOrganizationAccountsAsync(cancellationToken);
                        var hierarchyStatus = organizationResult.Accounts.Count > 0
                            ? organizationResult.ProviderMessages.Count > 0 ? ScanCoverageStatus.Partial : ScanCoverageStatus.Collected
                            : organizationResult.ProviderMessages.Count > 0 ? StatusForException(new InvalidOperationException(string.Join(";", organizationResult.ProviderMessages))) : ScanCoverageStatus.NoData;
                        coverage.Add(new ScanCoverageRecord(provider.ProviderType.ToString(), "Organization hierarchy", hierarchyStatus, organizationResult.Accounts.Count, string.Join("; ", organizationResult.ProviderMessages.DefaultIfEmpty("Organization account discovery."))));
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
                                    scanAccess) { CloudAccountId = organizationAccount.Id });
                            }
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception exception)
                    {
                        coverage.Add(new ScanCoverageRecord(provider.ProviderType.ToString(), "Organization hierarchy", StatusForException(exception), 0, exception.Message));
                        messages.Add($"{provider.ProviderType}: organization hierarchy: {exception.Message}");
                    }
                }
                else
                {
                    coverage.Add(new ScanCoverageRecord(provider.ProviderType.ToString(), "Organization hierarchy", ScanCoverageStatus.Unsupported, 0, "This provider adapter does not expose organization hierarchy discovery."));
                }

                messages.Add($"{provider.ProviderType}: discovered {accounts.Count} account(s).");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                coverage.Add(new ScanCoverageRecord(provider.ProviderType.ToString(), "Provider scan", StatusForException(exception), 0, exception.Message));
                messages.Add($"{provider.ProviderType}: {exception.Message}");
            }
        }

        if (_kubernetesRbacProvider is not null && !multipleConnectedProviders && selectedAccountKeys is not null)
        {
            coverage.Add(new ScanCoverageRecord("Kubernetes", "Cluster accounts", ScanCoverageStatus.Skipped, 0, "Kubernetes cluster accounts are not included in the selected cloud-account scope. Select all accounts to include the Kubernetes scan."));
            messages.Add("Kubernetes cluster scanning was skipped because the account picker does not yet include cluster accounts.");
        }

        if (_kubernetesRbacProvider is not null && !multipleConnectedProviders && selectedAccountKeys is null)
        {
            try
            {
                var kubernetesResult = await _kubernetesRbacProvider.ScanKubernetesRbacAsync(cancellationToken, selectedProvider);
                messages.AddRange(kubernetesResult.ProviderMessages);
                coverage.AddRange(kubernetesResult.Coverage);
                workloadTraces.AddRange(kubernetesResult.Clusters.SelectMany(cluster => cluster.WorkloadTraces));
                foreach (var cluster in kubernetesResult.Clusters.Where(cluster => !selectedProvider.HasValue || cluster.Cluster.CloudProvider == selectedProvider.Value))
                {
                    var providerType = cluster.Cluster.CloudProvider;
                    accountsScanned++;
                    accountRecords.Add(new LiveScanAccountRecord(providerType.ToString(), cluster.Cluster.Name, cluster.Cluster.ExternalAccountId ?? string.Empty, cluster.Cluster.Description ?? string.Empty, "Kubernetes RBAC", $"Scanned: {cluster.Cluster.ConnectionStatus}; read-only scan") { CloudAccountId = cluster.Cluster.Id });
                    resourcesScanned += cluster.Resources.Count;
                    resourceRecords.AddRange(cluster.Resources);
                    identitiesScanned += cluster.Principals.Count;
                    privilegedIdentities += cluster.Principals.Count(principal => principal.IsPrivileged);
                    identityRecords.AddRange(cluster.Principals);
                    rolesScanned += cluster.Roles.Count;
                    roleRecords.AddRange(cluster.Roles.Select(role => new DiscoveredCloudRole(providerType, role)));
                    permissionsScanned += cluster.Permissions.Count;
                    permissionRecords.AddRange(cluster.Permissions.Select(permission => new DiscoveredCloudPermission(providerType, permission)));
                    var clusterFindings = await _riskEngine.EvaluateAsync(cluster.Permissions, providerType, cancellationToken);
                    foreach (var finding in clusterFindings)
                    {
                        finding.CloudAccountId = cluster.Cluster.Id;
                        findings.Add(finding);
                    }
                    effectiveAccessRecords.AddRange(cluster.AccessAssignments.Select(item => new LiveEffectiveAccessRecord(
                        providerType.ToString(), item.Principal, item.Role, item.Scope, item.Source, item.IsPrivileged, item.AssignmentType, item.LastUsedAt, item.LastChangedAt, item.LastChangedBy, cluster.Cluster.ExternalAccountId ?? cluster.Cluster.Id.ToString())));
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

        var completedAt = DateTime.UtcNow;
        var scannedAccountKeys = accountRecords.Where(account => account.ScanAccess.StartsWith("Scanned", StringComparison.OrdinalIgnoreCase))
            .Select(account => account.SelectionKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var riskFindingDetails = PostureHistoryService.SnapshotFindings(findings);
        var providersInScan = accountRecords.Select(item => item.Provider)
            .Concat(effectiveAccessRecords.Select(item => item.Provider))
            .Concat(coverage.Select(item => item.Provider))
            .Concat(riskFindingDetails.Select(item => item.Provider))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var previous = previousScans.Where(scan => providersInScan.Overlaps(ScanProviders(scan))
                && scan.ScannedAccountKeys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                    .SequenceEqual(scannedAccountKeys, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(scan => scan.CompletedAt)
            .FirstOrDefault();
        var historyEntry = new LiveScanHistoryEntry(
            startedAt,
            completedAt,
            string.Join(", ", providersInScan),
            accountsScanned,
            identitiesScanned,
            resourcesScanned,
            rolesScanned,
            permissionsScanned,
            findings.Count,
            effectiveAccessRecords,
            accountRecords)
        {
            Coverage = coverage,
                        ScannedAccountKeys = scannedAccountKeys,
            WorkloadTraces = workloadTraces,
            RiskFindingDetails = riskFindingDetails
        };
        var postureAnalysis = _postureHistoryService.Analyze(previous, historyEntry);
        var notifications = (previous?.PendingNotifications ?? Array.Empty<PostureChangeRecord>())
            .Concat(postureAnalysis.Changes.Where(change => change.AlertEligible))
            .GroupBy(PostureHistoryService.NotificationKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var delivery = await _postureNotificationChannel.DeliverAsync(notifications, cancellationToken);
        var pendingNotifications = delivery.Delivered == notifications.Length
            ? Array.Empty<PostureChangeRecord>()
            : notifications;
        historyEntry = historyEntry with
        {
            PostureChanges = postureAnalysis.Changes,
            PendingNotifications = pendingNotifications,
            PostureTrend = postureAnalysis.Trend,
            NotificationDelivery = delivery.Status
        };
        var result = new LiveCloudScanResult(
            accountsScanned,
            identitiesScanned,
            resourcesScanned,
            rolesScanned,
            permissionsScanned,
            privilegedIdentities,
            findings,
            completedAt,
            messages,
            identityRecords,
            resourceRecords,
            roleRecords,
            permissionRecords,
            startedAt,
            effectiveAccessRecords,
            accountRecords)
        {
            Coverage = coverage,
            WorkloadTraces = workloadTraces,
            PostureChanges = postureAnalysis.Changes,
            PostureTrend = postureAnalysis.Trend,
            NotificationDelivery = delivery.Status
        };
        if (_historyStore is not null)
        {
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

    private static LiveScanAccountRecord ToAccountRecord(CloudProviderType provider, CloudAccount account, string scanAccess = "Available")
        => new(provider.ToString(), account.Name, GetStableAccountId(account), ExtractAwsProfile(account.Description, provider),
            ExtractPermissionSet(account.Name, account.Description, provider), scanAccess) { CloudAccountId = account.Id };

    private static string GetStableAccountId(CloudAccount account)
        => account.CloudProvider == CloudProviderType.Azure
            ? account.SubscriptionId ?? account.ExternalAccountId ?? account.ProjectId ?? account.Id.ToString()
            : account.ExternalAccountId ?? account.ProjectId ?? account.SubscriptionId ?? account.Id.ToString();

    private static HashSet<string> ScanProviders(LiveScanHistoryEntry scan)
        => scan.AccountDetails.Select(account => account.Provider)
            .Concat(scan.EffectiveAccess.Select(access => access.Provider))
            .Concat(scan.Coverage.Select(item => item.Provider))
            .Concat(scan.RiskFindingDetails.Select(finding => finding.Provider))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private sealed class DisabledPostureNotificationChannel : IPostureNotificationChannel
    {
        public Task<PostureNotificationResult> DeliverAsync(IReadOnlyList<PostureChangeRecord> alerts, CancellationToken cancellationToken = default)
            => Task.FromResult(new PostureNotificationResult(false, 0, "Webhook not configured; alert delivery skipped."));
    }

    private static async Task<IReadOnlyList<T>> CollectAsync<T>(
        string provider,
        string account,
        string dataSource,
        Func<Task<IReadOnlyList<T>>> discover,
        ICollection<ScanCoverageRecord> coverage,
        ICollection<string> messages,
        CancellationToken cancellationToken)
    {
        try
        {
            var records = await discover();
            coverage.Add(new ScanCoverageRecord(provider, CoverageName(account, dataSource), records.Count > 0 ? ScanCoverageStatus.Collected : ScanCoverageStatus.NoData, records.Count, records.Count > 0 ? $"{records.Count} record(s) collected." : "The source was queried successfully and returned no records."));
            return records;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var status = StatusForException(exception);
            coverage.Add(new ScanCoverageRecord(provider, CoverageName(account, dataSource), status, 0, exception.Message));
            messages.Add($"{provider}/{account}: {dataSource}: {exception.Message}");
            return Array.Empty<T>();
        }
    }

    private static string CoverageName(string account, string dataSource)
        => string.IsNullOrWhiteSpace(account) ? dataSource : $"{account}: {dataSource}";

    private static ScanCoverageStatus StatusForException(Exception exception)
    {
        var message = exception.ToString();
        if (message.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
            || message.Contains("accessdenied", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not authorized", StringComparison.OrdinalIgnoreCase)
            || message.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
            || message.Contains("insufficient privileges", StringComparison.OrdinalIgnoreCase)) return ScanCoverageStatus.Denied;
        if (message.Contains("not supported", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unsupported", StringComparison.OrdinalIgnoreCase)) return ScanCoverageStatus.Unsupported;
        return ScanCoverageStatus.Unavailable;
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
