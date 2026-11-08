using System.Net.Http;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Integration.Contracts;

namespace CloudEntitle.Desktop.ViewModels;

public sealed class AuthoritativeDecisionsViewModel : ObservableObject
{
    private readonly ISharedEffectiveDecisionApi? _api;
    private Guid? _organization;
    private string _context = "";
    private bool _canRead, _canCapture, _consent, _busy;
    private long _generation;
    private CancellationTokenSource? _cancellation;
    private IReadOnlyList<EffectiveDecisionSource> _sources = [];
    private IReadOnlyList<EffectiveDecisionSnapshot> _history = [];
    private EffectiveDecisionSource? _source;
    private EffectiveDecisionSnapshot? _before, _after;
    private DecisionEscalationReport? _report;
    private string _principal = "", _permission = "", _status = "Load configured connections explicitly. No provider is contacted on sign-in.";

    public AuthoritativeDecisionsViewModel(ISharedEffectiveDecisionApi? api)
    {
        _api = api;
        LoadSourcesCommand = new AsyncRelayCommand(LoadSourcesAsync);
        CaptureCommand = new AsyncRelayCommand(CaptureAsync);
        LoadHistoryCommand = new AsyncRelayCommand(LoadHistoryAsync);
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync);
        CancelCommand = new RelayCommand(Cancel);
    }
    public AsyncRelayCommand LoadSourcesCommand { get; }
    public AsyncRelayCommand CaptureCommand { get; }
    public AsyncRelayCommand LoadHistoryCommand { get; }
    public AsyncRelayCommand AnalyzeCommand { get; }
    public RelayCommand CancelCommand { get; }
    public IReadOnlyList<EffectiveDecisionSource> Sources { get => _sources; private set => SetProperty(ref _sources, value); }
    public IReadOnlyList<EffectiveDecisionSnapshot> History { get => _history; private set => SetProperty(ref _history, value); }
    public bool Busy { get => _busy; private set => SetProperty(ref _busy, value); }
    public bool ReadConsent { get => _consent; set => SetProperty(ref _consent, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public DecisionEscalationReport? Report { get => _report; private set => SetProperty(ref _report, value); }
    public EffectiveDecisionSource? SelectedSource
    {
        get => _source;
        set
        {
            if (!SetProperty(ref _source, value)) return;
            ResetResults();
            SetProperty(ref _principal, value?.Principals.FirstOrDefault() ?? "", nameof(Principal));
            SetProperty(ref _permission, value?.PrivilegedPermissions.FirstOrDefault() ?? "", nameof(Permission));
            OnPropertyChanged(nameof(SetupInstructions));
            OnPropertyChanged(nameof(Principals));
            OnPropertyChanged(nameof(Permissions));
            OnPropertyChanged(nameof(FullResourceName));
        }
    }
    public IReadOnlyList<string> Principals => SelectedSource?.Principals ?? [];
    public IReadOnlyList<string> Permissions => SelectedSource?.PrivilegedPermissions ?? [];
    public string FullResourceName => SelectedSource?.FullResourceName ?? "";
    public string Principal { get => _principal; set { if (SetProperty(ref _principal, value)) ResetResults(); } }
    public string Permission { get => _permission; set { if (SetProperty(ref _permission, value)) ResetResults(); } }
    public EffectiveDecisionSnapshot? Before { get => _before; set { if (SetProperty(ref _before, value)) { ReadConsent = false; Report = null; InvalidatePending(); } } }
    public EffectiveDecisionSnapshot? After { get => _after; set { if (SetProperty(ref _after, value)) { ReadConsent = false; Report = null; InvalidatePending(); } } }
    public string SetupInstructions => (SelectedSource?.CoverageNotice ?? "Select a server-configured client connection.")
        + " An administrator configures the exact project, actor, principal sample and privileged permission allowlist. Connect OAuth securely on the server with cloud-platform and userinfo.email scopes; no token is entered in this Desktop. Accept GCP PAB preview explicitly. Each native read requires new consent; history and analysis only read saved evidence.";

    public void SetContext(Guid? organization, string contextIdentity, bool canRead, bool canCapture)
    {
        if (_organization == organization && _context == contextIdentity && _canRead == canRead && _canCapture == canCapture) return;
        _organization = organization;
        _context = contextIdentity;
        _canRead = canRead;
        _canCapture = canCapture;
        ResetResults();
        Sources = [];
        SelectedSource = null;
        Status = "Load configured connections explicitly. No provider is contacted on sign-in.";
    }

    private void InvalidatePending()
    {
        _generation++;
        _cancellation?.Cancel();
        _cancellation = null;
        Busy = false;
        ReadConsent = false;
    }
    private void ResetResults()
    {
        InvalidatePending();
        History = [];
        SetProperty(ref _before, null, nameof(Before));
        SetProperty(ref _after, null, nameof(After));
        Report = null;
        Status = "Choose a configured sample. History is saved evidence only; live capture requires explicit consent.";
    }
    public void Cancel()
    {
        InvalidatePending();
        Report = null;
        Status = "Cancelled. Late results are discarded; a read already completed on the server may remain in immutable history.";
    }
    private bool CanRun(bool capture = false)
    {
        if (_api is not null && _organization is not null && _canRead && (!capture || _canCapture) && !Busy) return true;
        Status = "A shared organization and inventory/finding permissions are required; capture and analysis also require ManagePolicies.";
        return false;
    }
    private async Task RunAsync(Func<Guid, CancellationToken, long, Task> operation)
    {
        var generation = _generation;
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        Busy = true;
        try { await operation(_organization!.Value, cancellation.Token, generation); }
        catch (OperationCanceledException) { if (generation == _generation) Status = "Cancelled; no confirmation was inferred."; }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        { if (generation == _generation) Status = "The configured native connection or saved evidence is unavailable. No confirmation was inferred."; }
        finally
        {
            if (generation == _generation) { _cancellation = null; Busy = false; }
            cancellation.Dispose();
        }
    }
    private bool Current(long generation, CancellationToken token) => generation == _generation && !token.IsCancellationRequested;
    public async Task LoadSourcesAsync()
    {
        if (!CanRun()) return;
        await RunAsync(async (organization, token, generation) =>
        {
            var sources = await _api!.GetEffectiveDecisionSourcesAsync(organization, token);
            if (!Current(generation, token)) return;
            Sources = sources;
            Status = $"{sources.Count} configured connections loaded. No provider was contacted.";
        });
    }
    public async Task LoadHistoryAsync()
    {
        if (!CanRun() || SelectedSource is null) return;
        var source = SelectedSource;
        ReadConsent = false;
        Report = null;
        await RunAsync(async (organization, token, generation) =>
        {
            var history = await _api!.GetEffectiveDecisionHistoryAsync(organization, source.SourceId, token);
            if (!Current(generation, token)) return;
            History = history;
            Status = $"{history.Count} immutable native observations loaded. No provider was contacted.";
        });
    }
    public async Task CaptureAsync()
    {
        if (!CanRun(true) || SelectedSource is null) return;
        if (!ReadConsent) { Status = "Explicitly authorize one native read with new per-run consent."; return; }
        var request = new CaptureEffectiveDecisionRequest(SelectedSource.SourceId, Principal, Permission, FullResourceName, true);
        ReadConsent = false;
        Report = null;
        await RunAsync(async (organization, token, generation) =>
        {
            var snapshot = await _api!.CaptureEffectiveDecisionAsync(organization, request, token);
            if (!Current(generation, token)) return;
            History = new[] { snapshot }.Concat(History.Where(s => s.SnapshotId != snapshot.SnapshotId))
                .OrderByDescending(s => s.CompletedAtUtc).Take(50).ToArray();
            Status = $"{snapshot.Decision} / {snapshot.Coverage}: {string.Join(", ", snapshot.Blockers)}. A single capture is never an escalation.";
        });
    }
    public async Task AnalyzeAsync()
    {
        if (!CanRun(true) || SelectedSource is null || Before is null || After is null) return;
        var request = new AnalyzeDecisionEscalationRequest(SelectedSource.SourceId, Before.SnapshotId, After.SnapshotId);
        ReadConsent = false;
        Report = null;
        await RunAsync(async (organization, token, generation) =>
        {
            var report = await _api!.AnalyzeDecisionEscalationAsync(organization, request, token);
            if (!Current(generation, token)) return;
            Report = report;
            Status = report.Status + ": " + string.Join(", ", report.Blockers);
        });
    }
}
