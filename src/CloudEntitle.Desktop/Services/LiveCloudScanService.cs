using CloudEntitle.Application.Interfaces;
using CloudEntitle.Application.Services;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Desktop.ViewModels;
using CloudEntitle.RiskEngine.Models;
using ApplicationRiskEngine = CloudEntitle.Application.Services.RiskEngine;
using DomainAccessPath = CloudEntitle.Domain.Entities.AccessPath;

namespace CloudEntitle.Desktop.Services;

public interface ILiveCloudScanService
{
    LiveCloudScanResult? CurrentResult { get; }
    event EventHandler? ScanCompleted;
    Task<IReadOnlyList<LiveScanAccountRecord>> DiscoverAccountsAsync(CancellationToken cancellationToken = default);
    Task<LiveCloudScanResult> ScanAsync(CancellationToken cancellationToken = default);
    Task<LiveCloudScanResult> ScanAsync(IReadOnlySet<string>? selectedAccountKeys, CancellationToken cancellationToken = default);
    Task<LiveCloudScanResult> ScanVerifiedAccountAsync(LiveScanAccountRecord account, CloudAccountVerification verification, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Verified-account scanning is not configured.");
    async Task<LiveCloudScanResult> ScanAsync(IReadOnlySet<string>? selectedAccountKeys, IProgress<LiveScanProgress>? progress, CancellationToken cancellationToken = default)
    {
        progress?.Report(new LiveScanProgress("Cloud", string.Empty, "Scan", LiveScanProgressState.Started));
        var result = await ScanAsync(selectedAccountKeys, cancellationToken);
        progress?.Report(new LiveScanProgress("Cloud", string.Empty, "Scan", LiveScanProgressState.Completed));
        return result;
    }
}

public enum LiveScanProgressState
{
    Started,
    Completed,
    Failed
}

public sealed record LiveScanProgress(
    string Provider,
    string Account,
    string DataSource,
    LiveScanProgressState State,
    int? RecordCount = null)
{
    public string Message
    {
        get
        {
            var scope = string.IsNullOrWhiteSpace(Account) ? Provider : $"{Provider} / {Account}";
            return State switch
            {
                LiveScanProgressState.Started => $"Scanning {scope}: {DataSource}...",
                LiveScanProgressState.Completed when RecordCount.GetValueOrDefault() == 0 => $"No {DataSource} records found for {scope}.",
                LiveScanProgressState.Completed => $"Collected {RecordCount} {DataSource} record(s) from {scope}.",
                _ => $"{DataSource} could not be collected for {scope}; see Source coverage for details."
            };
        }
    }
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
    public IReadOnlyList<ResourceSecuritySignal> ResourceSecuritySignals { get; init; } = Array.Empty<ResourceSecuritySignal>();
    public IReadOnlyList<ResourceSecurityEvidenceCoverage> ResourceSecurityCoverage { get; init; } = Array.Empty<ResourceSecurityEvidenceCoverage>();
    public IReadOnlyList<DomainAccessPath> ModeledAccessPaths { get; init; } = Array.Empty<DomainAccessPath>();
    public IReadOnlyList<RiskFindingModel> ResourceRiskFindings { get; init; } = Array.Empty<RiskFindingModel>();
    public IReadOnlyList<string> RiskGraphDiagnostics { get; init; } = Array.Empty<string>();
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
    private readonly LiveResourceRiskGraphEvaluator _resourceRiskGraphEvaluator = new();
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly ICloudAuthenticationLauncher? _authenticationLauncher;

    public LiveCloudScanResult? CurrentResult { get; private set; }
    public event EventHandler? ScanCompleted;

    public LiveCloudScanService(IEnumerable<ICloudProvider> providers, ILiveScanHistoryStore? historyStore = null, IKubernetesRbacProvider? kubernetesRbacProvider = null, CloudConnectionState? cloudConnectionState = null, IPostureNotificationChannel? postureNotificationChannel = null, ICloudAuthenticationLauncher? authenticationLauncher = null)
    {
        _providers = providers.ToArray();
        _historyStore = historyStore;
        _kubernetesRbacProvider = kubernetesRbacProvider;
        _cloudConnectionState = cloudConnectionState;
        _postureNotificationChannel = postureNotificationChannel ?? new DisabledPostureNotificationChannel();
        _authenticationLauncher = authenticationLauncher;
    }

    public async Task<IReadOnlyList<LiveScanAccountRecord>> DiscoverAccountsAsync(CancellationToken cancellationToken = default)
    {
        var providers = _providers.Where(provider => _cloudConnectionState?.IsExplicitlyDisconnected(provider.ProviderType) != true).ToArray();
        var connected = _cloudConnectionState?.ConnectedProviders;
        if (connected is { Count: > 0 })
            providers = providers.Where(provider => connected.Contains(provider.ProviderType)).ToArray();

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

        _cloudConnectionState?.Update(readyProviders.Select(provider => provider.ProviderType));
        var accounts = new List<LiveScanAccountRecord>();
        foreach (var provider in readyProviders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var discovered = await provider.DiscoverAccountsAsync(cancellationToken);
            accounts.AddRange(discovered.Select(account => ToAccountRecord(provider.ProviderType, account)));
        }
        if (_kubernetesRbacProvider is not null)
        {
            var connectedProviders = readyProviders.Select(provider => provider.ProviderType).ToHashSet();
            var clusters = await _kubernetesRbacProvider.DiscoverClustersAsync(cancellationToken);
            accounts.AddRange(clusters
                .Where(cluster => connectedProviders.Count != 1 || connectedProviders.Contains(cluster.CloudProvider))
                .Select(cluster => new LiveScanAccountRecord(
                    cluster.CloudProvider.ToString(), cluster.Name, cluster.ExternalAccountId ?? string.Empty,
                    cluster.Description ?? string.Empty, "Kubernetes RBAC", "Available")
                { CloudAccountId = cluster.Id }));
        }
        return accounts;
    }

    public Task<LiveCloudScanResult> ScanAsync(CancellationToken cancellationToken = default)
        => ScanAsync(null, cancellationToken);

    public Task<LiveCloudScanResult> ScanAsync(IReadOnlySet<string>? selectedAccountKeys, CancellationToken cancellationToken = default)
        => ScanAsync(selectedAccountKeys, null, cancellationToken);

    public async Task<LiveCloudScanResult> ScanAsync(IReadOnlySet<string>? selectedAccountKeys, IProgress<LiveScanProgress>? progress, CancellationToken cancellationToken = default)
    {
        await _scanGate.WaitAsync(cancellationToken);
        try
        {
            return await ScanCoreAsync(selectedAccountKeys, progress, cancellationToken);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    public async Task<LiveCloudScanResult> ScanVerifiedAccountAsync(LiveScanAccountRecord account, CloudAccountVerification verification, CancellationToken cancellationToken = default)
    {
        if (_authenticationLauncher is null)
            throw new InvalidOperationException("Verified-account scanning requires cloud identity verification.");
        if (!string.Equals(account.Provider, verification.Provider, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(account.AccountId, verification.AccountId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected account does not match the verified scope.");
        if (!Enum.TryParse<CloudProviderType>(account.Provider, true, out var provider)
            || !_providers.Any(item => item.ProviderType == provider))
            throw new InvalidOperationException("The verified cloud provider is not configured for scanning.");
        await _scanGate.WaitAsync(cancellationToken);
        try
        {
            if (_cloudConnectionState?.IsExplicitlyDisconnected(provider) == true)
                throw new InvalidOperationException("Connect the verified provider before scanning.");
            CloudAccountVerifier.RequireSameIdentity(verification, await _authenticationLauncher.VerifyAccountAsync(account, cancellationToken));
            return await ScanCoreAsync(new HashSet<string> { account.SelectionKey }, null, cancellationToken, account, verification);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private async Task ReverifyAccountAsync(LiveScanAccountRecord account, CloudAccountVerification? verification, CancellationToken cancellationToken)
    {
        if (_authenticationLauncher is null || verification is null)
            throw new InvalidOperationException("Verified-account scanning requires a verified identity.");
        CloudAccountVerifier.RequireSameIdentity(verification, await _authenticationLauncher.VerifyAccountAsync(account, cancellationToken));
    }

    private static string ScanProfile(string profile, CloudProviderType provider)
        => provider == CloudProviderType.Aws && string.IsNullOrEmpty(profile) ? "default" : profile;

    private async Task<LiveCloudScanResult> ScanCoreAsync(IReadOnlySet<string>? selectedAccountKeys, IProgress<LiveScanProgress>? progress, CancellationToken cancellationToken,
        LiveScanAccountRecord? verifiedAccount = null, CloudAccountVerification? verification = null)
    {
        var startedAt = DateTime.UtcNow;
        var accountsScanned = 0;
        var accountRecords = new List<LiveScanAccountRecord>();
        var accountsDiscovered = new List<CloudAccount>();
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
        var graphAssignments = new List<LiveResourceAccessAssignment>();
        var resourceSecuritySignals = new List<ResourceSecuritySignal>();
        var resourceSecurityCoverage = new List<ResourceSecurityEvidenceCoverage>();
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
        var selectedProvider = verifiedAccount is not null
            ? Enum.Parse<CloudProviderType>(verifiedAccount.Provider, true)
            : connectedProviders.Count == 1 ? connectedProviders.Single() : (CloudProviderType?)null;
        if (selectedProvider.HasValue)
        {
            messages.Add($"{selectedProvider.Value}: scan is restricted to the connected provider.");
        }

        foreach (var provider in _providers.Where(provider => !selectedProvider.HasValue || provider.ProviderType == selectedProvider.Value))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_cloudConnectionState?.IsExplicitlyDisconnected(provider.ProviderType) == true)
            {
                messages.Add($"{provider.ProviderType}: disconnected from YASK-CIEM. Use Connect Cloud first.");
                coverage.Add(new ScanCoverageRecord(provider.ProviderType.ToString(), "CLI sign-in", ScanCoverageStatus.Skipped, 0, "Provider was explicitly disconnected from YASK-CIEM."));
                continue;
            }
            try
            {
                if (verifiedAccount is null && !await provider.TestConnectionAsync(cancellationToken))
                {
                    if (_cloudConnectionState?.ConnectedProviders.Contains(provider.ProviderType) == true)
                        _cloudConnectionState.Update(Array.Empty<CloudProviderType>());
                    messages.Add($"{provider.ProviderType}: no active CLI sign-in. Use Connect Cloud first.");
                    coverage.Add(new ScanCoverageRecord(provider.ProviderType.ToString(), "CLI sign-in", ScanCoverageStatus.Unavailable, 0, "No active CLI sign-in was detected."));
                    continue;
                }

                if (verifiedAccount is null) _cloudConnectionState?.Update(new[] { provider.ProviderType });
                var accounts = await CollectAsync(provider.ProviderType.ToString(), string.Empty, "Cloud accounts", () => provider.DiscoverAccountsAsync(cancellationToken), coverage, messages, progress, cancellationToken);
                var selectedAccounts = accounts.Where(account => verifiedAccount is not null
                    ? string.Equals(GetStableAccountId(account), verifiedAccount.AccountId, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(ScanProfile(ExtractAwsProfile(account.Description, provider.ProviderType), provider.ProviderType),
                            ScanProfile(verifiedAccount.Profile, provider.ProviderType), StringComparison.Ordinal)
                    : selectedAccountKeys is null || selectedAccountKeys.Contains(ToAccountRecord(provider.ProviderType, account).SelectionKey)).ToArray();
                if (verifiedAccount is not null)
                {
                    if (selectedAccounts.Length != 1)
                        throw new InvalidOperationException("The verified account/profile was not uniquely discovered. No account inventory was collected; refresh discovery and verification.");
                    await ReverifyAccountAsync(verifiedAccount, verification, cancellationToken);
                }
                accountsDiscovered.AddRange(selectedAccounts);
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
                    var identities = await CollectAsync(provider.ProviderType.ToString(), scanScope, "Identities", () => provider.DiscoverIdentitiesAsync(account.Id, cancellationToken), coverage, messages, progress, cancellationToken);
                    identitiesScanned += identities.Count;
                    identityRecords.AddRange(identities);
                    privilegedIdentities += identities.Count(identity => identity.IsPrivileged);

                    var roles = await CollectAsync(provider.ProviderType.ToString(), scanScope, "Roles", () => provider.DiscoverRolesAsync(account.Id, cancellationToken), coverage, messages, progress, cancellationToken);
                    rolesScanned += roles.Count;
                    roleRecords.AddRange(roles.Select(role => new DiscoveredCloudRole(provider.ProviderType, role)));

                    var permissions = await CollectAsync(provider.ProviderType.ToString(), scanScope, "Permissions", () => provider.DiscoverPermissionsAsync(account.Id, cancellationToken), coverage, messages, progress, cancellationToken);
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
                        var resources = await CollectAsync(provider.ProviderType.ToString(), scanScope, "Resources", () => resourceProvider.GetResourcesAsync(account.Id, cancellationToken), coverage, messages, progress, cancellationToken);
                        resourcesScanned += resources.Count;
                        resourceRecords.AddRange(resources);
                        var mappedResources = resources.Select(resource => ResourceSecuritySignalMapper.MapTags(resource, provider.ProviderType)).ToArray();
                        resourceSecuritySignals.AddRange(mappedResources.SelectMany(mapping => mapping.Signals));
                        resourceSecurityCoverage.AddRange(mappedResources.SelectMany(mapping => mapping.Coverage));
                        var resourceInventoryStatus = coverage.LastOrDefault(item => item.Provider == provider.ProviderType.ToString()
                            && item.DataSource == $"{scanScope}: Resources")?.Status;
                        var signalCoverageStatus = resourceInventoryStatus switch
                        {
                            ScanCoverageStatus.Collected => ScanCoverageStatus.Partial,
                            ScanCoverageStatus.NoData => ScanCoverageStatus.NoData,
                            ScanCoverageStatus.Denied => ScanCoverageStatus.Denied,
                            ScanCoverageStatus.Unavailable => ScanCoverageStatus.Unavailable,
                            ScanCoverageStatus.Unsupported => ScanCoverageStatus.Unsupported,
                            ScanCoverageStatus.Skipped => ScanCoverageStatus.Skipped,
                            ScanCoverageStatus.Partial => ScanCoverageStatus.Partial,
                            _ => ScanCoverageStatus.Unavailable
                        };
                        foreach (var signalType in Enum.GetValues<ResourceSecuritySignalType>().Where(type => type != ResourceSecuritySignalType.WorkloadIdentityRisk))
                        {
                            coverage.Add(new ScanCoverageRecord(provider.ProviderType.ToString(), $"{scanScope}: {signalType} signals",
                                signalCoverageStatus,
                                mappedResources.Sum(mapping => mapping.Coverage.Count(item => item.SignalType == signalType)),
                                "Derived from resource tags/labels only; provider-native security findings and complete negative coverage are not asserted."));
                        }
                    }
                    else
                    {
                        coverage.Add(new ScanCoverageRecord(provider.ProviderType.ToString(), $"{scanScope}: Resources", ScanCoverageStatus.Unsupported, 0, "This provider adapter does not expose resource inventory."));
                    }

                    if (provider is ICloudEffectiveAccessProvider accessProvider)
                    {
                        var access = await CollectAsync(provider.ProviderType.ToString(), scanScope, "Effective access", () => accessProvider.DiscoverEffectiveAccessAsync(account.Id, cancellationToken), coverage, messages, progress, cancellationToken);
                        graphAssignments.AddRange(access.Where(item => !item.AssignmentType.Equals("Role usage", StringComparison.OrdinalIgnoreCase))
                            .Select(item => new LiveResourceAccessAssignment(provider.ProviderType, account.Id, item)));
                        effectiveAccessRecords.AddRange(access.Select(item => new LiveEffectiveAccessRecord(
                            provider.ProviderType.ToString(), item.Principal, item.Role, item.Scope, item.Source, item.IsPrivileged, item.AssignmentType, item.LastUsedAt, item.LastChangedAt, item.LastChangedBy, account.ExternalAccountId ?? account.SubscriptionId ?? account.ProjectId ?? account.Id.ToString(), item.UsageHistoryComplete, item.UsageHistoryDays, item.UsageEvidenceSource)));
                        if (provider.ProviderType == CloudProviderType.Aws)
                        {
                            var roleUsage = access.Where(item => item.AssignmentType.Equals("Role usage", StringComparison.OrdinalIgnoreCase)).ToArray();
                            var effectiveCoverage = coverage.LastOrDefault(item => item.Provider == provider.ProviderType.ToString()
                                && item.DataSource == $"{scanScope}: Effective access");
                            var roleUsageStatus = effectiveCoverage is null ? ScanCoverageStatus.Unavailable
                                : effectiveCoverage.Status != ScanCoverageStatus.Collected ? effectiveCoverage.Status
                                : roleUsage.All(item => item.UsageHistoryComplete == true) ? ScanCoverageStatus.Collected
                                : ScanCoverageStatus.Partial;
                            coverage.Add(new ScanCoverageRecord(provider.ProviderType.ToString(), $"{scanScope}: AWS IAM role last-used", roleUsageStatus, roleUsage.Length,
                                "AWS IAM ListRoles.RoleLastUsed; role-level activity only, with at most 400 days of history. This does not prove individual actions or resource usage."));
                            coverage.Add(new ScanCoverageRecord(provider.ProviderType.ToString(), $"{scanScope}: AWS action-level usage", ScanCoverageStatus.Unsupported, 0,
                                "Per-action/resource CloudTrail events are not yet normalized into permission usage."));
                        }
                        else
                        {
                            coverage.Add(new ScanCoverageRecord(provider.ProviderType.ToString(), $"{scanScope}: permission usage", ScanCoverageStatus.Unsupported, 0,
                                provider.ProviderType == CloudProviderType.Azure
                                    ? "The current Azure Activity Log integration reports RBAC changes, not complete permission-use telemetry."
                                    : "GCP Cloud Audit Logs are not yet ingested for permission-use analysis."));
                        }
                    }
                    else
                    {
                        coverage.Add(new ScanCoverageRecord(provider.ProviderType.ToString(), $"{scanScope}: Effective access", ScanCoverageStatus.Unsupported, 0, "This provider adapter does not expose effective-access assignments."));
                    }

                    if (provider is IAccessAnalyzerFindingsProvider analyzerProvider)
                    {
                        var analyzerFindings = await CollectAsync(provider.ProviderType.ToString(), scanScope, "Access analyzer", () => analyzerProvider.DiscoverAccessAnalyzerFindingsAsync(account.Id, cancellationToken), coverage, messages, progress, cancellationToken);
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

                if (verifiedAccount is not null)
                {
                    coverage.Add(new ScanCoverageRecord(provider.ProviderType.ToString(), "Entra directory and PIM", ScanCoverageStatus.Skipped, 0, "Verified-account scan excludes provider-wide directory collection."));
                }
                else if (provider is IEntraDirectoryProvider entraProvider)
                {
                    try
                    {
                        var directoryAccount = selectedAccounts.FirstOrDefault();
                        var entraResult = await entraProvider.DiscoverEntraDirectoryAsync(directoryAccount?.Id ?? Guid.Empty, cancellationToken);
                        var newPrincipals = new List<CloudPrincipal>();
                        foreach (var principal in entraResult.Principals)
                        {
                            var existing = principal.PrincipalId is null ? null : identityRecords.FirstOrDefault(identity =>
                                identity.CloudProvider == principal.CloudProvider
                                && string.Equals(identity.PrincipalId, principal.PrincipalId, StringComparison.OrdinalIgnoreCase));
                            if (existing is null)
                            {
                                newPrincipals.Add(principal);
                                continue;
                            }

                            MergeIdentityEvidence(existing, principal);
                        }
                        identityRecords.AddRange(newPrincipals);
                        identitiesScanned += newPrincipals.Count;
                        privilegedIdentities += newPrincipals.Count(identity => identity.IsPrivileged);
                        effectiveAccessRecords.AddRange(entraResult.AccessAssignments.Select(item => new LiveEffectiveAccessRecord(
                            provider.ProviderType.ToString(), item.Principal, item.Role, item.Scope, item.Source, item.IsPrivileged, item.AssignmentType, item.LastUsedAt, item.LastChangedAt, item.LastChangedBy, directoryAccount?.ExternalAccountId ?? directoryAccount?.SubscriptionId ?? directoryAccount?.TenantId)));
                        if (directoryAccount is not null)
                            graphAssignments.AddRange(entraResult.AccessAssignments.Select(item => new LiveResourceAccessAssignment(provider.ProviderType, directoryAccount.Id, item)));
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

                if (verifiedAccount is not null)
                {
                    coverage.Add(new ScanCoverageRecord(provider.ProviderType.ToString(), "Organization hierarchy", ScanCoverageStatus.Skipped, 0, "Verified-account scan excludes organization roster collection."));
                }
                else if (provider is ICloudOrganizationProvider organizationProvider)
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

        if (verifiedAccount is not null)
        {
            coverage.Add(new ScanCoverageRecord(verifiedAccount.Provider, "Cluster accounts", ScanCoverageStatus.Skipped, 0, "Verified-account scan excludes Kubernetes contexts."));
        }
        else if (_kubernetesRbacProvider is not null)
        {
            try
            {
                var selectedClusterKeys = selectedAccountKeys;
                if (selectedAccountKeys is not null)
                {
                    var discoveredClusters = await _kubernetesRbacProvider.DiscoverClustersAsync(cancellationToken);
                    selectedClusterKeys = discoveredClusters
                        .Select(cluster => LiveScanAccountRecord.CreateSelectionKey(
                            cluster.CloudProvider.ToString(), cluster.ExternalAccountId ?? string.Empty, cluster.Description ?? string.Empty))
                        .Where(selectedAccountKeys.Contains)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (selectedClusterKeys.Count == 0)
                    {
                        coverage.Add(new ScanCoverageRecord("Kubernetes", "Cluster accounts", ScanCoverageStatus.Skipped, 0, "No Kubernetes contexts were selected for this scan."));
                        messages.Add("Kubernetes cluster scanning was skipped because no Kubernetes contexts were selected.");
                    }
                }

                if (selectedClusterKeys is null || selectedClusterKeys.Count > 0)
                {
                    var kubernetesResult = await _kubernetesRbacProvider.ScanKubernetesRbacAsync(cancellationToken, selectedProvider, selectedClusterKeys);
                    messages.AddRange(kubernetesResult.ProviderMessages);
                    coverage.AddRange(kubernetesResult.Coverage);
                    workloadTraces.AddRange(kubernetesResult.Clusters.SelectMany(cluster => cluster.WorkloadTraces));
                    foreach (var cluster in kubernetesResult.Clusters.Where(cluster => !selectedProvider.HasValue || cluster.Cluster.CloudProvider == selectedProvider.Value))
                    {
                        var providerType = cluster.Cluster.CloudProvider;
                    accountsDiscovered.Add(cluster.Cluster);
                    accountsScanned++;
                    accountRecords.Add(new LiveScanAccountRecord(providerType.ToString(), cluster.Cluster.Name, cluster.Cluster.ExternalAccountId ?? string.Empty, cluster.Cluster.Description ?? string.Empty, "Kubernetes RBAC", $"Scanned: {cluster.Cluster.ConnectionStatus}; read-only scan") { CloudAccountId = cluster.Cluster.Id });
                    var clusterResources = cluster.Resources.ToList();
                    var workloadObservedAt = DateTime.UtcNow;
                    foreach (var trace in cluster.WorkloadTraces)
                    {
                        var workloadResourceId = $"clusters/{trace.Cluster}/namespaces/{trace.Namespace}/{trace.WorkloadKind.ToLowerInvariant()}/{trace.WorkloadName}";
                        var workloadResource = clusterResources.FirstOrDefault(resource => resource.ResourceId?.Equals(workloadResourceId, StringComparison.OrdinalIgnoreCase) == true)
                            ?? new CloudResource
                            {
                                Id = Guid.NewGuid(), AccountId = cluster.Cluster.Id, Name = $"{trace.Namespace}/{trace.WorkloadName}",
                                Type = $"Kubernetes {trace.WorkloadKind}", ResourceType = ResourceType.Resource,
                                ResourceId = workloadResourceId, ResourceUri = workloadResourceId, Scope = $"namespaces/{trace.Namespace}",
                                UpdatedAt = workloadObservedAt,
                                Tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                                {
                                    ["WorkloadKind"] = trace.WorkloadKind,
                                    ["ServiceAccount"] = trace.ServiceAccount,
                                    ["TokenInjectionEnabled"] = trace.TokenInjectionEnabled.ToString()
                                }
                            };
                        if (!clusterResources.Contains(workloadResource)) clusterResources.Add(workloadResource);
                        var workloadMapping = ResourceSecuritySignalMapper.MapWorkloadTrust(trace, workloadResource, workloadObservedAt);
                        resourceSecuritySignals.AddRange(workloadMapping.Signals);
                        resourceSecurityCoverage.AddRange(workloadMapping.Coverage);
                    }
                    resourcesScanned += clusterResources.Count;
                    resourceRecords.AddRange(clusterResources);
                    var mappedResources = clusterResources.Select(resource => ResourceSecuritySignalMapper.MapTags(resource, providerType)).ToArray();
                    resourceSecuritySignals.AddRange(mappedResources.SelectMany(mapping => mapping.Signals));
                    resourceSecurityCoverage.AddRange(mappedResources.SelectMany(mapping => mapping.Coverage));
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
                    graphAssignments.AddRange(cluster.AccessAssignments.Select(item => new LiveResourceAccessAssignment(providerType, cluster.Cluster.Id, item)));
                    }
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

        var graphResult = new LiveResourceRiskGraphResult(Array.Empty<DomainAccessPath>(), Array.Empty<RiskFindingModel>(), Array.Empty<string>());
        var hasWorkloadIdentities = identityRecords.Any(principal => principal.IdentityType is IdentityType.ServicePrincipal
            or IdentityType.ServiceAccount or IdentityType.ManagedIdentity or IdentityType.Application);
        if ((graphAssignments.Count > 0 && resourceRecords.Count > 0) || hasWorkloadIdentities)
        {
            try
            {
                graphResult = await _resourceRiskGraphEvaluator.EvaluateAsync(accountsDiscovered, identityRecords, resourceRecords,
                    roleRecords.Select(item => item.Role).ToArray(), permissionRecords.Select(item => item.Permission).ToArray(),
                    graphAssignments, resourceSecuritySignals, resourceSecurityCoverage, DateTime.UtcNow, cancellationToken);
                findings.AddRange(graphResult.Findings.Select(finding => ToCloudFinding(finding, identityRecords, resourceRecords, permissionRecords)));
                messages.AddRange(graphResult.Diagnostics);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var diagnostic = $"Live access-path risk evaluation was incomplete: {exception.Message}";
                graphResult = graphResult with { Diagnostics = graphResult.Diagnostics.Append(diagnostic).ToArray() };
                coverage.Add(new ScanCoverageRecord("Risk graph", "Live access paths", ScanCoverageStatus.Partial, 0, diagnostic));
                messages.Add(diagnostic);
            }
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
            RiskFindingDetails = riskFindingDetails,
            ResourceSecuritySignals = resourceSecuritySignals,
            ResourceSecurityCoverage = resourceSecurityCoverage,
            ModeledAccessPaths = graphResult.AccessPaths,
            ResourceRiskFindings = graphResult.Findings
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
            ResourceSecuritySignals = resourceSecuritySignals,
            ResourceSecurityCoverage = resourceSecurityCoverage,
            ModeledAccessPaths = graphResult.AccessPaths,
            ResourceRiskFindings = graphResult.Findings,
            RiskGraphDiagnostics = graphResult.Diagnostics,
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

    private static void MergeIdentityEvidence(CloudPrincipal target, CloudPrincipal source)
    {
        if (source.OwnerInventoryComplete.HasValue)
        {
            target.OwnerInventoryComplete = source.OwnerInventoryComplete;
            target.OwnerIds = source.OwnerIds.ToList();
        }
        target.CredentialInventoryComplete = source.CredentialInventoryComplete ?? target.CredentialInventoryComplete;
        target.CredentialLastRotatedAtUtc = source.CredentialLastRotatedAtUtc ?? target.CredentialLastRotatedAtUtc;
        target.CredentialExpiresAtUtc = source.CredentialExpiresAtUtc ?? target.CredentialExpiresAtUtc;
        target.CredentialEvidenceSource = source.CredentialEvidenceSource ?? target.CredentialEvidenceSource;
        target.OwnerEvidenceSource = source.OwnerEvidenceSource ?? target.OwnerEvidenceSource;
        target.FederationInventoryComplete = source.FederationInventoryComplete ?? target.FederationInventoryComplete;
        target.FederationIssuer = source.FederationIssuer ?? target.FederationIssuer;
        target.FederationSubject = source.FederationSubject ?? target.FederationSubject;
        target.FederationAudience = source.FederationAudience ?? target.FederationAudience;
        target.FederationTrustVerified = source.FederationTrustVerified ?? target.FederationTrustVerified;
        if (source.FederationInventoryComplete.HasValue)
            target.FederationTrusts = source.FederationTrusts.ToList();
        target.LastObservedWorkloadAtUtc = source.LastObservedWorkloadAtUtc ?? target.LastObservedWorkloadAtUtc;
        if (source.ActivityEvidenceComplete.HasValue)
        {
            target.ActivityEvidenceComplete = source.ActivityEvidenceComplete;
            target.ActivityObservationDays = source.ActivityObservationDays;
            target.ActivityEvidenceSource = source.ActivityEvidenceSource;
            target.LastActivityAt = source.LastActivityAt ?? target.LastActivityAt;
        }
    }

    private static RiskFinding ToCloudFinding(
        RiskFindingModel finding,
        IReadOnlyList<CloudPrincipal> principals,
        IReadOnlyList<CloudResource> resources,
        IReadOnlyList<DiscoveredCloudPermission> permissions)
    {
        var providerType = Enum.TryParse<CloudProviderType>(finding.CloudProvider.ToString(), true, out var parsedProvider)
            ? parsedProvider : CloudProviderType.Azure;
        var principal = principals.FirstOrDefault(item => item.Id == finding.PrincipalId);
        var resource = finding.ResourceId.HasValue ? resources.FirstOrDefault(item => item.Id == finding.ResourceId.Value) : null;
        var permission = finding.PermissionId.HasValue ? permissions.FirstOrDefault(item => item.Permission.Id == finding.PermissionId.Value)?.Permission : null;
        var detailText = finding.Evidence.Details.Count == 0
            ? string.Empty
            : string.Join("; ", finding.Evidence.Details.Select(item => $"{item.Key}={item.Value}"));
        return new RiskFinding
        {
            Id = finding.Id,
            CloudProvider = providerType,
            Severity = finding.Severity,
            Title = finding.Title,
            Description = finding.Description,
            Principal = principal?.Name ?? finding.PrincipalId.ToString("D"),
            Resource = resource?.Name ?? finding.ResourceId?.ToString("D") ?? "Unknown resource",
            Permission = permission?.Name ?? finding.PermissionId?.ToString("D") ?? "Role-level access",
            Evidence = string.IsNullOrWhiteSpace(detailText) ? finding.Evidence.Summary : $"{finding.Evidence.Summary}; {detailText}",
            Recommendation = finding.Recommendation ?? "Review the modeled identity-to-resource path and validate its partial provider evidence.",
            CreatedAt = finding.CreatedAt,
            RuleId = finding.RuleId,
            CloudAccountId = finding.CloudAccountId,
            PrincipalId = finding.PrincipalId,
            ResourceId = finding.ResourceId,
            EffectiveAccessId = finding.EffectiveAccessId,
            Status = finding.Status,
            UpdatedAt = finding.LastEvaluatedAt
        };
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
        IProgress<LiveScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new LiveScanProgress(provider, account, dataSource, LiveScanProgressState.Started));
        try
        {
            var records = await discover();
            coverage.Add(new ScanCoverageRecord(provider, CoverageName(account, dataSource), records.Count > 0 ? ScanCoverageStatus.Collected : ScanCoverageStatus.NoData, records.Count, records.Count > 0 ? $"{records.Count} record(s) collected." : "The source was queried successfully and returned no records."));
            progress?.Report(new LiveScanProgress(provider, account, dataSource, LiveScanProgressState.Completed, records.Count));
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
            progress?.Report(new LiveScanProgress(provider, account, dataSource, LiveScanProgressState.Failed));
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
