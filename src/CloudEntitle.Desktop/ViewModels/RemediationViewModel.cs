using CloudEntitle.Application.Interfaces;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.RemediationPlanning.Abstractions;
using CloudEntitle.RemediationPlanning.Models;
using CloudEntitle.RemediationPlanning.Services;
using CloudEntitle.Recommendations.Models;
using CloudEntitle.OrganizationManagement.Models;
using CloudEntitle.OrganizationManagement.Services;
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
    private readonly AuthenticatedOrganizationContext _authenticatedContext;
    private readonly OrganizationManagementService _organizationService;
    private DemoSnapshot? _snapshot;
    private IReadOnlyList<RecommendationModel> _recommendations = Array.Empty<RecommendationModel>();
    private IReadOnlyList<RemediationPlan> _plans = Array.Empty<RemediationPlan>();
    private RecommendationModel? _selectedRecommendation;
    private RemediationPlan? _selectedPlan;
    private string _searchText = string.Empty;
    private string _reviewComment = string.Empty;
    private string _statusMessage = "Create a review plan from an evidence-backed recommendation.";

    public RemediationViewModel(
        IDemoDataService demoData,
        IRecommendationStore recommendationStore,
        IRemediationPlanStore planStore,
        IRemediationPlanner planner,
        IRemediationSimulator simulator,
        IRemediationEngine engine,
        ILiveCloudScanService? liveScan,
        AuthenticatedOrganizationContext authenticatedContext,
        OrganizationManagementService organizationService) : base(demoData)
    {
        _demoData = demoData;
        _recommendationStore = recommendationStore;
        _planStore = planStore;
        _workflow = new RemediationWorkflowService(planner, simulator, engine);
        _liveScan = liveScan;
        _authenticatedContext = authenticatedContext;
        _organizationService = organizationService;
        _authenticatedContext.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(SignedInAs));
            NotifyActions();
        };
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
    public string ReviewComment { get => _reviewComment; set { if (SetProperty(ref _reviewComment, value)) NotifyActions(); } }
    public string SignedInAs => _authenticatedContext.Identity?.Email ?? "Not signed in";
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
            OnPropertyChanged(nameof(PreviewFingerprint));
            OnPropertyChanged(nameof(CanApprovePlan));
            OnPropertyChanged(nameof(CanRejectPlan));
            OnPropertyChanged(nameof(ApprovalButtonText));
            OnPropertyChanged(nameof(CanRunDryRun));
            OnPropertyChanged(nameof(CanVerifyPlan));
            OnPropertyChanged(nameof(CanPrepareRollback));
        }
    }
    public bool CanCreatePlan => SelectedRecommendation is not null && HasRemediationPermission();
    public bool CanApprovePlan
    {
        get
        {
            var plan = SelectedPlan;
            var identity = _authenticatedContext.Identity;
            if (plan is null || identity is null || !IsCurrentOrganizationPlan(plan) || !HasRemediationPermission()
                || plan.Recommendation.EvaluationState != RiskEvaluationState.Confirmed
                || plan.CreatedByUserId is null || plan.CreatedByUserId == identity.UserId
                || string.IsNullOrWhiteSpace(ReviewComment)) return false;
            if (plan.Status == RemediationPlanStatus.RequiresReview) return true;
            return plan.Status == RemediationPlanStatus.DryRunCompleted
                && RemediationPlanIntegrity.HasAuditedApproval(plan)
                && plan.LastExecution is { Mode: RemediationExecutionMode.DryRun, Status: RemediationExecutionStatus.DryRunCompleted, MutatedCloud: false }
                && plan.Simulation is not null
                && plan.DryRunApprovedByUserId != identity.UserId;
        }
    }
    public bool CanRejectPlan => SelectedPlan is { Status: RemediationPlanStatus.RequiresReview or RemediationPlanStatus.Draft or RemediationPlanStatus.DryRunCompleted }
        && _authenticatedContext.Identity is { } reviewer
        && IsCurrentOrganizationPlan(SelectedPlan)
        && HasRemediationPermission()
        && SelectedPlan.CreatedByUserId is { } creator
        && creator != reviewer.UserId
        && !string.IsNullOrWhiteSpace(ReviewComment);
    public bool CanRunDryRun => SelectedPlan is { Status: RemediationPlanStatus.Approved }
        && RemediationPlanIntegrity.HasAuditedApproval(SelectedPlan)
        && IsCurrentOrganizationPlan(SelectedPlan) && HasRemediationPermission() && ContextHasEvidence(SelectedPlan);
    public bool CanVerifyPlan => SelectedPlan is { } verificationPlan
        && IsCurrentOrganizationPlan(verificationPlan) && HasRemediationPermission()
        && verificationPlan.LastExecution is { MutatedCloud: true, Status: RemediationExecutionStatus.Executed or RemediationExecutionStatus.VerificationFailed };
    public bool CanPrepareRollback => SelectedPlan is { Status: RemediationPlanStatus.DryRunCompleted or RemediationPlanStatus.Implemented or RemediationPlanStatus.VerificationFailed }
        && IsCurrentOrganizationPlan(SelectedPlan) && HasRemediationPermission()
        && SelectedPlan.RollbackChanges.Count > 0 && !string.IsNullOrWhiteSpace(ReviewComment);
    public string SimulationSummary => SelectedPlan?.Simulation is not { } simulation
        ? "No dry-run results yet."
        : $"{simulation.Status}; access impact: {simulation.AccessImpact}; affected: {simulation.AffectedEffectiveAccess.Count}; potential loss: {simulation.PotentiallyLostEffectiveAccess.Count}.";
    public string VerificationSummary => SelectedPlan?.Verification is not { } verification
        ? "Verification has not run. Dry runs do not change cloud access."
        : $"{verification.Status}: {verification.Details}";
    public string ApprovalButtonText => SelectedPlan?.Status == RemediationPlanStatus.DryRunCompleted ? "Approve for execution" : "Approve preview";
    public string PreviewFingerprint => SelectedPlan is null ? "No preview selected." : $"SHA-256 {RemediationPlanIntegrity.ComputeChangeSetFingerprint(SelectedPlan)}";
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
        if (!CanCreatePlan || SelectedRecommendation is null || _snapshot is null
            || _authenticatedContext.Identity is not { } identity || _authenticatedContext.OrganizationId is not { } organizationId) return;
        try
        {
            var context = BuildContext();
            var plan = _workflow.CreatePlan(SelectedRecommendation, context, identity.Email, identity.UserId, organizationId);
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
        if (!CanApprovePlan || SelectedPlan is null || _authenticatedContext.Identity is not { } identity) return;
        try
        {
            var isFinalApproval = SelectedPlan.Status == RemediationPlanStatus.DryRunCompleted;
            var plan = _workflow.Approve(SelectedPlan, identity.Email, ReviewComment, approverUserId: identity.UserId);
            await _planStore.SaveAsync(plan);
            StatusMessage = isFinalApproval
                ? "Final approval recorded for this dry-run fingerprint. Provider changes remain disabled in this build."
                : "Approval recorded locally. The next step is a non-mutating dry run.";
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
        if (!CanRejectPlan || SelectedPlan is null || _authenticatedContext.Identity is not { } identity) return;
        try
        {
            var plan = _workflow.Reject(SelectedPlan, identity.Email, ReviewComment, reviewerUserId: identity.UserId);
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
        if (!CanRunDryRun || SelectedPlan is null || _authenticatedContext.Identity is not { } identity) return;
        try
        {
            var plan = await _workflow.RunDryRunAsync(SelectedPlan, BuildContext(), identity.Email, requestedByUserId: identity.UserId);
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
        if (!CanVerifyPlan || SelectedPlan is null || _authenticatedContext.Identity is not { } identity) return;
        try
        {
            var plan = await _workflow.VerifyAsync(SelectedPlan, identity.Email, requestedByUserId: identity.UserId);
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
        if (!CanPrepareRollback || SelectedPlan is null || _authenticatedContext.Identity is not { } identity) return;
        try
        {
            var rollbackPlan = _workflow.PrepareRollback(SelectedPlan, identity.Email, ReviewComment, requestedByUserId: identity.UserId);
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

    private bool IsCurrentOrganizationPlan(RemediationPlan plan)
        => _authenticatedContext.OrganizationId is { } organizationId && plan.OrganizationId == organizationId;

    private bool HasRemediationPermission()
    {
        if (_authenticatedContext.Identity is not { } identity || _authenticatedContext.OrganizationId is not { } organizationId)
            return false;
        try
        {
            return _organizationService.Authorize(organizationId, identity, OrganizationAction.ManageRemediations).Allowed;
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
    }

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
        OnPropertyChanged(nameof(SignedInAs));
        OnPropertyChanged(nameof(CanCreatePlan));
        OnPropertyChanged(nameof(CanApprovePlan));
        OnPropertyChanged(nameof(CanRejectPlan));
        OnPropertyChanged(nameof(CanRunDryRun));
        OnPropertyChanged(nameof(CanVerifyPlan));
        OnPropertyChanged(nameof(CanPrepareRollback));
    }
}
