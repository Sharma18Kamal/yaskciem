using CloudEntitle.Application.Interfaces;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.OrganizationManagement.Models;
using CloudEntitle.OrganizationManagement.Services;

namespace CloudEntitle.Desktop.ViewModels;

public sealed partial class OrganizationViewModel : WorkspaceViewModel
{
    private readonly OrganizationManagementService _organizationsService;
    private readonly IOrganizationSsoService _ssoService;
    private readonly ILiveCloudScanService _liveScan;
    private readonly AuthenticatedOrganizationContext _authenticatedContext;
    private readonly ICentralAuditSyncService? _centralAuditSyncService;
    private readonly ISharedOrganizationApi? _sharedOrganizationApi;
    private readonly ISharedScanScheduleApi? _sharedScanScheduleApi;
    private OrganizationWorkspaceDto? _sharedWorkspace;
    private IReadOnlyList<OrganizationProfile> _organizations = Array.Empty<OrganizationProfile>();
    private readonly ISharedScanSummaryPublisher? _sharedScanSummaryPublisher;
    private IReadOnlyList<SharedScanSummaryDto> _sharedScanSummaries = Array.Empty<SharedScanSummaryDto>();
    private IReadOnlyList<SharedCiemRecordDto> _sharedFindings = Array.Empty<SharedCiemRecordDto>();
    private IReadOnlyList<ScanScheduleDto> _scanSchedules = Array.Empty<ScanScheduleDto>();
    private IReadOnlyList<ScanJobDto> _scanJobs = Array.Empty<ScanJobDto>();
    private IReadOnlyList<OrganizationMember> _members = Array.Empty<OrganizationMember>();
    private IReadOnlyList<OrganizationRoleOption> _memberRoles = Enum.GetValues<OrganizationMemberRole>()
        .Select(role => new OrganizationRoleOption(role.ToString(), role, null)).ToArray();
    private IReadOnlyList<OrganizationCustomRole> _customRoles = Array.Empty<OrganizationCustomRole>();
    private IReadOnlyList<TenantBinding> _bindings = Array.Empty<TenantBinding>();
    private IReadOnlyList<OrganizationAuditEntry> _auditTrail = Array.Empty<OrganizationAuditEntry>();
    private IReadOnlyList<LiveScanAccountRecord> _availableAccounts = Array.Empty<LiveScanAccountRecord>();
    private IReadOnlyList<LiveScanAccountRecord> _consolidatedAccounts = Array.Empty<LiveScanAccountRecord>();
    private OrganizationProfile? _selectedOrganization;
    private OrganizationIdentity? _identity;
    private OrganizationMember? _selectedMember;
    private OrganizationRoleOption _newMemberRoleOption = new(OrganizationMemberRole.Viewer.ToString(), OrganizationMemberRole.Viewer, null);
    private OrganizationRoleOption _selectedMemberRoleOption = new(OrganizationMemberRole.Viewer.ToString(), OrganizationMemberRole.Viewer, null);
    private OrganizationCustomRole? _selectedCustomRole;
    private LiveScanAccountRecord? _selectedAccount;
    private TenantBinding? _selectedScanBinding;
    private ScanScheduleDto? _selectedScanSchedule;
    private ScanJobDto? _selectedScanJob;
    private int _scanIntervalMinutes = 60;
    private string _scanOperationsStatus = "Scheduled scans require shared organization mode.";
    private string _organizationName = string.Empty;
    private string _tenantKey = string.Empty;
    private string _organizationTenantId = string.Empty;
    private string _signInTenantId = string.Empty;
    private string _newMemberEmail = string.Empty;
    private string _customRoleName = string.Empty;
    private string _statusMessage = "Sign in with Microsoft Entra ID to manage organizations and view tenant data.";
    private string _centralAuditSyncStatus = "Central audit synchronization is not configured.";
    private string _sharedScanSummaryStatus = "Shared scan summaries are not configured.";
    private bool _customRoleViewInventory;
    private bool _customRoleRunScan;
    private bool _customRoleViewFindings;
    private bool _customRoleManageMembers;
    private bool _customRoleManageConnections;
    private bool _customRoleManagePolicies;
    private bool _customRoleManageRemediations;
    private bool _customRoleExportReports;
    private int _consolidatedIdentities;
    private int _consolidatedResources;
    private int _consolidatedFindings;
    private int _consolidatedCriticalFindings;
    private int _consolidatedEffectiveAccess;
    private int _unmatchedBoundAccounts;

    public OrganizationViewModel(
        IDemoDataService demoData,
        OrganizationManagementService organizationsService,
        IOrganizationSsoService ssoService,
        ILiveCloudScanService liveScan,
        AuthenticatedOrganizationContext authenticatedContext,
        ICentralAuditSyncService? centralAuditSyncService = null,
        ISharedOrganizationApi? sharedOrganizationApi = null,
        ISharedScanSummaryPublisher? sharedScanSummaryPublisher = null,
        ISharedScanScheduleApi? sharedScanScheduleApi = null,
        ISharedMonitoringApi? sharedMonitoringApi = null) : base(demoData)
    {
        _organizationsService = organizationsService;
        _ssoService = ssoService;
        _liveScan = liveScan;
        _authenticatedContext = authenticatedContext;
        _centralAuditSyncService = centralAuditSyncService;
        _sharedOrganizationApi = sharedOrganizationApi;
        _sharedScanScheduleApi = sharedScanScheduleApi;
        _sharedScanSummaryPublisher = sharedScanSummaryPublisher;
        _sharedMonitoringApi = sharedMonitoringApi;
        RefreshMonitoringCommand = new AsyncRelayCommand(RefreshMonitoringAsync);
        SaveNotificationRuleCommand = new AsyncRelayCommand(SaveNotificationRuleAsync);
        NewNotificationRuleCommand = new AsyncRelayCommand(() => { SelectedNotificationRule = null; ResetNotificationEditor(); return Task.CompletedTask; });
        LoadDeliveryAttemptsCommand = new AsyncRelayCommand(LoadDeliveryAttemptsAsync);
        CreateOrganizationCommand = new AsyncRelayCommand(CreateOrganizationAsync);
        SignInCommand = new AsyncRelayCommand(SignInAsync);
        SignOutCommand = new AsyncRelayCommand(SignOutAsync);
        SyncCentralAuditCommand = new AsyncRelayCommand(SyncCentralAuditAsync);
        PublishSharedScanSummaryCommand = new AsyncRelayCommand(PublishSharedScanSummaryAsync);
        AddMemberCommand = new AsyncRelayCommand(AddMemberAsync);
        UpdateMemberRoleCommand = new AsyncRelayCommand(UpdateMemberRoleAsync);
        ToggleMemberActiveCommand = new AsyncRelayCommand(ToggleMemberActiveAsync);
        NewCustomRoleCommand = new AsyncRelayCommand(NewCustomRoleAsync);
        SaveCustomRoleCommand = new AsyncRelayCommand(SaveCustomRoleAsync);
        DeleteCustomRoleCommand = new AsyncRelayCommand(DeleteCustomRoleAsync);
        BindAccountCommand = new AsyncRelayCommand(BindAccountAsync);
        CreateScanScheduleCommand = new AsyncRelayCommand(CreateScanScheduleAsync);
        RunScanNowCommand = new AsyncRelayCommand(RunScanNowAsync);
        ToggleScanScheduleCommand = new AsyncRelayCommand(ToggleScanScheduleAsync);
        CancelScanJobCommand = new AsyncRelayCommand(CancelScanJobAsync);
        RefreshScanOperationsCommand = new AsyncRelayCommand(RefreshScanOperationsAsync);
        _liveScan.ScanCompleted += async (_, _) =>
        {
            RefreshTenantView();
            if (_sharedScanSummaryPublisher is not null && SelectedOrganization is not null && IsAllowed(OrganizationAction.RunScan))
                await PublishSharedScanSummaryAsync();
        };
        _ = RefreshAsync();
    }

    public override string Title => "Organizations";
    public override string Subtitle => "Manage tenant membership and account bindings, then view consolidated results from the latest live scan.";
    public IReadOnlyList<OrganizationProfile> Organizations => _organizations;
    public IReadOnlyList<OrganizationMember> Members => _members;
    public IReadOnlyList<TenantBinding> Bindings => _bindings;
    public IReadOnlyList<OrganizationAuditEntry> AuditTrail => _auditTrail;
    public string CentralAuditSyncStatus { get => _centralAuditSyncStatus; private set => SetProperty(ref _centralAuditSyncStatus, value); }
    public string SharedScanSummaryStatus { get => _sharedScanSummaryStatus; private set => SetProperty(ref _sharedScanSummaryStatus, value); }
    public IReadOnlyList<SharedScanSummaryDto> SharedScanSummaries { get => _sharedScanSummaries; private set => SetProperty(ref _sharedScanSummaries, value); }
    public IReadOnlyList<SharedCiemRecordDto> SharedFindings { get => _sharedFindings; private set => SetProperty(ref _sharedFindings, value); }
    public IReadOnlyList<ScanScheduleDto> ScanSchedules { get => _scanSchedules; private set => SetProperty(ref _scanSchedules, value); }
    public IReadOnlyList<ScanJobDto> ScanJobs { get => _scanJobs; private set => SetProperty(ref _scanJobs, value); }
    public IReadOnlyList<LiveScanAccountRecord> AvailableAccounts => _availableAccounts;
    public IReadOnlyList<LiveScanAccountRecord> ConsolidatedAccounts => _consolidatedAccounts;
    public IReadOnlyList<OrganizationRoleOption> MemberRoles => _memberRoles;
    public IReadOnlyList<OrganizationCustomRole> CustomRoles => _customRoles;
    public OrganizationProfile? SelectedOrganization
    {
        get => _selectedOrganization;
        set
        {
            if (!SetProperty(ref _selectedOrganization, value)) return;
            if (value is not null)
            {
                OrganizationTenantId = value.EntraTenantId;
                SignInTenantId = value.EntraTenantId;
            }
            if (_identity is not null && !string.Equals(_identity.TenantId, value?.EntraTenantId, StringComparison.OrdinalIgnoreCase))
            {
                _identity = null;
                StatusMessage = "The selected organization uses a different Entra tenant. Sign in to continue.";
            }
            _authenticatedContext.Set(_identity, value?.Id);
            _sharedWorkspace = null;
            ResetMonitoring();
            SharedScanSummaries = Array.Empty<SharedScanSummaryDto>();
            SharedFindings = Array.Empty<SharedCiemRecordDto>();
            ScanSchedules = Array.Empty<ScanScheduleDto>();
            ScanJobs = Array.Empty<ScanJobDto>();
            SelectedScanSchedule = null;
            SelectedScanJob = null;
            if (_sharedOrganizationApi is not null && _identity is not null && value is not null)
                _ = RefreshSharedWorkspaceAsync();
            else
                RefreshTenantView();
            NotifyAuthorization();
        }
    }
    public OrganizationIdentity? Identity => _identity;
    public string SignedInAs => _identity?.Email ?? "Not signed in";
    public string CurrentRole => _sharedWorkspace?.CurrentMember.AssignedRoleName ?? CurrentOrganizationRole?.ToString() ?? "No organization access";
    public OrganizationMember? SelectedMember
    {
        get => _selectedMember;
        set
        {
            if (!SetProperty(ref _selectedMember, value)) return;
            SelectedMemberRoleOption = value is null
                ? MemberRoles.First(role => role.BuiltInRole == OrganizationMemberRole.Viewer)
                : MemberRoles.FirstOrDefault(role => value.CustomRoleId is not null && role.CustomRoleId == value.CustomRoleId)
                    ?? MemberRoles.FirstOrDefault(role => role.BuiltInRole == value.Role)
                    ?? MemberRoles.First(role => role.BuiltInRole == OrganizationMemberRole.Viewer);
            NotifyAuthorization();
        }
    }
    public OrganizationRoleOption NewMemberRoleOption { get => _newMemberRoleOption; set { if (SetProperty(ref _newMemberRoleOption, value)) NotifyAuthorization(); } }
    public OrganizationRoleOption SelectedMemberRoleOption { get => _selectedMemberRoleOption; set { if (SetProperty(ref _selectedMemberRoleOption, value)) NotifyAuthorization(); } }
    public OrganizationCustomRole? SelectedCustomRole
    {
        get => _selectedCustomRole;
        set
        {
            if (!SetProperty(ref _selectedCustomRole, value)) return;
            CustomRoleName = value?.Name ?? string.Empty;
            SetCustomRolePermissions(value?.Permissions ?? Array.Empty<OrganizationAction>());
            NotifyAuthorization();
        }
    }
    public string CustomRoleName { get => _customRoleName; set { if (SetProperty(ref _customRoleName, value)) NotifyAuthorization(); } }
    public bool CustomRoleViewInventory { get => _customRoleViewInventory; set => SetProperty(ref _customRoleViewInventory, value); }
    public bool CustomRoleRunScan { get => _customRoleRunScan; set => SetProperty(ref _customRoleRunScan, value); }
    public bool CustomRoleViewFindings { get => _customRoleViewFindings; set => SetProperty(ref _customRoleViewFindings, value); }
    public bool CustomRoleManageMembers { get => _customRoleManageMembers; set => SetProperty(ref _customRoleManageMembers, value); }
    public bool CustomRoleManageConnections { get => _customRoleManageConnections; set => SetProperty(ref _customRoleManageConnections, value); }
    public bool CustomRoleManagePolicies { get => _customRoleManagePolicies; set => SetProperty(ref _customRoleManagePolicies, value); }
    public bool CustomRoleManageRemediations { get => _customRoleManageRemediations; set => SetProperty(ref _customRoleManageRemediations, value); }
    public bool CustomRoleExportReports { get => _customRoleExportReports; set => SetProperty(ref _customRoleExportReports, value); }
    public LiveScanAccountRecord? SelectedAccount { get => _selectedAccount; set { if (SetProperty(ref _selectedAccount, value)) NotifyAuthorization(); } }
    public TenantBinding? SelectedScanBinding { get => _selectedScanBinding; set { if (SetProperty(ref _selectedScanBinding, value)) NotifyAuthorization(); } }
    public ScanScheduleDto? SelectedScanSchedule { get => _selectedScanSchedule; set { if (SetProperty(ref _selectedScanSchedule, value)) NotifyAuthorization(); } }
    public ScanJobDto? SelectedScanJob { get => _selectedScanJob; set { if (SetProperty(ref _selectedScanJob, value)) NotifyAuthorization(); } }
    public int ScanIntervalMinutes { get => _scanIntervalMinutes; set { if (SetProperty(ref _scanIntervalMinutes, value)) NotifyAuthorization(); } }
    public string ScanOperationsStatus { get => _scanOperationsStatus; private set => SetProperty(ref _scanOperationsStatus, value); }
    public string OrganizationName { get => _organizationName; set { if (SetProperty(ref _organizationName, value)) NotifyAuthorization(); } }
    public string TenantKey { get => _tenantKey; set { if (SetProperty(ref _tenantKey, value)) NotifyAuthorization(); } }
    public string OrganizationTenantId { get => _organizationTenantId; set { if (SetProperty(ref _organizationTenantId, value)) NotifyAuthorization(); } }
    public string SignInTenantId { get => _signInTenantId; set { if (SetProperty(ref _signInTenantId, value)) NotifyAuthorization(); } }
    public string NewMemberEmail { get => _newMemberEmail; set { if (SetProperty(ref _newMemberEmail, value)) NotifyAuthorization(); } }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public int BoundAccountCount => _bindings.Count;
    public int ConsolidatedAccountCount => _consolidatedAccounts.Count;
    public int ConsolidatedIdentities => _consolidatedIdentities;
    public int ConsolidatedResources => _consolidatedResources;
    public int ConsolidatedFindings => _consolidatedFindings;
    public int ConsolidatedCriticalFindings => _consolidatedCriticalFindings;
    public int ConsolidatedEffectiveAccess => _consolidatedEffectiveAccess;
    public int UnmatchedBoundAccounts => _unmatchedBoundAccounts;
    public string ConsolidatedCoverage => _liveScan.CurrentResult is null
        ? "No live scan is loaded. Run a read-only scan from Dashboard to populate the organization view."
        : $"Showing {_consolidatedAccounts.Count} bound account(s) present in the latest live scan; {_unmatchedBoundAccounts} bound account(s) have no latest-scan data. Synthetic Demo Mode records are excluded.";
    public bool CanCreateOrganization => _identity is not null
        && !string.IsNullOrWhiteSpace(OrganizationName)
        && !string.IsNullOrWhiteSpace(TenantKey)
        && Guid.TryParse(OrganizationTenantId, out var tenantId)
        && tenantId.ToString().Equals(_identity.TenantId, StringComparison.OrdinalIgnoreCase);
    public bool CanSignIn => Guid.TryParse(SignInTenantId, out _);
    public bool CanManageMembers => IsAllowed(OrganizationAction.ManageMembers);
    public bool CanManageConnections => IsAllowed(OrganizationAction.ManageConnections);
    public bool CanViewTenant => IsAllowed(OrganizationAction.ViewInventory);
    public bool CanPublishSharedScanSummary => _sharedScanSummaryPublisher is not null && IsAllowed(OrganizationAction.RunScan) && _liveScan.CurrentResult is not null;
    public bool CanAddMember => CanManageMembers && NewMemberEmail.Contains('@', StringComparison.Ordinal) && SelectedOrganization is not null;
    public bool CanChangeMemberRole => CanManageMembers && SelectedMember is not null;
    public bool CanToggleMember => CanManageMembers && SelectedMember is not null;
    public bool CanBindAccount => CanManageConnections && SelectedAccount is not null && SelectedOrganization is not null;
    public bool CanSaveCustomRole => CanManageMembers && !string.IsNullOrWhiteSpace(CustomRoleName);
    public bool CanDeleteCustomRole => CanManageMembers && SelectedCustomRole is not null;
    public bool CanCreateScanSchedule => _sharedScanScheduleApi is not null && IsAllowed(OrganizationAction.RunScan)
        && SelectedOrganization is not null && SelectedScanBinding is not null && ScanIntervalMinutes is >= 5 and <= 10080;
    public bool CanRunScheduledScan => _sharedScanScheduleApi is not null && IsAllowed(OrganizationAction.RunScan)
        && SelectedOrganization is not null && SelectedScanSchedule is not null;
    public bool CanToggleScanSchedule => CanRunScheduledScan;
    public bool CanCancelScanJob => _sharedScanScheduleApi is not null && IsAllowed(OrganizationAction.RunScan)
        && SelectedOrganization is not null && SelectedScanJob is { Status: "Queued" or "Running" };

    public AsyncRelayCommand CreateOrganizationCommand { get; }
    public AsyncRelayCommand SignInCommand { get; }
    public AsyncRelayCommand SignOutCommand { get; }
    public AsyncRelayCommand SyncCentralAuditCommand { get; }
    public AsyncRelayCommand PublishSharedScanSummaryCommand { get; }
    public AsyncRelayCommand AddMemberCommand { get; }
    public AsyncRelayCommand UpdateMemberRoleCommand { get; }
    public AsyncRelayCommand ToggleMemberActiveCommand { get; }
    public AsyncRelayCommand NewCustomRoleCommand { get; }
    public AsyncRelayCommand SaveCustomRoleCommand { get; }
    public AsyncRelayCommand DeleteCustomRoleCommand { get; }
    public AsyncRelayCommand BindAccountCommand { get; }
    public AsyncRelayCommand CreateScanScheduleCommand { get; }
    public AsyncRelayCommand RunScanNowCommand { get; }
    public AsyncRelayCommand ToggleScanScheduleCommand { get; }
    public AsyncRelayCommand CancelScanJobCommand { get; }
    public AsyncRelayCommand RefreshScanOperationsCommand { get; }

    public async Task RefreshAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            _organizations = _sharedOrganizationApi is not null
                ? _identity is not null ? await _sharedOrganizationApi.GetOrganizationsAsync() : Array.Empty<OrganizationProfile>()
                : _organizationsService.GetOrganizations();
            OnPropertyChanged(nameof(Organizations));
            if (SelectedOrganization is null || !_organizations.Any(item => item.Id == SelectedOrganization.Id))
                SelectedOrganization = _organizations.FirstOrDefault();
            else
                SelectedOrganization = _organizations.First(item => item.Id == SelectedOrganization.Id);
            if (_sharedOrganizationApi is not null && _identity is not null && SelectedOrganization is not null)
                await RefreshSharedWorkspaceAsync();
            RefreshTenantView();
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to load organizations: {exception.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task SignInAsync()
    {
        if (!CanSignIn) return;
        try
        {
            _identity = await _ssoService.SignInAsync(SignInTenantId);
            _authenticatedContext.Set(_identity, SelectedOrganization?.Id);
            var acceptedInvitation = _sharedOrganizationApi is null && SelectedOrganization is not null
                ? _organizationsService.AcceptInvitation(SelectedOrganization.Id, _identity)
                : null;
            StatusMessage = acceptedInvitation is null
                ? $"Signed in as {_identity.Email}. Organization membership is checked against the signed-in tenant and role."
                : "The Entra identity matched a pending organization invitation; membership is now linked to its object ID.";
            await RefreshAsync();
            RefreshTenantView();
            NotifyAuthorization();
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Entra sign-in failed: {exception.Message}";
        }
    }

    public async Task SyncCentralAuditAsync()
    {
        if (_centralAuditSyncService is null)
        {
            CentralAuditSyncStatus = "Set YASK_CIEM_API_BASE_URL to an HTTPS License Server URL to enable central audit sync.";
            return;
        }
        if (SelectedOrganization is null || _identity is null)
        {
            CentralAuditSyncStatus = "Select an organization and sign in before syncing audit events.";
            return;
        }

        try
        {
            var count = await _centralAuditSyncService.SyncOrganizationAsync(SelectedOrganization.Id);
            CentralAuditSyncStatus = $"Synced {count} organization audit events to the central tenant log. Repeated syncs are deduplicated.";
            ErrorMessage = null;
        }
        catch (Exception exception)
        {
            CentralAuditSyncStatus = $"Central audit sync failed: {exception.Message}";
        }
    }

    public async Task PublishSharedScanSummaryAsync()
    {
        if (_sharedScanSummaryPublisher is null)
        {
            SharedScanSummaryStatus = "Set the shared License Server URL to publish organization scan summaries.";
            return;
        }
        if (SelectedOrganization is null || _identity is null || !IsAllowed(OrganizationAction.RunScan))
        {
            SharedScanSummaryStatus = "Sign in with an organization role that can run scans before publishing summaries.";
            return;
        }
        try
        {
            var summary = await _sharedScanSummaryPublisher.PublishCurrentAsync(SelectedOrganization.Id);
            SharedScanSummaryStatus = summary is null
                ? "No scanned accounts are bound to this organization; nothing was published."
                : $"Shared scan data saved for {summary.Provider} at {summary.CompletedAtUtc.ToLocalTime():g}.";
            if (summary is not null && _sharedOrganizationApi is not null)
            {
                try
                {
                    SharedScanSummaries = await _sharedOrganizationApi.GetScanSummariesAsync(SelectedOrganization.Id, 20);
                    if (IsAllowed(OrganizationAction.ViewFindings))
                        SharedFindings = await _sharedOrganizationApi.GetFindingsAsync(SelectedOrganization.Id, summary.ScanId, 200);
                    await RefreshMonitoringAsync();
                }
                catch (Exception exception)
                {
                    SharedScanSummaryStatus += $" Refresh failed: {exception.Message}";
                }
            }
        }
        catch (Exception exception)
        {
            SharedScanSummaryStatus = $"Unable to publish shared scan summary: {exception.Message}";
        }
    }

    public Task SignOutAsync()
    {
        _identity = null;
        _sharedWorkspace = null;
        ResetMonitoring();
        SharedScanSummaries = Array.Empty<SharedScanSummaryDto>();
        SharedFindings = Array.Empty<SharedCiemRecordDto>();
        _authenticatedContext.Set(null, null);
        _members = Array.Empty<OrganizationMember>();
        _bindings = Array.Empty<TenantBinding>();
        _availableAccounts = Array.Empty<LiveScanAccountRecord>();
        _consolidatedAccounts = Array.Empty<LiveScanAccountRecord>();
        StatusMessage = "Signed out of organization SSO. Cloud CLI sign-ins are unchanged.";
        RefreshTenantView();
        NotifyAuthorization();
        return Task.CompletedTask;
    }

    public async Task CreateOrganizationAsync()
    {
        if (!CanCreateOrganization || _identity is null) return;
        try
        {
            var organization = _sharedOrganizationApi is not null
                ? await _sharedOrganizationApi.CreateOrganizationAsync(OrganizationName, TenantKey)
                : _organizationsService.CreateOrganization(OrganizationName, TenantKey, OrganizationTenantId, _identity);
            OrganizationName = string.Empty;
            TenantKey = string.Empty;
            StatusMessage = "Organization created and the signed-in identity was added as its first administrator.";
            await RefreshAsync();
            SelectedOrganization = _organizations.First(item => item.Id == organization.Id);
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to create organization: {exception.Message}";
        }
    }

    public async Task AddMemberAsync()
    {
        if (!CanAddMember || _identity is null || SelectedOrganization is null) return;
        try
        {
            if (_sharedOrganizationApi is not null)
                await _sharedOrganizationApi.InviteMemberAsync(SelectedOrganization.Id, NewMemberEmail, NewMemberRoleOption);
            else
                _organizationsService.AddMember(SelectedOrganization.Id, _identity, NewMemberEmail, NewMemberRoleOption);
            NewMemberEmail = string.Empty;
            StatusMessage = "Organization membership saved. The invited user must sign in through Entra before receiving access.";
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to add organization member: {exception.Message}";
        }
    }

    public async Task UpdateMemberRoleAsync()
    {
        if (!CanChangeMemberRole || _identity is null || SelectedOrganization is null || SelectedMember is null) return;
        try
        {
            if (_sharedOrganizationApi is not null)
                await _sharedOrganizationApi.UpdateMemberRoleAsync(SelectedOrganization.Id, SelectedMember.Id, SelectedMemberRoleOption);
            else
                _organizationsService.UpdateMemberRole(SelectedOrganization.Id, _identity, SelectedMember.Id, SelectedMemberRoleOption);
            StatusMessage = "Member role updated and saved.";
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to update member role: {exception.Message}";
        }
    }

    public async Task ToggleMemberActiveAsync()
    {
        if (!CanToggleMember || _identity is null || SelectedOrganization is null || SelectedMember is null) return;
        try
        {
            if (_sharedOrganizationApi is not null)
                await _sharedOrganizationApi.SetMemberActiveAsync(SelectedOrganization.Id, SelectedMember.Id, !SelectedMember.IsActive);
            else
                _organizationsService.SetMemberActive(SelectedOrganization.Id, _identity, SelectedMember.Id, !SelectedMember.IsActive);
            StatusMessage = SelectedMember.IsActive ? "Member deactivated." : "Member reactivated.";
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to update member status: {exception.Message}";
        }
    }

    public Task NewCustomRoleAsync()
    {
        SelectedCustomRole = null;
        CustomRoleName = string.Empty;
        SetCustomRolePermissions(Array.Empty<OrganizationAction>());
        return Task.CompletedTask;
    }

    public async Task SaveCustomRoleAsync()
    {
        if (!CanSaveCustomRole || _identity is null || SelectedOrganization is null) return;
        try
        {
            var isNewRole = SelectedCustomRole is null;
            var permissions = GetSelectedCustomRolePermissions();
            var savedRole = _sharedOrganizationApi is not null
                ? await _sharedOrganizationApi.SaveCustomRoleAsync(SelectedOrganization.Id, SelectedCustomRole?.Id, CustomRoleName, permissions)
                : isNewRole
                    ? _organizationsService.CreateCustomRole(SelectedOrganization.Id, _identity, CustomRoleName, permissions)
                    : _organizationsService.UpdateCustomRole(SelectedOrganization.Id, _identity, SelectedCustomRole!.Id, CustomRoleName, permissions);
            StatusMessage = isNewRole ? "Custom role created." : "Custom role updated.";
            RefreshTenantView();
            SelectedCustomRole = _customRoles.FirstOrDefault(role => role.Id == savedRole.Id);
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to save custom role: {exception.Message}";
        }
    }

    public async Task DeleteCustomRoleAsync()
    {
        if (!CanDeleteCustomRole || _identity is null || SelectedOrganization is null || SelectedCustomRole is null)
            return;
        try
        {
            if (_sharedOrganizationApi is not null)
                await _sharedOrganizationApi.DeleteCustomRoleAsync(SelectedOrganization.Id, SelectedCustomRole.Id);
            else
                _organizationsService.DeleteCustomRole(SelectedOrganization.Id, _identity, SelectedCustomRole.Id);
            StatusMessage = "Custom role deleted.";
            _ = NewCustomRoleAsync();
            RefreshTenantView();
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to delete custom role: {exception.Message}";
        }
    }

    public async Task BindAccountAsync()
    {
        if (!CanBindAccount || _identity is null || SelectedOrganization is null || SelectedAccount is null) return;
        if (SelectedAccount.CloudAccountId == Guid.Empty || !Enum.TryParse<CloudProvider>(SelectedAccount.Provider, true, out var provider))
        {
            ErrorMessage = "This roster entry does not have a scannable cloud-account ID and cannot be bound to consolidated data.";
            return;
        }
        try
        {
            if (_sharedOrganizationApi is not null)
                await _sharedOrganizationApi.BindAccountAsync(SelectedOrganization.Id, new BindOrganizationAccountRequest(provider, SelectedAccount.CloudAccountId, SelectedAccount.AccountId, SelectedAccount.AccountName));
            else
                _organizationsService.BindCloudAccount(SelectedOrganization.Id, _identity, provider, SelectedAccount.CloudAccountId, SelectedAccount.AccountId, SelectedAccount.AccountName);
            StatusMessage = "Cloud account bound to the organization. Consolidated counts use only matching live-scan account IDs.";
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to bind cloud account: {exception.Message}";
        }
    }

    public async Task CreateScanScheduleAsync()
    {
        if (!CanCreateScanSchedule || _sharedScanScheduleApi is null || SelectedOrganization is null || SelectedScanBinding is null)
            return;
        try
        {
            var schedule = await _sharedScanScheduleApi.CreateScanScheduleAsync(SelectedOrganization.Id,
                new CreateScanScheduleRequest(SelectedScanBinding.Provider, SelectedScanBinding.ExternalTenantId,
                    SelectedScanBinding.DisplayName, ScanIntervalMinutes));
            SelectedScanSchedule = schedule;
            ScanOperationsStatus = $"Schedule created. First full scan is due {schedule.NextRunAtUtc.ToLocalTime():g}.";
            await RefreshScanOperationsAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to create scheduled scan: {exception.Message}";
        }
    }

    public async Task RunScanNowAsync()
    {
        if (!CanRunScheduledScan || _sharedScanScheduleApi is null || SelectedOrganization is null || SelectedScanSchedule is null)
            return;
        try
        {
            SelectedScanJob = await _sharedScanScheduleApi.QueueScanNowAsync(SelectedOrganization.Id, SelectedScanSchedule.ScheduleId);
            ScanOperationsStatus = $"Scan job {SelectedScanJob.JobId:D} queued. Scans execute asynchronously on the server worker.";
            await RefreshScanOperationsAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to queue scan: {exception.Message}";
        }
    }

    public async Task ToggleScanScheduleAsync()
    {
        if (!CanToggleScanSchedule || _sharedScanScheduleApi is null || SelectedOrganization is null || SelectedScanSchedule is null)
            return;
        try
        {
            SelectedScanSchedule = await _sharedScanScheduleApi.UpdateScanScheduleAsync(SelectedOrganization.Id,
                SelectedScanSchedule.ScheduleId, new UpdateScanScheduleRequest(!SelectedScanSchedule.Enabled,
                    SelectedScanSchedule.IntervalMinutes, SelectedScanSchedule.MaxRetries));
            ScanOperationsStatus = SelectedScanSchedule.Enabled ? "Schedule enabled." : "Schedule paused.";
            await RefreshScanOperationsAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to update scheduled scan: {exception.Message}";
        }
    }

    public async Task CancelScanJobAsync()
    {
        if (!CanCancelScanJob || _sharedScanScheduleApi is null || SelectedOrganization is null || SelectedScanJob is null)
            return;
        try
        {
            await _sharedScanScheduleApi.CancelScanJobAsync(SelectedOrganization.Id, SelectedScanJob.JobId);
            ScanOperationsStatus = "Cancellation requested. A running provider command will stop at its next cancellation point.";
            await RefreshScanOperationsAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to cancel scan job: {exception.Message}";
        }
    }

    public async Task RefreshScanOperationsAsync()
    {
        if (_sharedScanScheduleApi is null || _sharedWorkspace is null || SelectedOrganization is null
            || !_sharedWorkspace.AllowedActions.Contains(OrganizationAction.RunScan))
        {
            ScanSchedules = Array.Empty<ScanScheduleDto>();
            ScanJobs = Array.Empty<ScanJobDto>();
            if (_sharedOrganizationApi is null)
                ScanOperationsStatus = "Scheduled scans require shared organization mode.";
            return;
        }
        try
        {
            ScanSchedules = await _sharedScanScheduleApi.GetScanSchedulesAsync(SelectedOrganization.Id);
            ScanJobs = await _sharedScanScheduleApi.GetScanJobsAsync(SelectedOrganization.Id, 50);
            if (SelectedScanSchedule is not null)
                SelectedScanSchedule = ScanSchedules.FirstOrDefault(item => item.ScheduleId == SelectedScanSchedule.ScheduleId);
            if (SelectedScanJob is not null)
                SelectedScanJob = ScanJobs.FirstOrDefault(item => item.JobId == SelectedScanJob.JobId);
            ScanOperationsStatus = $"Loaded {ScanSchedules.Count} account schedule(s) and {ScanJobs.Count} recent scan job(s).";
            NotifyAuthorization();
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to refresh scheduled scans: {exception.Message}";
        }
    }

    private OrganizationMemberRole? CurrentOrganizationRole =>
        _identity is null || SelectedOrganization is null || _sharedOrganizationApi is not null ? null : _organizationsService.GetCurrentRole(SelectedOrganization.Id, _identity);

    private bool IsAllowed(OrganizationAction action) =>
        _identity is not null && SelectedOrganization is not null && (_sharedWorkspace is not null
            ? _sharedWorkspace.AllowedActions.Contains(action)
            : _organizationsService.Authorize(SelectedOrganization.Id, _identity, action).Allowed);

    private void RefreshTenantView()
    {
        if (_sharedOrganizationApi is not null)
        {
            _customRoles = CanManageMembers ? _sharedWorkspace?.CustomRoles ?? Array.Empty<OrganizationCustomRole>() : Array.Empty<OrganizationCustomRole>();
            var builtInRoles = Enum.GetValues<OrganizationMemberRole>().Select(role => new OrganizationRoleOption(role.ToString(), role, null));
            var customRoleOptions = _customRoles.Select(role => new OrganizationRoleOption(role.Name, null, role.Id));
            _memberRoles = builtInRoles.Concat(customRoleOptions).ToArray();
            _members = CanManageMembers ? _sharedWorkspace?.Members ?? Array.Empty<OrganizationMember>() : Array.Empty<OrganizationMember>();
            _bindings = CanViewTenant ? _sharedWorkspace?.Bindings ?? Array.Empty<TenantBinding>() : Array.Empty<TenantBinding>();
            _auditTrail = CanManageMembers ? _sharedWorkspace?.AuditTrail ?? Array.Empty<OrganizationAuditEntry>() : Array.Empty<OrganizationAuditEntry>();
        }
        else
        {
            try
            {
                _memberRoles = CanManageMembers && _identity is not null && SelectedOrganization is not null
                    ? _organizationsService.GetRoleOptions(SelectedOrganization.Id, _identity)
                    : Enum.GetValues<OrganizationMemberRole>().Select(role => new OrganizationRoleOption(role.ToString(), role, null)).ToArray();
                _customRoles = CanManageMembers && _identity is not null && SelectedOrganization is not null
                    ? _organizationsService.GetCustomRoles(SelectedOrganization.Id, _identity)
                    : Array.Empty<OrganizationCustomRole>();
                _members = CanManageMembers && _identity is not null && SelectedOrganization is not null
                    ? _organizationsService.GetMembers(SelectedOrganization.Id, _identity)
                    : Array.Empty<OrganizationMember>();
                _bindings = CanViewTenant && _identity is not null && SelectedOrganization is not null
                    ? _organizationsService.GetTenantBindings(SelectedOrganization.Id, _identity)
                    : Array.Empty<TenantBinding>();
                _auditTrail = CanManageMembers && _identity is not null && SelectedOrganization is not null
                    ? _organizationsService.GetAuditTrail(SelectedOrganization.Id, _identity)
                    : Array.Empty<OrganizationAuditEntry>();
            }
            catch (UnauthorizedAccessException)
            {
                _members = Array.Empty<OrganizationMember>();
                _bindings = Array.Empty<TenantBinding>();
                _auditTrail = Array.Empty<OrganizationAuditEntry>();
            }
        }
        if (_selectedScanBinding is null || !_bindings.Any(binding => binding.Id == _selectedScanBinding.Id))
            SelectedScanBinding = _bindings.FirstOrDefault();

        var result = _liveScan.CurrentResult;
        var allAccounts = result?.AccountRecords ?? Array.Empty<LiveScanAccountRecord>();
        _consolidatedAccounts = (result?.AccountRecords ?? Array.Empty<LiveScanAccountRecord>())
            .Where(account => account.ScanAccess.StartsWith("Scanned", StringComparison.OrdinalIgnoreCase) && IsBoundAccount(account))
            .GroupBy(account => (account.Provider, account.AccountId), StringTupleComparer.Instance).Select(group => group.First()).ToArray();
        var currentAccountIds = _consolidatedAccounts.Select(account => account.CloudAccountId).Where(id => id != Guid.Empty).ToHashSet();
        var externalAccountIds = _consolidatedAccounts.Select(account => account.AccountId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _availableAccounts = CanManageConnections && _identity is not null && SelectedOrganization is not null
            ? allAccounts.Where(account => account.CloudAccountId != Guid.Empty
                    && Enum.TryParse<CloudProvider>(account.Provider, true, out var provider)
                    && (_sharedOrganizationApi is not null
                        ? !_bindings.Any(binding => binding.Provider == provider && binding.ExternalTenantId.Equals(account.AccountId, StringComparison.OrdinalIgnoreCase))
                        : _organizationsService.IsAccountAvailable(SelectedOrganization.Id, _identity, provider, account.AccountId)))
                .GroupBy(account => (account.Provider, account.AccountId), StringTupleComparer.Instance).Select(group => group.First()).ToArray()
            : Array.Empty<LiveScanAccountRecord>();
        _consolidatedIdentities = CanViewTenant ? (result?.IdentityRecords ?? Array.Empty<CloudPrincipal>()).Count(item => currentAccountIds.Contains(item.AccountId)) : 0;
        _consolidatedResources = CanViewTenant ? (result?.ResourceRecords ?? Array.Empty<CloudResource>()).Count(item => currentAccountIds.Contains(item.AccountId)) : 0;
        var findings = CanViewTenant ? (result?.Findings ?? Array.Empty<RiskFinding>()).Where(item => currentAccountIds.Contains(item.CloudAccountId)).ToArray() : Array.Empty<RiskFinding>();
        _consolidatedFindings = findings.Length;
        _consolidatedCriticalFindings = findings.Count(item => item.Severity == RiskSeverity.Critical);
        _consolidatedEffectiveAccess = CanViewTenant ? (result?.EffectiveAccessRecords ?? Array.Empty<LiveEffectiveAccessRecord>()).Count(item => item.AccountId is not null && externalAccountIds.Contains(item.AccountId)) : 0;
        _unmatchedBoundAccounts = Math.Max(0, _bindings.Count - _consolidatedAccounts.Count);
        foreach (var property in new[] { nameof(Members), nameof(Bindings), nameof(AuditTrail), nameof(AvailableAccounts), nameof(ConsolidatedAccounts), nameof(BoundAccountCount), nameof(ConsolidatedAccountCount), nameof(ConsolidatedIdentities), nameof(ConsolidatedResources), nameof(ConsolidatedFindings), nameof(ConsolidatedCriticalFindings), nameof(ConsolidatedEffectiveAccess), nameof(UnmatchedBoundAccounts), nameof(ConsolidatedCoverage), nameof(SharedScanSummaries), nameof(SharedFindings), nameof(SharedScanSummaryStatus), nameof(ScanSchedules), nameof(ScanJobs), nameof(ScanOperationsStatus) })
            OnPropertyChanged(property);
        OnPropertyChanged(nameof(MemberRoles));
        OnPropertyChanged(nameof(CustomRoles));
        NotifyAuthorization();
    }

    private async Task RefreshSharedWorkspaceAsync()
    {
        if (_sharedOrganizationApi is null || _identity is null || SelectedOrganization is null)
        {
            ResetMonitoring();
            _sharedWorkspace = null;
            SharedScanSummaries = Array.Empty<SharedScanSummaryDto>();
            SharedFindings = Array.Empty<SharedCiemRecordDto>();
            ScanSchedules = Array.Empty<ScanScheduleDto>();
            ScanJobs = Array.Empty<ScanJobDto>();
            ScanOperationsStatus = "Scheduled scans require shared organization mode.";
            RefreshTenantView();
            return;
        }
        try
        {
            _sharedWorkspace = await _sharedOrganizationApi.GetWorkspaceAsync(SelectedOrganization.Id);
            SharedScanSummaries = _sharedWorkspace.AllowedActions.Contains(OrganizationAction.ViewInventory)
                ? await _sharedOrganizationApi.GetScanSummariesAsync(SelectedOrganization.Id, 20)
                : Array.Empty<SharedScanSummaryDto>();
            SharedFindings = _sharedWorkspace.AllowedActions.Contains(OrganizationAction.ViewFindings)
                ? await _sharedOrganizationApi.GetFindingsAsync(SelectedOrganization.Id, SharedScanSummaries.FirstOrDefault()?.ScanId, 200)
                : Array.Empty<SharedCiemRecordDto>();
            if (_sharedScanScheduleApi is not null && _sharedWorkspace.AllowedActions.Contains(OrganizationAction.RunScan))
            {
                ScanSchedules = await _sharedScanScheduleApi.GetScanSchedulesAsync(SelectedOrganization.Id);
                ScanJobs = await _sharedScanScheduleApi.GetScanJobsAsync(SelectedOrganization.Id, 50);
                ScanOperationsStatus = $"Loaded {ScanSchedules.Count} account schedule(s) and {ScanJobs.Count} recent scan job(s).";
            }
            else
            {
                ScanSchedules = Array.Empty<ScanScheduleDto>();
                ScanJobs = Array.Empty<ScanJobDto>();
                ScanOperationsStatus = "Your organization role does not allow scheduled scans.";
            }
            SharedScanSummaryStatus = SharedScanSummaries.Count == 0
                ? "No shared scan summaries are available for this organization."
                : $"Loaded {SharedScanSummaries.Count} shared scan summaries and {SharedFindings.Count} findings.";
            await RefreshMonitoringAsync();
            RefreshTenantView();
            NotifyAuthorization();
        }
        catch (Exception exception)
        {
            ResetMonitoring();
            _sharedWorkspace = null;
            SharedScanSummaries = Array.Empty<SharedScanSummaryDto>();
            SharedFindings = Array.Empty<SharedCiemRecordDto>();
            ScanSchedules = Array.Empty<ScanScheduleDto>();
            ScanJobs = Array.Empty<ScanJobDto>();
            ScanOperationsStatus = "Unable to load shared scan operations.";
            RefreshTenantView();
            ErrorMessage = $"Unable to load shared organization workspace: {exception.Message}";
        }
    }

    private IReadOnlyList<OrganizationAction> GetSelectedCustomRolePermissions()
    {
        var permissions = new List<OrganizationAction>();
        if (CustomRoleViewInventory) permissions.Add(OrganizationAction.ViewInventory);
        if (CustomRoleRunScan) permissions.Add(OrganizationAction.RunScan);
        if (CustomRoleViewFindings) permissions.Add(OrganizationAction.ViewFindings);
        if (CustomRoleManageMembers) permissions.Add(OrganizationAction.ManageMembers);
        if (CustomRoleManageConnections) permissions.Add(OrganizationAction.ManageConnections);
        if (CustomRoleManagePolicies) permissions.Add(OrganizationAction.ManagePolicies);
        if (CustomRoleManageRemediations) permissions.Add(OrganizationAction.ManageRemediations);
        if (CustomRoleExportReports) permissions.Add(OrganizationAction.ExportReports);
        return permissions;
    }

    private void SetCustomRolePermissions(IEnumerable<OrganizationAction> permissions)
    {
        var selected = permissions.ToHashSet();
        CustomRoleViewInventory = selected.Contains(OrganizationAction.ViewInventory);
        CustomRoleRunScan = selected.Contains(OrganizationAction.RunScan);
        CustomRoleViewFindings = selected.Contains(OrganizationAction.ViewFindings);
        CustomRoleManageMembers = selected.Contains(OrganizationAction.ManageMembers);
        CustomRoleManageConnections = selected.Contains(OrganizationAction.ManageConnections);
        CustomRoleManagePolicies = selected.Contains(OrganizationAction.ManagePolicies);
        CustomRoleManageRemediations = selected.Contains(OrganizationAction.ManageRemediations);
        CustomRoleExportReports = selected.Contains(OrganizationAction.ExportReports);
    }

    private bool IsBoundAccount(LiveScanAccountRecord account)
        => _bindings.Any(binding => IsBindingMatch(binding, account));

    private static bool IsBindingMatch(TenantBinding binding, LiveScanAccountRecord account)
        => string.Equals(binding.Provider.ToString(), account.Provider, StringComparison.OrdinalIgnoreCase)
            && string.Equals(binding.ExternalTenantId, account.AccountId, StringComparison.OrdinalIgnoreCase);

    private sealed class StringTupleComparer : IEqualityComparer<(string Provider, string AccountId)>
    {
        public static StringTupleComparer Instance { get; } = new();
        public bool Equals((string Provider, string AccountId) left, (string Provider, string AccountId) right)
            => string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.AccountId, right.AccountId, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Provider, string AccountId) value)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Provider), StringComparer.OrdinalIgnoreCase.GetHashCode(value.AccountId));
    }

    private void NotifyAuthorization()
    {
        OnPropertyChanged(nameof(CanReadMonitoring));
        OnPropertyChanged(nameof(CanManageNotificationRules));
        foreach (var property in new[] { nameof(SignedInAs), nameof(CurrentRole), nameof(CanCreateOrganization), nameof(CanSignIn), nameof(CanManageMembers), nameof(CanManageConnections), nameof(CanViewTenant), nameof(CanPublishSharedScanSummary), nameof(CanAddMember), nameof(CanChangeMemberRole), nameof(CanToggleMember), nameof(CanBindAccount), nameof(CanSaveCustomRole), nameof(CanDeleteCustomRole), nameof(CanCreateScanSchedule), nameof(CanRunScheduledScan), nameof(CanToggleScanSchedule), nameof(CanCancelScanJob), nameof(Identity) })
            OnPropertyChanged(property);
    }
}
