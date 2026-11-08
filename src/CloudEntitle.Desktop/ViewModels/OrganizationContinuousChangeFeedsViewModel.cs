using CloudEntitle.Desktop.Services;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.OrganizationManagement.Models;

namespace CloudEntitle.Desktop.ViewModels;

public sealed partial class OrganizationViewModel
{
    private IReadOnlyList<ContinuousChangeFeedStatus> _continuousFeeds = [];
    private IReadOnlyList<StoredContinuousChange> _continuousChanges = [];
    private ContinuousChangeFeedStatus? _continuousFeed;
    private Guid? _continuousOrganization;
    private OrganizationIdentity? _continuousIdentity;
    private bool _continuousConsent;
    private bool _continuousBusy;
    private bool _continuousReset;
    private long _continuousContextVersion;
    private string _continuousStatus = "Refresh status explicitly. No Desktop sign-in polling or cloud provisioning.";
    private AsyncRelayCommand? _refreshContinuousFeeds;
    private AsyncRelayCommand? _connectContinuousFeed;
    private AsyncRelayCommand? _pauseContinuousFeed;
    private AsyncRelayCommand? _loadContinuousChanges;

    private bool ContinuousContextMatches => _continuousOrganization == SelectedOrganization?.Id && _continuousIdentity == _identity;
    public bool CanReadContinuousFeeds => CanReadMonitoring && _sharedOrganizationApi is ISharedContinuousChangeFeedApi;
    public bool CanManageContinuousFeeds => CanReadContinuousFeeds && IsAllowed(OrganizationAction.ManageConnections)
        && IsAllowed(OrganizationAction.ManagePolicies);
    public IReadOnlyList<ContinuousChangeFeedStatus> ContinuousFeeds => ContinuousContextMatches ? _continuousFeeds : [];
    public IReadOnlyList<StoredContinuousChange> ContinuousChanges => ContinuousContextMatches ? _continuousChanges : [];
    public ContinuousChangeFeedStatus? SelectedContinuousFeed
    {
        get => ContinuousContextMatches ? _continuousFeed : null;
        set
        {
            EnsureContinuousContext();
            _continuousOrganization = SelectedOrganization?.Id;
            _continuousIdentity = _identity;
            if (!SetProperty(ref _continuousFeed, value)) return;
            _continuousContextVersion++;
            _continuousChanges = [];
            ContinuousFeedConsent = false;
            ContinuousFeedResetSource = false;
            OnPropertyChanged(nameof(ContinuousChanges));
            OnPropertyChanged(nameof(ContinuousFeedCoverage));
        }
    }
    public IReadOnlyList<ChangeFeedCategoryCoverage> ContinuousFeedCoverage => SelectedContinuousFeed?.Coverage ?? [];
    public bool ContinuousFeedConsent
    {
        get => ContinuousContextMatches && _continuousConsent;
        set { EnsureContinuousContext(); SetProperty(ref _continuousConsent, value); }
    }
    public bool ContinuousFeedResetSource
    {
        get => ContinuousContextMatches && _continuousReset;
        set { EnsureContinuousContext(); SetProperty(ref _continuousReset, value); }
    }
    public bool ContinuousFeedBusy { get => _continuousBusy; private set => SetProperty(ref _continuousBusy, value); }
    public string ContinuousFeedStatus => ContinuousContextMatches ? _continuousStatus : "Refresh server status explicitly for this organization; no source read runs at Desktop sign-in.";
    public string ContinuousFeedSetupInstructions =>
        "Client-owned setup on demand: Azure Activity Log/Entra diagnostic categories → Event Hub + normalized HTTPS bridge; "
        + "AWS organization/account CloudTrail IAM events → EventBridge rule + bridge; "
        + "GCP Admin Activity/Data Access IAM logs → Logging sink + Pub/Sub + bridge. "
        + "Client administrator must provision and authorize these resources, configure exact IAM/trust/key categories, backfill and gaps, "
        + "then ask the server operator to configure an HTTPS allowlisted alias and server-only secret. "
        + "Refresh status, select alias, explicitly consent and enable/connect. Pause stops future polls. "
        + "This application creates no provider resources and does not claim native polling or full coverage.";
    public AsyncRelayCommand RefreshContinuousFeedsCommand => _refreshContinuousFeeds ??= new(RefreshContinuousFeedsAsync);
    public AsyncRelayCommand ConnectContinuousFeedCommand => _connectContinuousFeed ??= new(ConnectContinuousFeedAsync);
    public AsyncRelayCommand PauseContinuousFeedCommand => _pauseContinuousFeed ??= new(PauseContinuousFeedAsync);
    public AsyncRelayCommand LoadContinuousChangesCommand => _loadContinuousChanges ??= new(LoadContinuousChangesAsync);

    public void ResetContinuousChangeFeeds()
    {
        _continuousContextVersion++;
        _continuousOrganization = null;
        _continuousIdentity = null;
        _continuousFeeds = [];
        _continuousChanges = [];
        _continuousFeed = null;
        _continuousConsent = false;
        _continuousReset = false;
        foreach (var property in new[] { nameof(ContinuousFeeds), nameof(ContinuousChanges), nameof(SelectedContinuousFeed),
            nameof(ContinuousFeedCoverage), nameof(ContinuousFeedConsent), nameof(ContinuousFeedResetSource),
            nameof(ContinuousFeedStatus), nameof(CanReadContinuousFeeds), nameof(CanManageContinuousFeeds) })
            OnPropertyChanged(property);
    }

    public Task RefreshContinuousFeedsAsync() => RunContinuousOperationAsync(false, async (api, organization, _, token) =>
    {
        var states = await api.GetContinuousChangeFeedsAsync(organization, token);
        return () =>
        {
            _continuousFeeds = states;
            _continuousFeed = states.FirstOrDefault(s => s.Alias == _continuousFeed?.Alias);
            _continuousConsent = false;
            _continuousStatus = $"Loaded {states.Count} server bridge alias(es). Saved status only; source coverage is unverified.";
            NotifyContinuousFeedState();
        };
    });

    public Task ConnectContinuousFeedAsync()
    {
        if (!ContinuousFeedConsent || SelectedContinuousFeed is null)
        {
            SetContinuousStatus("Select a configured alias and explicitly consent to continuous server-side read-only polling.");
            return Task.CompletedTask;
        }
        var reset = ContinuousFeedResetSource;
        return RunContinuousOperationAsync(true, async (api, organization, alias, token) =>
        {
            var state = await api.ConfigureContinuousChangeFeedAsync(organization, alias!, new(true, true, reset), token);
            return () =>
            {
                _continuousFeed = state;
                _continuousFeeds = _continuousFeeds.Where(s => s.Alias != state.Alias).Append(state).OrderBy(s => s.Alias).ToArray();
                _continuousConsent = false;
                _continuousReset = false;
                _continuousStatus = "Opt-in saved. The server worker will poll the configured bridge; source coverage remains unverified.";
                NotifyContinuousFeedState();
            };
        });
    }

    public Task PauseContinuousFeedAsync() => RunContinuousOperationAsync(true, async (api, organization, alias, token) =>
    {
        if (alias is null) throw new InvalidOperationException("Select a saved feed first.");
        await api.PauseContinuousChangeFeedAsync(organization, alias, token);
        var states = await api.GetContinuousChangeFeedsAsync(organization, token);
        return () =>
        {
            _continuousFeeds = states;
            _continuousFeed = states.FirstOrDefault(s => s.Alias == alias);
            _continuousConsent = false;
            _continuousStatus = "Feed paused. In-flight attempts are fenced; saved revisions remain available.";
            NotifyContinuousFeedState();
        };
    });

    public Task LoadContinuousChangesAsync() => RunContinuousOperationAsync(false, async (api, organization, alias, token) =>
    {
        if (alias is null) throw new InvalidOperationException("Select a feed first.");
        var changes = await api.GetContinuousCloudChangesAsync(organization, alias, token);
        return () => { _continuousChanges = changes; _continuousStatus = $"Loaded {changes.Count} saved normalized revisions; no live poll."; NotifyContinuousFeedState(); };
    });

    private async Task RunContinuousOperationAsync(bool write,
        Func<ISharedContinuousChangeFeedApi, Guid, string?, CancellationToken, Task<Action>> operation)
    {
        if ((write ? !CanManageContinuousFeeds : !CanReadContinuousFeeds) || SelectedOrganization is null
            || _sharedOrganizationApi is not ISharedContinuousChangeFeedApi api)
        {
            SetContinuousStatus("Shared active organization and inventory/findings access are required; changes also require ManageConnections and ManagePolicies.");
            return;
        }
        if (ContinuousFeedBusy) return;
        EnsureContinuousContext();
        var organization = SelectedOrganization.Id;
        var identity = _identity;
        var alias = SelectedContinuousFeed?.Alias;
        var version = _continuousContextVersion;
        _continuousOrganization = organization;
        _continuousIdentity = identity;
        ContinuousFeedBusy = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            var apply = await operation(api, organization, alias, timeout.Token);
            if (version == _continuousContextVersion && SelectedOrganization?.Id == organization && _identity == identity && SelectedContinuousFeed?.Alias == alias)
                apply();
        }
        catch (Exception)
        {
            if (version == _continuousContextVersion && SelectedOrganization?.Id == organization && _identity == identity)
                SetContinuousStatus("Feed operation failed or response was uncertain. Refresh saved server status before retrying; no success assumed.");
        }
        finally { ContinuousFeedBusy = false; }
    }

    private void SetContinuousStatus(string value)
    {
        EnsureContinuousContext();
        _continuousOrganization = SelectedOrganization?.Id;
        _continuousIdentity = _identity;
        _continuousStatus = value;
        OnPropertyChanged(nameof(ContinuousFeedStatus));
    }

    private void EnsureContinuousContext()
    {
        if (ContinuousContextMatches) return;
        ResetContinuousChangeFeeds();
        _continuousOrganization = SelectedOrganization?.Id;
        _continuousIdentity = _identity;
    }

    private void NotifyContinuousFeedState()
    {
        foreach (var property in new[] { nameof(ContinuousFeeds), nameof(SelectedContinuousFeed), nameof(ContinuousFeedCoverage),
            nameof(ContinuousChanges), nameof(ContinuousFeedStatus), nameof(ContinuousFeedConsent), nameof(ContinuousFeedResetSource) })
            OnPropertyChanged(property);
    }
}
