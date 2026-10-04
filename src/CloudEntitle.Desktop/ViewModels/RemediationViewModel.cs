using CloudEntitle.Application.Interfaces;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.RemediationPlanning.Abstractions;
using CloudEntitle.RemediationPlanning.Models;
using CloudEntitle.RemediationPlanning.Services;
using CloudEntitle.Recommendations.Models;
using DomainAccessPath = CloudEntitle.Domain.Entities.AccessPath;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;

namespace CloudEntitle.Desktop.ViewModels;

public sealed class RemediationViewModel : WorkspaceViewModel
{
    private readonly IDemoDataService _demoData;
    private readonly IRecommendationStore _recommendationStore;
    private readonly IRemediationPlanStore _planStore;
    private readonly RemediationWorkflowService _workflow;
    private readonly ILiveCloudScanService? _liveScan;
    private DemoSnapshot? _snapshot;
    private IReadOnlyList<RecommendationModel> _recommendations = Array.Empty<RecommendationModel>();
    private IReadOnlyList<RemediationPlan> _plans = Array.Empty<RemediationPlan>();
    private RecommendationModel? _selectedRecommendation;
    private RemediationPlan? _selectedPlan;
    private string _searchText = string.Empty;
    private string _requestedBy = Environment.UserName;
    private string _reviewComment = string.Empty;
    private string _statusMessage = "Create a review plan from an evidence-backed recommendation.";

    public RemediationViewModel(
        IDemoDataService demoData,
        IRecommendationStore recommendationStore,
        IRemediationPlanStore planStore,
        IRemediationPlanner planner,
        IRemediationSimulator simulator,
        IRemediationEngine engine,
        ILiveCloudScanService? liveScan = null) : base(demoData)
    {
        _demoData = demoData;
        _recommendationStore = recommendationStore;
        _planStore = planStore;
        _workflow = new RemediationWorkflowService(planner, simulator, engine);
        _liveScan = liveScan;
        CreatePlanCommand = new AsyncRelayCommand(CreatePlanAsync);
        ApprovePlanCommand = new AsyncRelayCommand(ApprovePlanAsync);
        RejectPlanCommand = new AsyncRelayCommand(RejectPlanAsync);
        RunDryRunCommand = new AsyncRelayCommand(RunDryRunAsync);
        VerifyPlanCommand = new AsyncRelayCommand(VerifyPlanAsync);
        PrepareRollbackCommand = new AsyncRelayCommand(PrepareRollbackAsync);
        _demoData.DataChanged += async (_, _) => await RefreshAsync();
        if (_liveScan is not null) _liveScan.ScanCompleted += async (_, _) => await RefreshAsync();
        _ = RefreshAsync();
    }

    public override string Title => "Remediation";
    public override string Subtitle => "Approve, dry-run, verify, and prepare rollback drafts; cloud changes remain disabled in this build.";
    public IReadOnlyList<RecommendationModel> Recommendations => _recommendations
        .Where(item => WorkspaceDataFormatting.Matches(SearchText, item.Title, item.Provider.ToString(), item.CurrentState, item.SuggestedState, item.Reason))
        .ToArray();
    public IReadOnlyList<RemediationPlan> Plans => _plans
        .Where(item => WorkspaceDataFormatting.Matches(SearchText, item.Recommendation.Title, item.Provider.ToString(), item.Status.ToString(), item.SafetyNotice))
        .OrderByDescending(item => item.UpdatedAt)
        .ToArray();
    public IReadOnlyList<RemediationAuditEntry> SelectedAuditTrail => SelectedPlan?.AuditTrail ?? Array.Empty<RemediationAuditEntry>();
    public IReadOnlyList<RemediationChange> SelectedChanges => SelectedPlan?.Changes ?? Array.Empty<RemediationChange>();
    public IReadOnlyList<RemediationChange> SelectedRollbackChanges => SelectedPlan?.RollbackChanges ?? Array.Empty<RemediationChange>();
    public bool HasPlans => Plans.Count > 0;
    public bool HasRecommendations => Recommendations.Count > 0;
    public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) NotifyCollections(); } }
    public string RequestedBy { get => _requestedBy; set { if (SetProperty(ref _requestedBy, value)) NotifyActions(); } }
    public string ReviewComment { get => _reviewComment; set { if (SetProperty(ref _reviewComment, value)) NotifyActions(); } }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public RecommendationModel? SelectedRecommendation
    {
        get => _selectedRecommendation;
        set
        {
            if (!SetProperty(ref _selectedRecommendation, value)) return;
            OnPropertyChanged(nameof(CanCreatePlan));
        }
    }
    public RemediationPlan? SelectedPlan
    {
        get => _selectedPlan;
        set
        {
            if (!SetProperty(ref _selectedPlan, value)) return;
            OnPropertyChanged(nameof(SelectedAuditTrail));
            OnPropertyChanged(nameof(SelectedChanges));
            OnPropertyChanged(nameof(SelectedRollbackChanges));
            OnPropertyChanged(nameof(CanApprovePlan));
            OnPropertyChanged(nameof(CanRejectPlan));
            OnPropertyChanged(nameof(CanRunDryRun));
            OnPropertyChanged(nameof(CanVerifyPlan));
            OnPropertyChanged(nameof(CanPrepareRollback));
        }
    }
    public bool CanCreatePlan => SelectedRecommendation is not null && !string.IsNullOrWhiteSpace(RequestedBy);
    public bool CanApprovePlan => SelectedPlan is { Status: RemediationPlanStatus.RequiresReview or RemediationPlanStatus.Draft }
        && !string.IsNullOrWhiteSpace(RequestedBy) && !string.IsNullOrWhiteSpace(ReviewComment);
    public bool CanRejectPlan => SelectedPlan is { Status: RemediationPlanStatus.RequiresReview or RemediationPlanStatus.Draft }
        && !string.IsNullOrWhiteSpace(RequestedBy) && !string.IsNullOrWhiteSpace(ReviewComment);
    public bool CanRunDryRun => SelectedPlan is { Status: RemediationPlanStatus.Approved }
        && !string.IsNullOrWhiteSpace(RequestedBy) && ContextHasEvidence(SelectedPlan);
    public bool CanVerifyPlan => SelectedPlan?.LastExecution?.MutatedCloud == true
        && SelectedPlan.LastExecution.Status is RemediationExecutionStatus.Executed or RemediationExecutionStatus.VerificationFailed;
    public bool CanPrepareRollback => SelectedPlan is { Status: RemediationPlanStatus.DryRunCompleted or RemediationPlanStatus.Implemented or RemediationPlanStatus.VerificationFailed }
        && SelectedPlan.RollbackChanges.Count > 0 && !string.IsNullOrWhiteSpace(RequestedBy) && !string.IsNullOrWhiteSpace(ReviewComment);
    public string SimulationSummary => SelectedPlan?.Simulation is not { } simulation
        ? "No dry-run results yet."
        : $"{simulation.Status}; access impact: {simulation.AccessImpact}; affected: {simulation.AffectedEffectiveAccess.Count}; potential loss: {simulation.PotentiallyLostEffectiveAccess.Count}.";
    public string VerificationSummary => SelectedPlan?.Verification is not { } verification
        ? "Verification has not run. Dry runs do not change cloud access."
        : $"{verification.Status}: {verification.Details}";
    public AsyncRelayCommand CreatePlanCommand { get; }
    public AsyncRelayCommand ApprovePlanCommand { get; }
    public AsyncRelayCommand RejectPlanCommand { get; }
    public AsyncRelayCommand RunDryRunCommand { get; }
    public AsyncRelayCommand VerifyPlanCommand { get; }
    public AsyncRelayCommand PrepareRollbackCommand { get; }

    public async Task RefreshAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            _snapshot = await _demoData.GetSnapshotAsync();
            _recommendations = await _recommendationStore.GetAsync(SearchText);
            _plans = await _planStore.GetAsync();
            if (SelectedRecommendation is not null) SelectedRecommendation = _recommendations.FirstOrDefault(item => item.Id == SelectedRecommendation.Id);
            else SelectedRecommendation = _recommendations.FirstOrDefault();
            if (SelectedPlan is not null) SelectedPlan = _plans.FirstOrDefault(item => item.Id == SelectedPlan.Id);
            else SelectedPlan = Plans.FirstOrDefault();
            NotifyCollections();
            OnPropertyChanged(nameof(SimulationSummary));
            OnPropertyChanged(nameof(VerificationSummary));
            NotifyActions();
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to load remediation plans: {exception.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task CreatePlanAsync()
    {
        if (!CanCreatePlan || SelectedRecommendation is null || _snapshot is null) return;
        try
        {
            var context = BuildContext();
            var plan = _workflow.CreatePlan(SelectedRecommendation, context, RequestedBy);
            await _planStore.SaveAsync(plan);
            StatusMessage = plan.Status == RemediationPlanStatus.RequiresReview
                ? "Review plan created with linked access evidence. Approve it before the dry run."
                : "Draft created with incomplete recommendation evidence; resolve the evidence gap before approval.";
            await RefreshAsync();
            SelectedPlan = Plans.FirstOrDefault(item => item.Id == plan.Id);
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to create remediation plan: {exception.Message}";
        }
    }

    public async Task ApprovePlanAsync()
    {
        if (!CanApprovePlan || SelectedPlan is null) return;
        try
        {
            var plan = _workflow.Approve(SelectedPlan, RequestedBy, ReviewComment);
            await _planStore.SaveAsync(plan);
            StatusMessage = "Approval recorded locally. The next step is a non-mutating dry run.";
            await RefreshAsync();
            SelectedPlan = Plans.FirstOrDefault(item => item.Id == plan.Id);
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to approve remediation plan: {exception.Message}";
        }
    }

    public async Task RejectPlanAsync()
    {
        if (!CanRejectPlan || SelectedPlan is null) return;
        try
        {
            var plan = _workflow.Reject(SelectedPlan, RequestedBy, ReviewComment);
            await _planStore.SaveAsync(plan);
            StatusMessage = "Rejection and rationale recorded in the local audit trail.";
            await RefreshAsync();
            SelectedPlan = Plans.FirstOrDefault(item => item.Id == plan.Id);
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to reject remediation plan: {exception.Message}";
        }
    }

    public async Task RunDryRunAsync()
    {
        if (!CanRunDryRun || SelectedPlan is null) return;
        try
        {
            var plan = await _workflow.RunDryRunAsync(SelectedPlan, BuildContext(), RequestedBy);
            await _planStore.SaveAsync(plan);
            StatusMessage = plan.LastExecution?.Status == RemediationExecutionStatus.DryRunCompleted
                ? "Dry run completed. No cloud changes were made. Provider authorization was not re-evaluated."
                : "Dry run was blocked; see diagnostics in the plan.";
            await RefreshAsync();
            SelectedPlan = Plans.FirstOrDefault(item => item.Id == plan.Id);
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to run remediation dry run: {exception.Message}";
        }
    }

    public async Task VerifyPlanAsync()
    {
        if (!CanVerifyPlan || SelectedPlan is null) return;
        try
        {
            var plan = await _workflow.VerifyAsync(SelectedPlan, RequestedBy);
            await _planStore.SaveAsync(plan);
            StatusMessage = plan.Verification?.Details ?? "Verification status unavailable.";
            await RefreshAsync();
            SelectedPlan = Plans.FirstOrDefault(item => item.Id == plan.Id);
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to verify remediation: {exception.Message}";
        }
    }

    public async Task PrepareRollbackAsync()
    {
        if (!CanPrepareRollback || SelectedPlan is null) return;
        try
        {
            var rollbackPlan = _workflow.PrepareRollback(SelectedPlan, RequestedBy, ReviewComment);
            await _planStore.SaveAsync(rollbackPlan);
            StatusMessage = "A non-executable rollback draft was created. It requires separate review and approval.";
            await RefreshAsync();
            SelectedPlan = Plans.FirstOrDefault(item => item.Id == rollbackPlan.Id);
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to prepare rollback draft: {exception.Message}";
        }
    }

    private bool ContextHasEvidence(RemediationPlan plan)
        => plan.Recommendation.EffectiveAccessId.HasValue && _snapshot?.EffectiveAccess.Any(item => item.Id == plan.Recommendation.EffectiveAccessId.Value) == true;

    private RemediationContext BuildContext()
        => new(_recommendations, _snapshot?.EffectiveAccess ?? Array.Empty<DomainEffectiveAccess>(), _snapshot?.AccessPaths ?? Array.Empty<DomainAccessPath>());

    private void NotifyCollections()
    {
        OnPropertyChanged(nameof(Recommendations));
        OnPropertyChanged(nameof(Plans));
        OnPropertyChanged(nameof(HasRecommendations));
        OnPropertyChanged(nameof(HasPlans));
    }

    private void NotifyActions()
    {
        OnPropertyChanged(nameof(CanCreatePlan));
        OnPropertyChanged(nameof(CanApprovePlan));
        OnPropertyChanged(nameof(CanRunDryRun));
        OnPropertyChanged(nameof(CanVerifyPlan));
        OnPropertyChanged(nameof(CanPrepareRollback));
    }
}
