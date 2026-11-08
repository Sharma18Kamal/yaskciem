using System.IO;
using System.Data.Common;
using System.Text.Json;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Domain;
using CloudEntitle.EffectiveAccess.Models;
using CloudEntitle.EffectiveAccess.Services;
using CloudEntitle.OrganizationManagement.Models;
using CloudEntitle.OrganizationManagement.Services;

namespace CloudEntitle.Desktop.ViewModels;

public sealed record WhatIfTarget(WhatIfTargetKind Kind, Guid Id, string Name)
{
    public string DisplayName => $"{Kind}: {Name}";
}

public sealed class WhatIfSimulatorViewModel : WorkspaceViewModel
{
    private readonly IWhatIfSavedData _saved;
    private readonly WhatIfPermissionSimulator _simulator;
    private readonly IDialogService _dialogs;
    private readonly AuthenticatedOrganizationContext _context;
    private readonly OrganizationManagementService _organizations;
    private WhatIfInput? _input;
    private CloudAccount? _selectedAccount;
    private WhatIfTarget? _selectedTarget;
    private string _action = "Microsoft.Storage/storageAccounts/listKeys/action";
    private string _status = "Load saved local inventory or import a schema 1.0 offline authorization bundle. No live credentials or cloud calls.";
    private IReadOnlyList<CloudAccount> _accounts = Array.Empty<CloudAccount>();
    private IReadOnlyList<WhatIfTarget> _targets = Array.Empty<WhatIfTarget>();
    private WhatIfReport? _report;
    private int _version;
    private CancellationTokenSource? _pending;
    private Guid _accountId;
    private CloudProvider _provider;

    public WhatIfSimulatorViewModel(IDemoDataService demoData, IWhatIfSavedData saved,
        WhatIfPermissionSimulator simulator, IDialogService dialogs, AuthenticatedOrganizationContext context,
        OrganizationManagementService organizations) : base(demoData)
    {
        _saved = saved; _simulator = simulator; _dialogs = dialogs; _context = context; _organizations = organizations;
        RefreshAccountsCommand = new AsyncRelayCommand(RefreshAsync);
        LoadSavedCommand = new AsyncRelayCommand(LoadSavedAsync);
        ImportCommand = new AsyncRelayCommand(ImportAsync);
        SimulateCommand = new AsyncRelayCommand(SimulateAsync);
        ExportCommand = new AsyncRelayCommand(ExportAsync);
        _context.Changed += (_, _) => Reset();
        demoData.DataChanged += (_, _) => Reset();
    }

    public override string Title => "What-if Simulator";
    public override string Subtitle => "Preview one role/policy action removal across inventoried identities and resources. Offline predictions only.";
    public AsyncRelayCommand RefreshAccountsCommand { get; }
    public AsyncRelayCommand LoadSavedCommand { get; }
    public AsyncRelayCommand ImportCommand { get; }
    public AsyncRelayCommand SimulateCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public IReadOnlyList<CloudAccount> Accounts { get => _accounts; private set => SetProperty(ref _accounts, value); }
    public IReadOnlyList<WhatIfTarget> Targets { get => _targets; private set => SetProperty(ref _targets, value); }
    public CloudAccount? SelectedAccount
    {
        get => _selectedAccount;
        set { if (SetProperty(ref _selectedAccount, value)) InvalidateInput(); }
    }
    public WhatIfTarget? SelectedTarget
    {
        get => _selectedTarget;
        set { if (SetProperty(ref _selectedTarget, value)) ClearReport(); }
    }
    public string Action
    {
        get => _action;
        set { if (SetProperty(ref _action, value)) ClearReport(); }
    }
    public WhatIfReport? Report
    {
        get => _report;
        private set
        {
            if (!SetProperty(ref _report, value)) return;
            OnPropertyChanged(nameof(AccessChanges)); OnPropertyChanged(nameof(Summary));
        }
    }
    public IReadOnlyList<WhatIfAccessChange> AccessChanges => Report?.AccessChanges ?? Array.Empty<WhatIfAccessChange>();
    public string Summary => Report is null ? "No simulation result."
        : $"{Report.AccessChanges.Count} affected modeled pairs; {Report.ModeledLostPairs} modeled losses; {Report.PotentialLossPairs} uncertain pairs. Selected-action exposure reduction: {(Report.ModeledExposureReductionPercent.HasValue ? $"{Report.ModeledExposureReductionPercent}%" : "Unknown")}. Not a total risk score.";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public async Task RefreshAsync()
    {
        var version = Begin();
        try
        {
            EnsureAccess();
            var accounts = await _saved.GetAccountsAsync(_pending!.Token);
            if (version != _version) return;
            Accounts = accounts.Where(IsAccountAllowed).ToArray();
            Status = $"Found {Accounts.Count} permitted saved local account(s). No cloud endpoint contacted.";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or UnauthorizedAccessException or IOException or DbException)
        { if (version == _version) Status = $"Saved inventory read failed: {exception.Message}"; }
    }

    public async Task LoadSavedAsync()
    {
        if (SelectedAccount is null) { Status = "Select a saved account first."; return; }
        var account = SelectedAccount;
        InvalidateInput();
        var version = Begin();
        try
        {
            EnsureAccess();
            if (!IsAccountAllowed(account)) throw new UnauthorizedAccessException("This account is not bound to the current organization.");
            var token = _pending!.Token;
            var input = await Task.Run(() => _saved.LoadAsync(account.Id, token), token);
            if (version != _version) return;
            SetInput(input, (CloudProvider)account.CloudProvider, account.Id);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or UnauthorizedAccessException or IOException or DbException)
        { if (version == _version) Status = $"Saved scenario load failed: {exception.Message}"; }
    }

    public async Task ImportAsync()
    {
        InvalidateInput();
        var version = Begin();
        try
        {
            EnsureAccess();
            var path = _dialogs.ChooseAuthorizationBundlePath();
            if (path is null) return;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > WhatIfPermissionSimulator.MaximumJsonBytes) throw new ArgumentException("Offline bundle exceeds 8 MiB.");
            using var reader = new StreamReader(stream);
            await LoadJsonAsync(await reader.ReadToEndAsync(_pending!.Token), version, _pending.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or IOException)
        { if (version == _version) Status = $"Offline import failed: {exception.Message}"; }
    }

    public async Task LoadJsonAsync(string json)
    {
        InvalidateInput();
        var version = Begin();
        try { await LoadJsonAsync(json, version, _pending!.Token); }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        { if (version == _version) { InvalidateInput(); Status = $"Offline import failed: {exception.Message}"; } }
    }

    private async Task LoadJsonAsync(string json, int version, CancellationToken token)
    {
        EnsureAccess();
        var input = await Task.Run(() => WhatIfPermissionSimulator.ImportAsync(json, token), token);
        if (version != _version) return;
        var principal = input.Evaluation.Normalized.Principals.First();
        var accountId = principal.AccountId;
        var provider = (CloudProvider)principal.CloudProvider;
        EnsureAccountBinding(accountId, provider);
        SetInput(input, provider, accountId);
    }

    public async Task SimulateAsync()
    {
        if (_input is null || SelectedTarget is null) { Status = "Load evidence and select a role or policy."; return; }
        var input = _input;
        var change = new WhatIfChange(_provider, _accountId, SelectedTarget.Kind, SelectedTarget.Id, Action.Trim());
        var version = Begin();
        try
        {
            EnsureAccess();
            EnsureAccountBinding(change.AccountId, change.Provider);
            var token = _pending!.Token;
            var report = await Task.Run(() => _simulator.SimulateAsync(input, change, token), token);
            if (version != _version) return;
            EnsureAccess();
            EnsureAccountBinding(change.AccountId, change.Provider);
            Report = report;
            Status = report.Notice;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        { if (version == _version) Status = $"Simulation rejected: {exception.Message}"; }
    }

    public async Task ExportAsync()
    {
        if (Report is null) { Status = "Run a simulation before exporting."; return; }
        var report = Report;
        var version = _version;
        try
        {
            EnsureAccess();
            EnsureAccountBinding(report.Change.AccountId, report.Change.Provider);
            var path = _dialogs.ChooseWhatIfReportPath();
            if (path is null || version != _version) return;
            EnsureAccess();
            EnsureAccountBinding(report.Change.AccountId, report.Change.Provider);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions
            { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
            if (version == _version)
                Status = "Saved a non-executable simulation report. It is not an approved policy or provider verification.";
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        { Status = $"Simulation export failed: {exception.Message}"; }
    }

    private void SetInput(WhatIfInput input, CloudProvider provider, Guid accountId)
    {
        WhatIfPermissionSimulator.Validate(input, provider, accountId);
        _input = input; _provider = provider; _accountId = accountId;
        Targets = input.Evaluation.Normalized.Roles.Select(item => new WhatIfTarget(WhatIfTargetKind.Role, item.Id, item.Name))
            .Concat(input.Evaluation.Normalized.Policies.Select(item => new WhatIfTarget(WhatIfTargetKind.Policy, item.Id, item.Name))).ToArray();
        SelectedTarget = null;
        Status = $"{input.SourceLabel}: {input.Evaluation.Normalized.Principals.Count} identities and {input.Evaluation.Resources.Count} resources loaded. Original policy evidence is local only.";
    }
    private bool IsAccountAllowed(CloudAccount account)
        => _context.OrganizationId is not { } organization || _context.Identity is not { } identity
            || _organizations.GetTenantBindings(organization, identity).Any(item => item.CloudAccountId == account.Id
                && item.Provider == (CloudProvider)account.CloudProvider);
    private void EnsureAccountBinding(Guid accountId, CloudProvider provider)
    {
        if (_context.OrganizationId is { } organization && _context.Identity is { } identity
            && !_organizations.GetTenantBindings(organization, identity).Any(item => item.CloudAccountId == accountId && item.Provider == provider))
            throw new UnauthorizedAccessException("Account ID/provider must match a current organization binding.");
    }
    private void EnsureAccess()
    {
        if (_context.OrganizationId is not { } organization) return;
        try
        {
            if (_context.Identity is not { } identity
                || !_organizations.Authorize(organization, identity, OrganizationAction.ViewInventory).Allowed
                || !_organizations.Authorize(organization, identity, OrganizationAction.ViewFindings).Allowed)
                throw new UnauthorizedAccessException("Current organization inventory/finding permissions are required.");
        }
        catch (KeyNotFoundException exception)
        {
            throw new UnauthorizedAccessException("The selected organization is no longer available. Select a current organization.", exception);
        }
    }
    private int Begin() { _pending?.Cancel(); _pending?.Dispose(); _pending = new(); _version++; Report = null; return _version; }
    private void ClearReport() { _version++; _pending?.Cancel(); Report = null; }
    private void InvalidateInput() { ClearReport(); _input = null; Targets = Array.Empty<WhatIfTarget>(); _selectedTarget = null; OnPropertyChanged(nameof(SelectedTarget)); }
    private void Reset() { InvalidateInput(); Accounts = Array.Empty<CloudAccount>(); _selectedAccount = null; OnPropertyChanged(nameof(SelectedAccount)); Status = "Context changed; reload permitted offline evidence."; }
}
