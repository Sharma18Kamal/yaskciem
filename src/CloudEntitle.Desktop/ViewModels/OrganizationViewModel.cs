using CloudEntitle.Application.Interfaces;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.OrganizationManagement.Models;
using CloudEntitle.OrganizationManagement.Services;

namespace CloudEntitle.Desktop.ViewModels;

public sealed class OrganizationViewModel : WorkspaceViewModel
{
    private readonly OrganizationManagementService _organizationsService;
    private readonly IOrganizationSsoService _ssoService;
    private readonly ILiveCloudScanService _liveScan;
    private IReadOnlyList<OrganizationProfile> _organizations = Array.Empty<OrganizationProfile>();
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
    private string _organizationName = string.Empty;
    private string _tenantKey = string.Empty;
    private string _organizationTenantId = string.Empty;
    private string _signInTenantId = string.Empty;
    private string _newMemberEmail = string.Empty;
    private string _customRoleName = string.Empty;
    private string _statusMessage = "Sign in with Microsoft Entra ID to manage organizations and view tenant data.";
    private bool _customRoleViewInventory;
    private bool _customRoleRunScan;
    private bool _customRoleViewFindings;
    private bool _customRoleManageMembers;
    private bool _customRoleManageConnections;
    private bool _customRoleManagePolicies;
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
        ILiveCloudScanService liveScan) : base(demoData)
    {
        _organizationsService = organizationsService;
        _ssoService = ssoService;
        _liveScan = liveScan;
        CreateOrganizationCommand = new AsyncRelayCommand(CreateOrganizationAsync);
        SignInCommand = new AsyncRelayCommand(SignInAsync);
        SignOutCommand = new AsyncRelayCommand(SignOutAsync);
        AddMemberCommand = new AsyncRelayCommand(AddMemberAsync);
        UpdateMemberRoleCommand = new AsyncRelayCommand(UpdateMemberRoleAsync);
        ToggleMemberActiveCommand = new AsyncRelayCommand(ToggleMemberActiveAsync);
        NewCustomRoleCommand = new AsyncRelayCommand(NewCustomRoleAsync);
        SaveCustomRoleCommand = new AsyncRelayCommand(SaveCustomRoleAsync);
        DeleteCustomRoleCommand = new AsyncRelayCommand(DeleteCustomRoleAsync);
        BindAccountCommand = new AsyncRelayCommand(BindAccountAsync);
        _liveScan.ScanCompleted += (_, _) => RefreshTenantView();
        _ = RefreshAsync();
    }

    public override string Title => "Organizations";
    public override string Subtitle => "Manage tenant membership and account bindings, then view consolidated results from the latest live scan.";
    public IReadOnlyList<OrganizationProfile> Organizations => _organizations;
    public IReadOnlyList<OrganizationMember> Members => _members;
    public IReadOnlyList<TenantBinding> Bindings => _bindings;
    public IReadOnlyList<OrganizationAuditEntry> AuditTrail => _auditTrail;
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
            RefreshTenantView();
            NotifyAuthorization();
        }
    }
    public OrganizationIdentity? Identity => _identity;
    public string SignedInAs => _identity?.Email ?? "Not signed in";
    public string CurrentRole => CurrentOrganizationRole?.ToString() ?? "No organization access";
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
    public bool CustomRoleExportReports { get => _customRoleExportReports; set => SetProperty(ref _customRoleExportReports, value); }
    public LiveScanAccountRecord? SelectedAccount { get => _selectedAccount; set { if (SetProperty(ref _selectedAccount, value)) NotifyAuthorization(); } }
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
    public bool CanAddMember => CanManageMembers && NewMemberEmail.Contains('@', StringComparison.Ordinal) && SelectedOrganization is not null;
    public bool CanChangeMemberRole => CanManageMembers && SelectedMember is not null;
    public bool CanToggleMember => CanManageMembers && SelectedMember is not null;
    public bool CanBindAccount => CanManageConnections && SelectedAccount is not null && SelectedOrganization is not null;
    public bool CanSaveCustomRole => CanManageMembers && !string.IsNullOrWhiteSpace(CustomRoleName);
    public bool CanDeleteCustomRole => CanManageMembers && SelectedCustomRole is not null;

    public AsyncRelayCommand CreateOrganizationCommand { get; }
    public AsyncRelayCommand SignInCommand { get; }
    public AsyncRelayCommand SignOutCommand { get; }
    public AsyncRelayCommand AddMemberCommand { get; }
    public AsyncRelayCommand UpdateMemberRoleCommand { get; }
    public AsyncRelayCommand ToggleMemberActiveCommand { get; }
    public AsyncRelayCommand NewCustomRoleCommand { get; }
    public AsyncRelayCommand SaveCustomRoleCommand { get; }
    public AsyncRelayCommand DeleteCustomRoleCommand { get; }
    public AsyncRelayCommand BindAccountCommand { get; }

    public Task RefreshAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            _organizations = _organizationsService.GetOrganizations();
            OnPropertyChanged(nameof(Organizations));
            if (SelectedOrganization is null || !_organizations.Any(item => item.Id == SelectedOrganization.Id))
                SelectedOrganization = _organizations.FirstOrDefault();
            else
                SelectedOrganization = _organizations.First(item => item.Id == SelectedOrganization.Id);
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
        return Task.CompletedTask;
    }

    public async Task SignInAsync()
    {
        if (!CanSignIn) return;
        try
        {
            _identity = await _ssoService.SignInAsync(SignInTenantId);
            var acceptedInvitation = SelectedOrganization is null ? null : _organizationsService.AcceptInvitation(SelectedOrganization.Id, _identity);
            StatusMessage = acceptedInvitation is null
                ? $"Signed in as {_identity.Email}. Organization membership is checked against the signed-in tenant and role."
                : "The Entra identity matched a pending organization invitation; membership is now linked to its object ID.";
            RefreshTenantView();
            NotifyAuthorization();
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Entra sign-in failed: {exception.Message}";
        }
    }

    public Task SignOutAsync()
    {
        _identity = null;
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
            var organization = _organizationsService.CreateOrganization(OrganizationName, TenantKey, OrganizationTenantId, _identity);
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

    public Task SaveCustomRoleAsync()
    {
        if (!CanSaveCustomRole || _identity is null || SelectedOrganization is null) return Task.CompletedTask;
        try
        {
            var isNewRole = SelectedCustomRole is null;
            var permissions = GetSelectedCustomRolePermissions();
            var savedRole = isNewRole
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
        return Task.CompletedTask;
    }

    public Task DeleteCustomRoleAsync()
    {
        if (!CanDeleteCustomRole || _identity is null || SelectedOrganization is null || SelectedCustomRole is null)
            return Task.CompletedTask;
        try
        {
            _organizationsService.DeleteCustomRole(SelectedOrganization.Id, _identity, SelectedCustomRole.Id);
            StatusMessage = "Custom role deleted.";
            _ = NewCustomRoleAsync();
            RefreshTenantView();
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to delete custom role: {exception.Message}";
        }
        return Task.CompletedTask;
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
            _organizationsService.BindCloudAccount(SelectedOrganization.Id, _identity, provider, SelectedAccount.CloudAccountId, SelectedAccount.AccountId, SelectedAccount.AccountName);
            StatusMessage = "Cloud account bound to the organization. Consolidated counts use only matching live-scan account IDs.";
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to bind cloud account: {exception.Message}";
        }
    }

    private OrganizationMemberRole? CurrentOrganizationRole =>
        _identity is null || SelectedOrganization is null ? null : _organizationsService.GetCurrentRole(SelectedOrganization.Id, _identity);

    private bool IsAllowed(OrganizationAction action) =>
        _identity is not null && SelectedOrganization is not null && _organizationsService.Authorize(SelectedOrganization.Id, _identity, action).Allowed;

    private void RefreshTenantView()
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
                    && _organizationsService.IsAccountAvailable(SelectedOrganization.Id, _identity, provider, account.AccountId))
                .GroupBy(account => (account.Provider, account.AccountId), StringTupleComparer.Instance).Select(group => group.First()).ToArray()
            : Array.Empty<LiveScanAccountRecord>();
        _consolidatedIdentities = CanViewTenant ? (result?.IdentityRecords ?? Array.Empty<CloudPrincipal>()).Count(item => currentAccountIds.Contains(item.AccountId)) : 0;
        _consolidatedResources = CanViewTenant ? (result?.ResourceRecords ?? Array.Empty<CloudResource>()).Count(item => currentAccountIds.Contains(item.AccountId)) : 0;
        var findings = CanViewTenant ? (result?.Findings ?? Array.Empty<RiskFinding>()).Where(item => currentAccountIds.Contains(item.CloudAccountId)).ToArray() : Array.Empty<RiskFinding>();
        _consolidatedFindings = findings.Length;
        _consolidatedCriticalFindings = findings.Count(item => item.Severity == RiskSeverity.Critical);
        _consolidatedEffectiveAccess = CanViewTenant ? (result?.EffectiveAccessRecords ?? Array.Empty<LiveEffectiveAccessRecord>()).Count(item => item.AccountId is not null && externalAccountIds.Contains(item.AccountId)) : 0;
        _unmatchedBoundAccounts = Math.Max(0, _bindings.Count - _consolidatedAccounts.Count);
        foreach (var property in new[] { nameof(Members), nameof(Bindings), nameof(AuditTrail), nameof(AvailableAccounts), nameof(ConsolidatedAccounts), nameof(BoundAccountCount), nameof(ConsolidatedAccountCount), nameof(ConsolidatedIdentities), nameof(ConsolidatedResources), nameof(ConsolidatedFindings), nameof(ConsolidatedCriticalFindings), nameof(ConsolidatedEffectiveAccess), nameof(UnmatchedBoundAccounts), nameof(ConsolidatedCoverage) })
            OnPropertyChanged(property);
        OnPropertyChanged(nameof(MemberRoles));
        OnPropertyChanged(nameof(CustomRoles));
        NotifyAuthorization();
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
        foreach (var property in new[] { nameof(SignedInAs), nameof(CurrentRole), nameof(CanCreateOrganization), nameof(CanSignIn), nameof(CanManageMembers), nameof(CanManageConnections), nameof(CanViewTenant), nameof(CanAddMember), nameof(CanChangeMemberRole), nameof(CanToggleMember), nameof(CanBindAccount), nameof(CanSaveCustomRole), nameof(CanDeleteCustomRole), nameof(Identity) })
            OnPropertyChanged(property);
    }
}
