using System.Security.Cryptography;
using System.Text;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.IdentityGovernance.Models;
using CloudEntitle.IdentityGovernance.Services;
using CloudEntitle.Integration.Contracts;

namespace CloudEntitle.Desktop.ViewModels;

public sealed record ReviewerOption(string Value, string Display);

public sealed class AccessReviewViewModel : WorkspaceViewModel
{
    private static readonly string[] QualifyingRoles =
    {
        "Global Administrator", "Security Administrator", "System Analyst", "Security Analyst",
        "AdministratorAccess", "SecurityAudit",
        "roles/owner", "roles/iam.securityAdmin", "roles/iam.securityReviewer"
    };

    private static readonly IReadOnlyList<AccessReviewTemplate> BuiltInTemplates = new[]
    {
        new AccessReviewTemplate("all-access", "All effective access", "Review every currently granted or conditional entitlement.", 30),
        new AccessReviewTemplate("privileged-workloads", "Privileged workload identities", "Review privileged service principals, managed identities, and service accounts.", 7, PrivilegedOnly: true, WorkloadIdentitiesOnly: true),
        new AccessReviewTemplate("external-identities", "External identities", "Review effective access held by external identities.", 14, ExternalOnly: true)
    };

    private static readonly IReadOnlyList<ReviewDecision> AvailableDecisions = new[]
    {
        ReviewDecision.Certified,
        ReviewDecision.NeedsInvestigation,
        ReviewDecision.Rejected,
        ReviewDecision.ExceptionGranted
    };

    private readonly IDemoDataService _demoData;
    private readonly IAccessReviewStore _store;
    private readonly bool _isSharedStore;
    private readonly ILiveCloudScanService? _liveScan;
    private readonly IdentityGovernanceService _governance;
    private IReadOnlyList<AccessReviewCampaign> _campaigns = Array.Empty<AccessReviewCampaign>();
    private AccessReviewCampaign? _selectedCampaign;
    private AccessReviewItem? _selectedItem;
    private string _searchText = string.Empty;
    private string _campaignName = "Quarterly cloud access review";
    private string _campaignDurationDays = BuiltInTemplates[0].DurationDays.ToString();
    private AccessReviewTemplate _selectedTemplate = BuiltInTemplates[0];
    private string _assignedReviewerName = string.Empty;
    private string _assignedOwner = string.Empty;
    private string _assignmentTeam = string.Empty;
    private string _assignmentAccount = string.Empty;
    private string _assignmentReason = string.Empty;
    private GovernanceReportDto? _governanceReport;
    private IReadOnlyList<string> _eligibleOwners = Array.Empty<string>();
    private IReadOnlyList<ReviewerOption> _eligibleReviewers = Array.Empty<ReviewerOption>();
    private string _reviewerName = string.Empty;
    private string _actionActor = Environment.UserName;
    private string _reviewComment = string.Empty;
    private DateTime? _itemDueAt;
    private DateTime? _exceptionExpiresAt;
    private ReviewDecision _selectedDecision = ReviewDecision.Certified;
    private string _reminderMessage = "Reviewer reminders are surfaced when this workspace is refreshed.";

    public AccessReviewViewModel(IDemoDataService demoData, IAccessReviewStore store, ILiveCloudScanService? liveScan = null)
    {
        _demoData = demoData;
        _store = store;
        _isSharedStore = store is SharedAccessReviewStore;
        if (store is SharedAccessReviewStore sharedStore)
        {
            var authenticatedActor = sharedStore.AuthenticatedActorEmail;
            if (!string.IsNullOrWhiteSpace(authenticatedActor)) _actionActor = authenticatedActor;
        }
        _liveScan = liveScan;
        _governance = new IdentityGovernanceService();
        CreateCampaignCommand = new AsyncRelayCommand(CreateCampaignAsync);
        AssignReviewerCommand = new AsyncRelayCommand(AssignReviewerAsync);
        RecordDecisionCommand = new AsyncRelayCommand(RecordDecisionAsync);
        RefreshCampaignsCommand = new AsyncRelayCommand(RefreshAsync);
        if (_liveScan is not null) _liveScan.ScanCompleted += (_, _) => _ = RefreshAsync();
        _ = RefreshAsync();
    }

    public override string Title => "Access Reviews";
    public override string Subtitle => "Assign, decide, escalate, and expire access reviews without changing cloud access.";
    public IReadOnlyList<AccessReviewCampaign> Campaigns => _campaigns;
    public IReadOnlyList<AccessReviewCampaign> FilteredCampaigns => _campaigns
        .Where(campaign => WorkspaceDataFormatting.Matches(SearchText, campaign.Name, campaign.Status.ToString(), campaign.CreatedBy))
        .OrderByDescending(campaign => campaign.CreatedAt)
        .ToArray();
    public IReadOnlyList<AccessReviewItem> ReviewItems => SelectedCampaign?.Items
        .Where(item => WorkspaceDataFormatting.Matches(SearchText, item.AccessSummary, item.AssignedReviewer, item.Decision.ToString(), item.ReviewComment, item.EvidenceSnapshot))
        .OrderBy(item => item.WorkflowDeadline ?? SelectedCampaign.DueAt)
        .ToArray() ?? Array.Empty<AccessReviewItem>();
    public IReadOnlyList<AccessReviewAuditEntry> SelectedAuditTrail => SelectedItem?.AuditTrail ?? Array.Empty<AccessReviewAuditEntry>();
    public IReadOnlyList<ReviewDecision> Decisions => AvailableDecisions;
    public IReadOnlyList<AccessReviewTemplate> Templates => BuiltInTemplates;
    public IReadOnlyList<ReviewerOption> EligibleReviewers => _eligibleReviewers;
    public bool IsSharedStore => _isSharedStore;
    public string ReviewerEligibilityMessage => _eligibleReviewers.Count == 0
        ? _isSharedStore
            ? "No eligible organization reviewers. Add an active Organization Admin or Security Analyst, or grant an approved custom review role."
            : "No eligible reviewers. Reviewers must be active Azure, AWS, or GCP users with an approved privileged role. Run a scan first."
        : _isSharedStore
            ? $"{_eligibleReviewers.Count} active organization reviewer(s) authorized to manage access reviews."
            : $"{_eligibleReviewers.Count} eligible reviewer(s): active cloud users with qualifying privileged access.";
    public bool HasCampaigns => FilteredCampaigns.Count > 0;
    public bool HasReviewItems => ReviewItems.Count > 0;
    public IReadOnlyList<AccessReviewAttestation> SelectedAttestations => SelectedItem?.Attestations ?? Array.Empty<AccessReviewAttestation>();
    public int OpenCampaigns => _campaigns.Count(item => item.Status == CampaignStatus.Open);
    public int EscalatedItems => _campaigns.Where(campaign => campaign.Status is not (CampaignStatus.Cancelled or CampaignStatus.Draft))
        .Sum(campaign => campaign.Items.Count(item => item.EscalatedAt.HasValue && IdentityGovernanceService.IsUnresolved(item, DateTime.UtcNow)));
    public int PendingItems => _campaigns.Where(campaign => campaign.Status is not (CampaignStatus.Cancelled or CampaignStatus.Draft))
        .Sum(campaign => campaign.Items.Count(item => IdentityGovernanceService.IsUnresolved(item, DateTime.UtcNow)));
    public IReadOnlyList<string> EligibleOwners => _eligibleOwners;
    public IReadOnlyList<GovernanceGroupDto> GovernanceGroups => _governanceReport?.Groups ?? Array.Empty<GovernanceGroupDto>();
    public IReadOnlyList<GovernanceWorkItemDto> GovernanceWork => _governanceReport?.Items ?? Array.Empty<GovernanceWorkItemDto>();
    public IReadOnlyList<GovernanceHistoryDto> GovernanceHistory => _governanceReport?.History ?? Array.Empty<GovernanceHistoryDto>();
    public string GovernanceReportStatus => _governanceReport is null
        ? "Organization-wide reporting is available in shared organization mode."
        : $"Organization-wide report as of {_governanceReport.AsOfUtc:u}; exception age buckets: <30, 30-89, 90+ days. Latest 500 audit events.";
    public string AssignedOwner { get => _assignedOwner; set => SetProperty(ref _assignedOwner, value); }
    public string AssignmentTeam { get => _assignmentTeam; set => SetProperty(ref _assignmentTeam, value); }
    public string AssignmentAccount { get => _assignmentAccount; set => SetProperty(ref _assignmentAccount, value); }
    public string AssignmentReason { get => _assignmentReason; set { if (SetProperty(ref _assignmentReason, value)) OnPropertyChanged(nameof(CanAssignReviewer)); } }
    public AccessReviewCampaign? SelectedCampaign
    {
        get => _selectedCampaign;
        set
        {
            if (!SetProperty(ref _selectedCampaign, value)) return;
            SelectedItem = value?.Items.FirstOrDefault();
            OnPropertyChanged(nameof(ReviewItems));
            OnPropertyChanged(nameof(HasReviewItems));
            OnPropertyChanged(nameof(CanCreateCampaign));
        }
    }
    public AccessReviewItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetProperty(ref _selectedItem, value)) return;
            AssignedReviewerName = value?.AssignedReviewer ?? string.Empty;
            AssignedOwner = value?.AssignedOwner ?? string.Empty;
            AssignmentTeam = value?.Team ?? string.Empty;
            AssignmentAccount = value?.ExternalAccountId ?? string.Empty;
            AssignmentReason = string.Empty;
            ReviewerName = value?.Reviewer ?? string.Empty;
            ReviewComment = value?.ReviewComment ?? string.Empty;
            ItemDueAt = value?.DueAt ?? SelectedCampaign?.DueAt;
            ExceptionExpiresAt = value?.ExceptionExpiresAt;
            SelectedDecision = value?.Decision is ReviewDecision.Pending or null ? ReviewDecision.Certified : value.Decision;
            OnPropertyChanged(nameof(SelectedAuditTrail));
            OnPropertyChanged(nameof(SelectedAttestations));
            OnPropertyChanged(nameof(CanAssignReviewer));
            OnPropertyChanged(nameof(CanRecordDecision));
        }
    }
    public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) { OnPropertyChanged(nameof(FilteredCampaigns)); OnPropertyChanged(nameof(ReviewItems)); OnPropertyChanged(nameof(HasCampaigns)); OnPropertyChanged(nameof(HasReviewItems)); } } }
    public string CampaignName { get => _campaignName; set { if (SetProperty(ref _campaignName, value)) OnPropertyChanged(nameof(CanCreateCampaign)); } }
    public string CampaignDurationDays { get => _campaignDurationDays; set { if (SetProperty(ref _campaignDurationDays, value)) OnPropertyChanged(nameof(CanCreateCampaign)); } }
    public AccessReviewTemplate SelectedTemplate
    {
        get => _selectedTemplate;
        set
        {
            if (!SetProperty(ref _selectedTemplate, value)) return;
            CampaignDurationDays = value.DurationDays.ToString();
        }
    }
    public string AssignedReviewerName
    {
        get => _assignedReviewerName;
        set
        {
            if (!SetProperty(ref _assignedReviewerName, value)) return;
            OnPropertyChanged(nameof(CanAssignReviewer));
            OnPropertyChanged(nameof(CanRecordDecision));
        }
    }
    public string ReviewerName { get => _reviewerName; set { if (SetProperty(ref _reviewerName, value)) { OnPropertyChanged(nameof(CanAssignReviewer)); OnPropertyChanged(nameof(CanRecordDecision)); } } }
    public string ActionActor { get => _actionActor; set { if (SetProperty(ref _actionActor, value)) { OnPropertyChanged(nameof(CanAssignReviewer)); OnPropertyChanged(nameof(CanRecordDecision)); } } }
    public string ReviewComment { get => _reviewComment; set { if (SetProperty(ref _reviewComment, value)) { OnPropertyChanged(nameof(CanRecordDecision)); } } }
    public string ReminderMessage { get => _reminderMessage; private set => SetProperty(ref _reminderMessage, value); }
    public DateTime? ItemDueAt { get => _itemDueAt; set { if (SetProperty(ref _itemDueAt, value)) OnPropertyChanged(nameof(CanAssignReviewer)); } }
    public DateTime? ExceptionExpiresAt { get => _exceptionExpiresAt; set { if (SetProperty(ref _exceptionExpiresAt, value)) OnPropertyChanged(nameof(CanRecordDecision)); } }
    public ReviewDecision SelectedDecision { get => _selectedDecision; set { if (SetProperty(ref _selectedDecision, value)) OnPropertyChanged(nameof(CanRecordDecision)); } }
    public bool CanCreateCampaign => !IsLoading && SelectedTemplate is not null && !string.IsNullOrWhiteSpace(CampaignName) && int.TryParse(CampaignDurationDays, out var days) && days is > 0 and <= 365;
    public bool CanAssignReviewer => SelectedCampaign is not null && SelectedItem is not null && IsEligibleReviewer(AssignedReviewerName) && !string.IsNullOrWhiteSpace(ActionActor) && ItemDueAt?.Date >= DateTime.Today
        && (!_isSharedStore || !string.IsNullOrWhiteSpace(AssignmentReason));
    public bool CanRecordDecision => SelectedCampaign is not null && SelectedItem is not null && !string.IsNullOrWhiteSpace(ActionActor)
        && !string.IsNullOrWhiteSpace(string.IsNullOrWhiteSpace(ReviewerName) ? AssignedReviewerName : ReviewerName)
        && (SelectedDecision != ReviewDecision.ExceptionGranted || ExceptionExpiresAt?.Date >= DateTime.Today)
        && (SelectedDecision == ReviewDecision.Certified || !string.IsNullOrWhiteSpace(ReviewComment));
    public string CoverageMessage => _isSharedStore
        ? "Review campaigns, assignments, decisions, evidence snapshots, and audit events are shared with the selected organization. They never grant, revoke, or alter cloud access."
        : "Review decisions and exceptions are local governance records only. They never grant, revoke, or alter cloud access.";
    public AsyncRelayCommand CreateCampaignCommand { get; }
    public AsyncRelayCommand AssignReviewerCommand { get; }
    public AsyncRelayCommand RecordDecisionCommand { get; }
    public AsyncRelayCommand RefreshCampaignsCommand { get; }

    public async Task RefreshAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        _governanceReport = null;
        _eligibleOwners = Array.Empty<string>();
        OnPropertyChanged(nameof(EligibleOwners));
        OnPropertyChanged(nameof(GovernanceGroups));
        OnPropertyChanged(nameof(GovernanceWork));
        OnPropertyChanged(nameof(GovernanceHistory));
        OnPropertyChanged(nameof(GovernanceReportStatus));
        try
        {
            var sharedReviewers = await _store.GetEligibleReviewerEmailsAsync();
            _eligibleReviewers = sharedReviewers is null
                ? await BuildEligibleReviewersAsync()
                : sharedReviewers.Select(email => new ReviewerOption(email, $"{email} (organization reviewer)")).ToArray();
            OnPropertyChanged(nameof(EligibleReviewers));
            OnPropertyChanged(nameof(ReviewerEligibilityMessage));
            OnPropertyChanged(nameof(CanAssignReviewer));
            var campaigns = await _store.GetAsync();
            var reviewed = new List<AccessReviewCampaign>(campaigns.Count);
            var remindersDue = 0;
            foreach (var campaign in campaigns)
            {
                var reevaluated = _isSharedStore ? campaign : _governance.ReevaluateCampaign(campaign);
                remindersDue += reevaluated.Items.Sum(item => item.ReminderCount)
                    - campaign.Items.Sum(item => item.ReminderCount);
                if (!_isSharedStore && HasLifecycleChanges(campaign, reevaluated)) await _store.SaveAsync(reevaluated);
                reviewed.Add(reevaluated);
            }
            ReminderMessage = _isSharedStore
                ? "The server evaluates reminders, expiry and escalation independently of this workspace (approximately every minute)."
                : remindersDue == 0
                ? "Reviewer reminders are surfaced when this workspace is refreshed."
                : $"{remindersDue} reviewer reminder(s) are due; see pending items and their assigned reviewers.";
            _campaigns = reviewed.OrderByDescending(item => item.CreatedAt).ToArray();
            if (_store is SharedAccessReviewStore { SupportsGovernance: true } shared)
            {
                _governanceReport = await shared.GetGovernanceReportAsync();
                _eligibleOwners = (await shared.GetGovernanceAssigneesAsync()).Owners;
                OnPropertyChanged(nameof(EligibleOwners));
                OnPropertyChanged(nameof(GovernanceGroups));
                OnPropertyChanged(nameof(GovernanceWork));
                OnPropertyChanged(nameof(GovernanceHistory));
                OnPropertyChanged(nameof(GovernanceReportStatus));
            }
            if (SelectedCampaign is not null)
                SelectedCampaign = _campaigns.FirstOrDefault(item => item.Id == SelectedCampaign.Id);
            else
                SelectedCampaign = _campaigns.FirstOrDefault();
            OnPropertyChanged(nameof(Campaigns));
            OnPropertyChanged(nameof(FilteredCampaigns));
            OnPropertyChanged(nameof(HasCampaigns));
            OnPropertyChanged(nameof(OpenCampaigns));
            OnPropertyChanged(nameof(EscalatedItems));
            OnPropertyChanged(nameof(PendingItems));
            OnPropertyChanged(nameof(CanCreateCampaign));
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to load access reviews: {exception.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task CreateCampaignAsync()
    {
        if (!CanCreateCampaign || !int.TryParse(CampaignDurationDays, out var days)) return;
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var candidates = (await GetCandidatesAsync()).Where(MatchesTemplate).ToArray();
            if (candidates.Length == 0)
            {
                ErrorMessage = "No reviewable effective-access records are available. Run a scan or load Demo Mode data first.";
                return;
            }
            var campaign = _governance.CreateCampaign(CampaignName.Trim(), candidates, TimeSpan.FromDays(days), asOf: DateTime.UtcNow,
                templateId: SelectedTemplate.Id, createdBy: ActionActor);
            foreach (var item in campaign.Items)
            {
                var ownerReviewer = item.OwnerSnapshot;
                var reviewer = IsEligibleReviewer(ownerReviewer) ? ownerReviewer : AssignedReviewerName;
                if (IsEligibleReviewer(reviewer))
                    campaign = _governance.AssignReviewer(campaign, item.Id, reviewer!, campaign.DueAt, ActionActor, DateTime.UtcNow);
            }
            await _store.SaveAsync(campaign);
            CampaignName = $"{CampaignName.Trim()} follow-up";
            await RefreshAsync();
            SelectedCampaign = _campaigns.FirstOrDefault(item => item.Id == campaign.Id);
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to create access review: {exception.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task AssignReviewerAsync()
    {
        if (!CanAssignReviewer || SelectedCampaign is null || SelectedItem is null || ItemDueAt is null) return;
        try
        {
            var campaign = SelectedCampaign;
            if (_store is SharedAccessReviewStore shared)
                campaign = await shared.ReassignAsync(campaign.Id, SelectedItem.Id,
                    new ReassignReviewItemRequest(SelectedItem.AssignmentVersion, AssignedOwner, AssignedReviewerName,
                        AssignmentTeam, AssignmentAccount, EndOfSelectedDayUtc(ItemDueAt.Value), AssignmentReason));
            else
            {
                campaign = _governance.AssignReviewer(campaign, SelectedItem.Id, AssignedReviewerName, EndOfSelectedDayUtc(ItemDueAt.Value), ActionActor);
                await _store.SaveAsync(campaign);
            }
            await RefreshAsync();
            var selectedItemId = SelectedItem.Id;
            SelectedCampaign = _campaigns.FirstOrDefault(item => item.Id == campaign.Id);
            SelectedItem = SelectedCampaign?.Items.FirstOrDefault(item => item.Id == selectedItemId);
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to assign reviewer: {exception.Message}";
        }
    }

    public async Task RecordDecisionAsync()
    {
        if (!CanRecordDecision || SelectedCampaign is null || SelectedItem is null) return;
        try
        {
            var reviewer = string.IsNullOrWhiteSpace(ReviewerName) ? AssignedReviewerName : ReviewerName;
            var campaign = _governance.RecordDecision(SelectedCampaign, SelectedItem.Id, SelectedDecision, reviewer, ReviewComment,
                SelectedDecision == ReviewDecision.ExceptionGranted && ExceptionExpiresAt.HasValue ? EndOfSelectedDayUtc(ExceptionExpiresAt.Value) : null,
                actor: ActionActor);
            await _store.SaveAsync(campaign);
            await RefreshAsync();
            var selectedItemId = SelectedItem.Id;
            SelectedCampaign = _campaigns.FirstOrDefault(item => item.Id == campaign.Id);
            SelectedItem = SelectedCampaign?.Items.FirstOrDefault(item => item.Id == selectedItemId);
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to record review decision: {exception.Message}";
        }
    }

    private async Task<IReadOnlyList<AccessReviewCandidate>> GetCandidatesAsync()
    {
        if (_demoData.IsDemoMode)
        {
            var snapshot = await _demoData.GetSnapshotAsync();
            var roles = snapshot.Roles.ToDictionary(item => item.Id);
            var permissions = snapshot.Permissions.ToDictionary(item => item.Id);
            var principals = snapshot.Principals.ToDictionary(item => item.Id);
            return snapshot.EffectiveAccess
                .Where(access => access.Decision is AccessDecision.Allow or AccessDecision.Conditional)
                .Select(access =>
                {
                    var principal = principals.GetValueOrDefault(access.PrincipalId);
                    return new AccessReviewCandidate(
                        access.PrincipalId,
                        access.Id,
                        access.AccessPathId,
                        access.Provider,
                        $"Decision={access.Decision}; Role={roles.GetValueOrDefault(access.RoleId ?? Guid.Empty)?.Name ?? "unknown"}; Permission={permissions.GetValueOrDefault(access.PermissionId)?.Name ?? access.PermissionId.ToString()}; Scope={access.Scope ?? "unknown"}",
                        $"Provider={access.Provider}; Decision={access.Decision}; Evaluation={access.EvaluationStatus}; Source={access.AccessSource ?? "not reported"}; Owner={principal?.OwnerIds.FirstOrDefault() ?? "not reported"}; Evidence={access.Evidence ?? "not recorded"}",
                        principal?.OwnerIds.FirstOrDefault(), principal?.IdentityType ?? IdentityType.Unknown,
                        access.IsPrivileged || principal?.IsPrivileged == true, principal?.IsExternal == true);
                })
                .ToArray();
        }

        var records = _liveScan?.CurrentResult?.EffectiveAccessRecords ?? Array.Empty<LiveEffectiveAccessRecord>();
        var identities = _liveScan?.CurrentResult?.IdentityRecords ?? Array.Empty<CloudPrincipal>();
        return records.Where(item => !item.AssignmentType.Equals("Role usage", StringComparison.OrdinalIgnoreCase))
            .Select(item =>
            {
                var principal = identities.FirstOrDefault(identity => identity.Name.Equals(item.Principal, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(identity.PrincipalId, item.Principal, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(identity.Email, item.Principal, StringComparison.OrdinalIgnoreCase));
                var principalId = StableId($"principal|{item.Provider}|{item.AccountId}|{item.Principal}");
                var accessId = StableId($"access|{item.Provider}|{item.AccountId}|{item.Principal}|{item.Role}|{item.Scope}|{item.AssignmentType}");
                var provider = item.Provider.Equals("Azure", StringComparison.OrdinalIgnoreCase) ? CloudProvider.Azure
                    : item.Provider.Equals("Aws", StringComparison.OrdinalIgnoreCase) ? CloudProvider.Aws
                    : item.Provider.Equals("Gcp", StringComparison.OrdinalIgnoreCase) ? CloudProvider.Gcp
                    : CloudProvider.Kubernetes;
                var summary = $"{item.AssignmentType}: {item.Role} at {item.Scope}";
                var owner = principal?.OwnerIds.FirstOrDefault();
                var evidence = $"Provider={item.Provider}; Account={item.AccountId ?? "not reported"}; Principal={item.Principal}; Role={item.Role}; Scope={item.Scope}; Source={item.Source}; Owner={owner ?? "not reported"}; LastUsed={item.LastUsedAt?.ToString("O") ?? "not reported"}; LastChanged={item.LastChangedAt?.ToString("O") ?? "not reported"}; LastChangedBy={item.LastChangedBy ?? "not reported"}";
                return new AccessReviewCandidate(principalId, accessId, null, provider, summary, evidence, owner,
                    principal?.IdentityType ?? IdentityType.Unknown, item.IsPrivileged || principal?.IsPrivileged == true, principal?.IsExternal == true)
                    { ExternalAccountId = item.AccountId };
            }).ToArray();
    }

    private bool IsEligibleReviewer(string? reviewer)
        => !string.IsNullOrWhiteSpace(reviewer)
            && _eligibleReviewers.Any(option => option.Value.Equals(reviewer.Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool IsQualifyingRole(string? role)
        => !string.IsNullOrWhiteSpace(role)
            && QualifyingRoles.Any(name => role.Contains(name, StringComparison.OrdinalIgnoreCase));

    private async Task<IReadOnlyList<ReviewerOption>> BuildEligibleReviewersAsync()
    {
        var grants = new Dictionary<string, (string Name, string Provider, SortedSet<string> Roles)>(StringComparer.OrdinalIgnoreCase);

        void Add(CloudPrincipal? principal, string provider, string? role)
        {
            if (principal is null || principal.IdentityType != IdentityType.User || !principal.IsActive || !IsQualifyingRole(role)) return;
            var value = string.IsNullOrWhiteSpace(principal.Email) ? principal.Name : principal.Email!;
            if (string.IsNullOrWhiteSpace(value)) return;
            if (!grants.TryGetValue(value, out var entry)) grants[value] = entry = (principal.Name, provider, new SortedSet<string>(StringComparer.OrdinalIgnoreCase));
            entry.Roles.Add(role!);
        }

        if (_demoData.IsDemoMode)
        {
            var snapshot = await _demoData.GetSnapshotAsync();
            var roles = snapshot.Roles.ToDictionary(item => item.Id);
            var principals = snapshot.Principals.ToDictionary(item => item.Id);
            foreach (var access in snapshot.EffectiveAccess.Where(item => item.Decision is AccessDecision.Allow or AccessDecision.Conditional && item.Provider != CloudProvider.Kubernetes))
                Add(principals.GetValueOrDefault(access.PrincipalId), access.Provider.ToString(), roles.GetValueOrDefault(access.RoleId ?? Guid.Empty)?.Name);
        }
        else if (_liveScan?.CurrentResult is { } scan)
        {
            foreach (var record in scan.EffectiveAccessRecords)
            {
                var provider = record.Provider;
                if (!(provider.Equals("Azure", StringComparison.OrdinalIgnoreCase) || provider.Equals("Aws", StringComparison.OrdinalIgnoreCase) || provider.Equals("Gcp", StringComparison.OrdinalIgnoreCase))) continue;
                if (record.AssignmentType.Contains("Role usage", StringComparison.OrdinalIgnoreCase) || record.AssignmentType.Contains("Eligible", StringComparison.OrdinalIgnoreCase)) continue;
                var principal = scan.IdentityRecords.FirstOrDefault(identity => identity.Name.Equals(record.Principal, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(identity.PrincipalId, record.Principal, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(identity.Email, record.Principal, StringComparison.OrdinalIgnoreCase));
                Add(principal, provider, record.Role);
            }
        }

        return grants.OrderBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => new ReviewerOption(item.Key, $"{item.Value.Name} ({item.Value.Provider}) - {string.Join(", ", item.Value.Roles)}"))
            .ToArray();
    }

    private bool MatchesTemplate(AccessReviewCandidate candidate)
        => (!SelectedTemplate.PrivilegedOnly || candidate.IsPrivileged)
            && (!SelectedTemplate.WorkloadIdentitiesOnly || candidate.IdentityType is IdentityType.ServicePrincipal or IdentityType.ManagedIdentity or IdentityType.ServiceAccount or IdentityType.Application)
            && (!SelectedTemplate.ExternalOnly || candidate.IsExternal);

    private static bool HasLifecycleChanges(AccessReviewCampaign before, AccessReviewCampaign after)
    {
        if (before.Status != after.Status) return true;
        return before.Items.Zip(after.Items).Any(pair => pair.First.EscalatedAt != pair.Second.EscalatedAt
            || pair.First.ExceptionExpiredAt != pair.Second.ExceptionExpiredAt
            || pair.First.ReminderCount != pair.Second.ReminderCount
            || pair.First.AuditTrail.Count != pair.Second.AuditTrail.Count);
    }

    private static DateTime EndOfSelectedDayUtc(DateTime selectedDate)
        => DateTime.SpecifyKind(selectedDate.Date.AddDays(1).AddTicks(-1), DateTimeKind.Local).ToUniversalTime();

    private static Guid StableId(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
