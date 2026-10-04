using System.Security.Cryptography;
using System.Text;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.IdentityGovernance.Models;
using CloudEntitle.IdentityGovernance.Services;

namespace CloudEntitle.Desktop.ViewModels;

public sealed class AccessReviewViewModel : WorkspaceViewModel
{
    private static readonly IReadOnlyList<ReviewDecision> AvailableDecisions = new[]
    {
        ReviewDecision.Certified,
        ReviewDecision.NeedsInvestigation,
        ReviewDecision.Rejected,
        ReviewDecision.ExceptionGranted
    };

    private readonly IDemoDataService _demoData;
    private readonly IAccessReviewStore _store;
    private readonly ILiveCloudScanService? _liveScan;
    private readonly IdentityGovernanceService _governance;
    private IReadOnlyList<AccessReviewCampaign> _campaigns = Array.Empty<AccessReviewCampaign>();
    private AccessReviewCampaign? _selectedCampaign;
    private AccessReviewItem? _selectedItem;
    private string _searchText = string.Empty;
    private string _campaignName = "Quarterly cloud access review";
    private string _campaignDurationDays = "14";
    private string _assignedReviewerName = Environment.UserName;
    private string _reviewerName = string.Empty;
    private string _actionActor = Environment.UserName;
    private string _reviewComment = string.Empty;
    private DateTime? _itemDueAt;
    private DateTime? _exceptionExpiresAt;
    private ReviewDecision _selectedDecision = ReviewDecision.Certified;

    public AccessReviewViewModel(IDemoDataService demoData, IAccessReviewStore store, ILiveCloudScanService? liveScan = null)
    {
        _demoData = demoData;
        _store = store;
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
        .OrderBy(item => item.DueAt ?? SelectedCampaign.DueAt)
        .ToArray() ?? Array.Empty<AccessReviewItem>();
    public IReadOnlyList<AccessReviewAuditEntry> SelectedAuditTrail => SelectedItem?.AuditTrail ?? Array.Empty<AccessReviewAuditEntry>();
    public IReadOnlyList<ReviewDecision> Decisions => AvailableDecisions;
    public bool HasCampaigns => FilteredCampaigns.Count > 0;
    public bool HasReviewItems => ReviewItems.Count > 0;
    public int OpenCampaigns => _campaigns.Count(item => item.Status == CampaignStatus.Open);
    public int EscalatedItems => _campaigns.Sum(campaign => campaign.Items.Count(item => item.EscalatedAt.HasValue));
    public int PendingItems => _campaigns.Sum(campaign => campaign.Items.Count(item => item.Decision == ReviewDecision.Pending || item.ExceptionExpiredAt.HasValue));
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
            ReviewerName = value?.Reviewer ?? string.Empty;
            ReviewComment = value?.ReviewComment ?? string.Empty;
            ItemDueAt = value?.DueAt ?? SelectedCampaign?.DueAt;
            ExceptionExpiresAt = value?.ExceptionExpiresAt;
            SelectedDecision = value?.Decision is ReviewDecision.Pending or null ? ReviewDecision.Certified : value.Decision;
            OnPropertyChanged(nameof(SelectedAuditTrail));
            OnPropertyChanged(nameof(CanAssignReviewer));
            OnPropertyChanged(nameof(CanRecordDecision));
        }
    }
    public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) { OnPropertyChanged(nameof(FilteredCampaigns)); OnPropertyChanged(nameof(ReviewItems)); OnPropertyChanged(nameof(HasCampaigns)); OnPropertyChanged(nameof(HasReviewItems)); } } }
    public string CampaignName { get => _campaignName; set { if (SetProperty(ref _campaignName, value)) OnPropertyChanged(nameof(CanCreateCampaign)); } }
    public string CampaignDurationDays { get => _campaignDurationDays; set { if (SetProperty(ref _campaignDurationDays, value)) OnPropertyChanged(nameof(CanCreateCampaign)); } }
    public string AssignedReviewerName { get => _assignedReviewerName; set { if (SetProperty(ref _assignedReviewerName, value)) OnPropertyChanged(nameof(CanAssignReviewer)); } }
    public string ReviewerName { get => _reviewerName; set { if (SetProperty(ref _reviewerName, value)) { OnPropertyChanged(nameof(CanAssignReviewer)); OnPropertyChanged(nameof(CanRecordDecision)); } } }
    public string ActionActor { get => _actionActor; set { if (SetProperty(ref _actionActor, value)) { OnPropertyChanged(nameof(CanAssignReviewer)); OnPropertyChanged(nameof(CanRecordDecision)); } } }
    public string ReviewComment { get => _reviewComment; set { if (SetProperty(ref _reviewComment, value)) { OnPropertyChanged(nameof(CanRecordDecision)); } } }
    public DateTime? ItemDueAt { get => _itemDueAt; set { if (SetProperty(ref _itemDueAt, value)) OnPropertyChanged(nameof(CanAssignReviewer)); } }
    public DateTime? ExceptionExpiresAt { get => _exceptionExpiresAt; set { if (SetProperty(ref _exceptionExpiresAt, value)) OnPropertyChanged(nameof(CanRecordDecision)); } }
    public ReviewDecision SelectedDecision { get => _selectedDecision; set { if (SetProperty(ref _selectedDecision, value)) OnPropertyChanged(nameof(CanRecordDecision)); } }
    public bool CanCreateCampaign => !IsLoading && !string.IsNullOrWhiteSpace(CampaignName) && int.TryParse(CampaignDurationDays, out var days) && days is > 0 and <= 365;
    public bool CanAssignReviewer => SelectedCampaign is not null && SelectedItem is not null && !string.IsNullOrWhiteSpace(AssignedReviewerName) && !string.IsNullOrWhiteSpace(ActionActor) && ItemDueAt?.Date >= DateTime.Today;
    public bool CanRecordDecision => SelectedCampaign is not null && SelectedItem is not null && !string.IsNullOrWhiteSpace(ReviewerName) && !string.IsNullOrWhiteSpace(ActionActor)
        && (SelectedDecision != ReviewDecision.ExceptionGranted || ExceptionExpiresAt?.Date >= DateTime.Today)
        && (SelectedDecision == ReviewDecision.Certified || !string.IsNullOrWhiteSpace(ReviewComment));
    public string CoverageMessage => "Review decisions and exceptions are local governance records only. They never grant, revoke, or alter cloud access.";
    public AsyncRelayCommand CreateCampaignCommand { get; }
    public AsyncRelayCommand AssignReviewerCommand { get; }
    public AsyncRelayCommand RecordDecisionCommand { get; }
    public AsyncRelayCommand RefreshCampaignsCommand { get; }

    public async Task RefreshAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var campaigns = await _store.GetAsync();
            var reviewed = new List<AccessReviewCampaign>(campaigns.Count);
            foreach (var campaign in campaigns)
            {
                var reevaluated = _governance.ReevaluateCampaign(campaign);
                if (HasLifecycleChanges(campaign, reevaluated)) await _store.SaveAsync(reevaluated);
                reviewed.Add(reevaluated);
            }
            _campaigns = reviewed.OrderByDescending(item => item.CreatedAt).ToArray();
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
            var candidates = await GetCandidatesAsync();
            if (candidates.Count == 0)
            {
                ErrorMessage = "No reviewable effective-access records are available. Run a scan or load Demo Mode data first.";
                return;
            }
            var campaign = _governance.CreateCampaign(CampaignName.Trim(), candidates, TimeSpan.FromDays(days), asOf: DateTime.UtcNow);
            if (!string.IsNullOrWhiteSpace(AssignedReviewerName))
            {
                foreach (var item in campaign.Items)
                    campaign = _governance.AssignReviewer(campaign, item.Id, AssignedReviewerName, campaign.DueAt, ActionActor, DateTime.UtcNow);
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
            var campaign = _governance.AssignReviewer(SelectedCampaign, SelectedItem.Id, AssignedReviewerName, EndOfSelectedDayUtc(ItemDueAt.Value), ActionActor);
            await _store.SaveAsync(campaign);
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
            var campaign = _governance.RecordDecision(SelectedCampaign, SelectedItem.Id, SelectedDecision, ReviewerName, ReviewComment,
                SelectedDecision == ReviewDecision.ExceptionGranted && ExceptionExpiresAt.HasValue ? EndOfSelectedDayUtc(ExceptionExpiresAt.Value) : null);
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
            return snapshot.EffectiveAccess
                .Where(access => access.Decision is AccessDecision.Allow or AccessDecision.Conditional)
                .Select(access => new AccessReviewCandidate(
                    access.PrincipalId,
                    access.Id,
                    access.AccessPathId,
                    access.Provider,
                    $"Decision={access.Decision}; Role={roles.GetValueOrDefault(access.RoleId ?? Guid.Empty)?.Name ?? "unknown"}; Permission={permissions.GetValueOrDefault(access.PermissionId)?.Name ?? access.PermissionId.ToString()}; Scope={access.Scope ?? "unknown"}",
                    $"Provider={access.Provider}; Decision={access.Decision}; Evaluation={access.EvaluationStatus}; Source={access.AccessSource ?? "not reported"}; Evidence={access.Evidence ?? "not recorded"}"))
                .ToArray();
        }

        var records = _liveScan?.CurrentResult?.EffectiveAccessRecords ?? Array.Empty<LiveEffectiveAccessRecord>();
        return records.Where(item => !item.AssignmentType.Equals("Role usage", StringComparison.OrdinalIgnoreCase))
            .Select(item =>
            {
                var principalId = StableId($"principal|{item.Provider}|{item.AccountId}|{item.Principal}");
                var accessId = StableId($"access|{item.Provider}|{item.AccountId}|{item.Principal}|{item.Role}|{item.Scope}|{item.AssignmentType}");
                var provider = item.Provider.Equals("Azure", StringComparison.OrdinalIgnoreCase) ? CloudProvider.Azure
                    : item.Provider.Equals("Aws", StringComparison.OrdinalIgnoreCase) ? CloudProvider.Aws
                    : item.Provider.Equals("Gcp", StringComparison.OrdinalIgnoreCase) ? CloudProvider.Gcp
                    : CloudProvider.Kubernetes;
                var summary = $"{item.AssignmentType}: {item.Role} at {item.Scope}";
                var evidence = $"Provider={item.Provider}; Account={item.AccountId ?? "not reported"}; Principal={item.Principal}; Role={item.Role}; Scope={item.Scope}; Source={item.Source}; LastUsed={item.LastUsedAt?.ToString("O") ?? "not reported"}; LastChanged={item.LastChangedAt?.ToString("O") ?? "not reported"}; LastChangedBy={item.LastChangedBy ?? "not reported"}";
                return new AccessReviewCandidate(principalId, accessId, null, provider, summary, evidence);
            }).ToArray();
    }

    private static bool HasLifecycleChanges(AccessReviewCampaign before, AccessReviewCampaign after)
    {
        if (before.Status != after.Status) return true;
        return before.Items.Zip(after.Items).Any(pair => pair.First.EscalatedAt != pair.Second.EscalatedAt
            || pair.First.ExceptionExpiredAt != pair.Second.ExceptionExpiredAt
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
