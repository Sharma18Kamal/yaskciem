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
using CloudEntitle.EffectiveAccess.Models;
using CloudEntitle.EffectiveAccess.Services;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private readonly ICloudAdministrationService? _cloudAdministration;
    private readonly IDialogService? _dialogs;
    private readonly IWhatIfSavedData? _whatIfSavedData;
    private readonly WhatIfPermissionSimulator? _whatIfSimulator;
    private DemoSnapshot? _snapshot;
    private IReadOnlyList<RecommendationModel> _recommendations = Array.Empty<RecommendationModel>();
    private IReadOnlyList<RemediationPlan> _plans = Array.Empty<RemediationPlan>();
    private RecommendationModel? _selectedRecommendation;
    private RemediationPlan? _selectedPlan;
    private string _searchText = string.Empty;
    private string _reviewComment = string.Empty;
    private string _providerProfile = string.Empty;
    private string _providerScope = string.Empty;
    private string _providerSubjectKind = string.Empty;
    private string _providerSubject = string.Empty;
    private string _providerRoleOrPolicy = string.Empty;
    private string _providerPolicyJson = "{}";
    private string _providerReason = string.Empty;
    private string _statusMessage = "Create a review plan from an evidence-backed recommendation.";
    private CloudAdministrationOperation _providerOperation;
    private bool _dependenciesReviewed;

    public RemediationViewModel(
        IDemoDataService demoData,
        IRecommendationStore recommendationStore,
        IRemediationPlanStore planStore,
        IRemediationPlanner planner,
        IRemediationSimulator simulator,
        IRemediationEngine engine,
        ILiveCloudScanService? liveScan,
        AuthenticatedOrganizationContext authenticatedContext,
        OrganizationManagementService organizationService,
        ICloudAdministrationService? cloudAdministration = null,
        IDialogService? dialogs = null,
        IWhatIfSavedData? whatIfSavedData = null,
        WhatIfPermissionSimulator? whatIfSimulator = null) : base(demoData)
    {
        _demoData = demoData;
        _recommendationStore = recommendationStore;
        _planStore = planStore;
        _workflow = new RemediationWorkflowService(planner, simulator, engine);
        _liveScan = liveScan;
        _authenticatedContext = authenticatedContext;
        _organizationService = organizationService;
        _cloudAdministration = cloudAdministration;
        _dialogs = dialogs;
        _whatIfSavedData = whatIfSavedData;
        _whatIfSimulator = whatIfSimulator;
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
        PrepareProviderPreviewCommand = new AsyncRelayCommand(PrepareProviderPreviewAsync);
        ApplyApprovedProviderPlanCommand = new AsyncRelayCommand(() => ExecuteProviderPlanAsync(rollback: false, reconcile: false));
        RollbackApprovedProviderPlanCommand = new AsyncRelayCommand(() => ExecuteProviderPlanAsync(rollback: true, reconcile: false));
        ReconcileProviderPlanCommand = new AsyncRelayCommand(() => ExecuteProviderPlanAsync(rollback: false, reconcile: true));
        _demoData.DataChanged += async (_, _) => await RefreshAsync();
        if (_liveScan is not null) _liveScan.ScanCompleted += async (_, _) => await RefreshAsync();
        _ = RefreshAsync();
    }

    public override string Title => "Remediation";
    public override string Subtitle => "Generate evidence-backed changes, simulate, approve independently, and execute only a fresh provider preview after explicit confirmation.";
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
            OnPropertyChanged(nameof(CanPrepareProviderPreview));
            OnPropertyChanged(nameof(CanApplyApprovedProviderPlan));
            OnPropertyChanged(nameof(CanRollbackApprovedProviderPlan));
            OnPropertyChanged(nameof(CanReconcileProviderPlan));
            OnPropertyChanged(nameof(ProviderOperations));
            OnPropertyChanged(nameof(ProviderExecutionSummary));
            OnPropertyChanged(nameof(WhatIfSummary));
            OnPropertyChanged(nameof(DependenciesReviewed));
            if (ProviderOperations.Count > 0 && !ProviderOperations.Contains(ProviderOperation))
                ProviderOperation = DefaultProviderOperation(ProviderOperations);
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
            if (plan.Status == RemediationPlanStatus.RequiresReview)
                return HasFreshExecutionEvidence(plan);
            return plan.Status == RemediationPlanStatus.DryRunCompleted
                && RemediationPlanIntegrity.HasAuditedApproval(plan)
                && plan.LastExecution is { Mode: RemediationExecutionMode.DryRun, Status: RemediationExecutionStatus.DryRunCompleted, MutatedCloud: false }
                && plan.Simulation is not null
                && HasFreshExecutionEvidence(plan)
                && plan.WhatIfReportFingerprint is not null
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
        && IsCurrentOrganizationPlan(SelectedPlan) && HasRemediationPermission() && ContextHasEvidence(SelectedPlan)
        && HasFreshExecutionEvidence(SelectedPlan);
    public bool CanVerifyPlan => SelectedPlan is { Status: RemediationPlanStatus.Implemented or RemediationPlanStatus.VerificationFailed } verificationPlan
        && verificationPlan.ProviderExecution is { Status: "Applied" or "Verified" or "VerificationFailed" }
        && IsCurrentOrganizationPlan(verificationPlan) && HasPolicyRemediationPermission()
        && IsExecutionActorAllowed(verificationPlan) && _cloudAdministration is not null;
    public bool CanPrepareRollback => SelectedPlan is { Status: RemediationPlanStatus.Implemented } rollbackSource
        && rollbackSource.LastExecution is { Status: RemediationExecutionStatus.Verified }
        && IsCurrentOrganizationPlan(rollbackSource) && HasPolicyRemediationPermission()
        && SelectedPlan.RollbackChanges.Count > 0 && SelectedPlan.ProviderExecution is { Status: "Applied" or "Verified" }
        && IsExecutionActorAllowed(rollbackSource) && !string.IsNullOrWhiteSpace(ReviewComment);
    public IReadOnlyList<CloudAdministrationOperation> ProviderOperations => SelectedPlan?.Provider switch
    {
        CloudProvider.Azure => new[] { CloudAdministrationOperation.AzureRemoveRole },
        CloudProvider.Aws => new[] { CloudAdministrationOperation.AwsDetachManagedPolicy, CloudAdministrationOperation.AwsDeleteInlinePolicy },
        CloudProvider.Gcp => new[] { CloudAdministrationOperation.GcpSetAllowPolicy },
        _ => Array.Empty<CloudAdministrationOperation>()
    };
    public CloudAdministrationOperation ProviderOperation
    {
        get => _providerOperation;
        set { if (SetProperty(ref _providerOperation, value)) OnPropertyChanged(nameof(CanPrepareProviderPreview)); }
    }
    public string ProviderProfile { get => _providerProfile; set { if (SetProperty(ref _providerProfile, value)) OnPropertyChanged(nameof(CanPrepareProviderPreview)); } }
    public string ProviderScope { get => _providerScope; set { if (SetProperty(ref _providerScope, value)) OnPropertyChanged(nameof(CanPrepareProviderPreview)); } }
    public string ProviderSubjectKind { get => _providerSubjectKind; set { if (SetProperty(ref _providerSubjectKind, value)) OnPropertyChanged(nameof(CanPrepareProviderPreview)); } }
    public string ProviderSubject { get => _providerSubject; set { if (SetProperty(ref _providerSubject, value)) OnPropertyChanged(nameof(CanPrepareProviderPreview)); } }
    public string ProviderRoleOrPolicy { get => _providerRoleOrPolicy; set { if (SetProperty(ref _providerRoleOrPolicy, value)) OnPropertyChanged(nameof(CanPrepareProviderPreview)); } }
    public string ProviderPolicyJson { get => _providerPolicyJson; set { if (SetProperty(ref _providerPolicyJson, value)) OnPropertyChanged(nameof(CanPrepareProviderPreview)); } }
    public string ProviderReason { get => _providerReason; set { if (SetProperty(ref _providerReason, value)) OnPropertyChanged(nameof(CanPrepareProviderPreview)); } }
    public bool DependenciesReviewed { get => _dependenciesReviewed; set { if (SetProperty(ref _dependenciesReviewed, value)) NotifyActions(); } }
    public bool CanPrepareProviderPreview => SelectedPlan is { Status: RemediationPlanStatus.Draft or RemediationPlanStatus.RequiresReview or RemediationPlanStatus.ExecutionFailed }
        && SelectedPlan.Recommendation.EvaluationState == RiskEvaluationState.Confirmed
        && SelectedPlan.Recommendation.RecommendationType == RecommendationType.RemoveUnusedPermission
        && SelectedPlan.IsRollbackDraft != true
        && SelectedPlan.WhatIfReportFingerprint is not null
        && HasFreshWhatIfSnapshot(SelectedPlan)
        && IsCurrentOrganizationPlan(SelectedPlan) && HasPolicyRemediationPermission()
        && _cloudAdministration is not null
        && ProviderOperations.Contains(ProviderOperation)
        && IsRemovalOperation(SelectedPlan.Provider, ProviderOperation)
        && !string.IsNullOrWhiteSpace(ProviderReason);
    public bool CanApplyApprovedProviderPlan => IsApprovedLivePlan(SelectedPlan)
        && SelectedPlan?.IsRollbackDraft != true
        && SelectedPlan?.ProviderExecution?.Status == "Prepared"
        && IsExecutionActorAllowed(SelectedPlan)
        && DependenciesReviewed && _dialogs is not null && _cloudAdministration is not null;
    public bool CanRollbackApprovedProviderPlan => IsApprovedLivePlan(SelectedPlan)
        && SelectedPlan?.IsRollbackDraft == true
        && SelectedPlan?.ProviderExecution?.Status is "Applied" or "Verified"
        && IsExecutionActorAllowed(SelectedPlan)
        && DependenciesReviewed && _dialogs is not null && _cloudAdministration is not null;
    public bool CanReconcileProviderPlan => SelectedPlan?.Status == RemediationPlanStatus.OutcomeUnknown
        && SelectedPlan.ProviderExecution is not null
        && IsCurrentOrganizationPlan(SelectedPlan) && HasPolicyRemediationPermission()
        && IsExecutionActorAllowed(SelectedPlan)
        && _cloudAdministration is not null && _authenticatedContext.Identity is not null;
    public string SimulationSummary => SelectedPlan?.Simulation is not { } simulation
        ? "No dry-run results yet."
        : $"{simulation.Status}; access impact: {simulation.AccessImpact}; affected: {simulation.AffectedEffectiveAccess.Count}; potential loss: {simulation.PotentiallyLostEffectiveAccess.Count}.";
    public string VerificationSummary => SelectedPlan?.Verification is not { } verification
        ? "Verification has not run. Dry runs do not change cloud access."
        : $"{verification.Status}: {verification.Details}";
    public string ProviderExecutionSummary => SelectedPlan?.ProviderExecution is not { } provider
        ? "No provider policy preview is attached. Live execution is unavailable."
        : $"{provider.Status}; {provider.Operation}; account {provider.AccountId}; identity {provider.CloudIdentity}; previewed {provider.PreparedAtUtc:u}; request SHA-256 {provider.RequestFingerprint}.";
    public string WhatIfSummary => SelectedPlan?.WhatIfReportFingerprint is not { } fingerprint
        ? "No What-if permission simulation is bound to this plan."
        : $"Milestone 17 report SHA-256 {fingerprint}; evaluated {SelectedPlan.WhatIfEvaluatedAtUtc:u}; source snapshot {SelectedPlan.WhatIfSourceSnapshotAtUtc:u}.";
    public string ApprovalButtonText => SelectedPlan?.Status == RemediationPlanStatus.DryRunCompleted ? "Approve for execution" : "Approve preview";
    public string PreviewFingerprint => SelectedPlan is null ? "No preview selected." : $"SHA-256 {RemediationPlanIntegrity.ComputeChangeSetFingerprint(SelectedPlan)}";
    public AsyncRelayCommand CreatePlanCommand { get; }
    public AsyncRelayCommand ApprovePlanCommand { get; }
    public AsyncRelayCommand RejectPlanCommand { get; }
    public AsyncRelayCommand RunDryRunCommand { get; }
    public AsyncRelayCommand VerifyPlanCommand { get; }
    public AsyncRelayCommand PrepareRollbackCommand { get; }
    public AsyncRelayCommand PrepareProviderPreviewCommand { get; }
    public AsyncRelayCommand ApplyApprovedProviderPlanCommand { get; }
    public AsyncRelayCommand RollbackApprovedProviderPlanCommand { get; }
    public AsyncRelayCommand ReconcileProviderPlanCommand { get; }

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
            plan = await TryAttachWhatIfEvidenceAsync(plan, identity);
            await _planStore.SaveAsync(plan);
            StatusMessage = plan.WhatIfReportFingerprint is not null
                ? "Review plan created with a bound Milestone 17 What-if report. Prepare the exact provider before/after preview before approval."
                : plan.Status == RemediationPlanStatus.RequiresReview
                    ? "Review-only plan created. This recommendation type or saved permission evidence cannot be safely correlated to the What-if simulator; provider execution remains disabled."
                    : "Draft created with incomplete recommendation evidence; resolve the evidence gap before approval.";
            await RefreshAsync();
            SelectedPlan = Plans.FirstOrDefault(item => item.Id == plan.Id);
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to create remediation plan: {exception.Message}";
        }
    }

    private async Task<RemediationPlan> TryAttachWhatIfEvidenceAsync(RemediationPlan plan, OrganizationIdentity actor)
    {
        if (plan.Recommendation.RecommendationType != RecommendationType.RemoveUnusedPermission
            || _whatIfSavedData is null || _whatIfSimulator is null
            || !plan.Recommendation.EntitlementId.HasValue)
            return plan;

        var input = await _whatIfSavedData.LoadAsync(plan.Recommendation.CloudAccountId, CancellationToken.None);
        if (input.SourceSnapshotAtUtc is not { } snapshotAt
            || snapshotAt > DateTime.UtcNow
            || DateTime.UtcNow - snapshotAt > TimeSpan.FromMinutes(15))
            throw new InvalidOperationException("The saved authorization inventory has no recent provider collection timestamp. Run a fresh inventory scan before preparing remediation.");
        var entitlement = input.Evaluation.Normalized.Entitlements.SingleOrDefault(item =>
            item.Id == plan.Recommendation.EntitlementId
            && item.PrincipalId == plan.Recommendation.PrincipalId);
        if (entitlement is null)
            throw new InvalidOperationException("The saved authorization snapshot does not contain the recommendation's exact entitlement.");
        var permission = input.Evaluation.Normalized.Permissions.SingleOrDefault(item => item.Id == entitlement.PermissionId);
        if (permission is null || string.IsNullOrWhiteSpace(permission.Action)
            || permission.Action.Contains('*') || permission.Action.Contains('?'))
            throw new InvalidOperationException("The saved entitlement does not map to one exact provider permission; no What-if report was generated.");

        var targetKind = entitlement.RoleId.HasValue ? WhatIfTargetKind.Role
            : entitlement.PolicyId.HasValue ? WhatIfTargetKind.Policy
            : WhatIfTargetKind.Identity;
        var targetId = entitlement.RoleId ?? entitlement.PolicyId ?? entitlement.PrincipalId;
        if (targetId == Guid.Empty)
            throw new InvalidOperationException("The entitlement has no exact role, policy or identity target.");
        var resourceScope = entitlement.ResourceId.HasValue
            ? input.Evaluation.Resources.SingleOrDefault(item => item.Id == entitlement.ResourceId)?.ResourceId
                ?? input.Evaluation.Resources.SingleOrDefault(item => item.Id == entitlement.ResourceId)?.ResourceUri
            : null;
        var change = new WhatIfChange(plan.Provider, plan.Recommendation.CloudAccountId,
            targetKind, targetId, permission.Action)
        {
            Operation = WhatIfOperation.Remove,
            ResourceScope = resourceScope
        };
        var report = await _whatIfSimulator.SimulateAsync(input, change);
        return AttachWhatIfReport(plan, report, actor);
    }

    private RemediationPlan AttachWhatIfReport(RemediationPlan plan, WhatIfReport report, OrganizationIdentity actor)
    {
        if (report.Change.Provider != plan.Provider || report.Change.AccountId != plan.Recommendation.CloudAccountId)
            throw new InvalidOperationException("The What-if result does not match the recommendation's provider and account.");
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });
        return _workflow.AttachWhatIfEvidence(plan, json, Hash(json), report.EvaluatedAtUtc,
            actor.Email, actor.UserId, report.SourceSnapshotAtUtc);
    }

    public async Task PrepareProviderPreviewAsync()
    {
        if (!CanPrepareProviderPreview || SelectedPlan is not { } selected || _cloudAdministration is null
            || _authenticatedContext.Identity is not { } identity
            || _authenticatedContext.OrganizationId is not { } organizationId)
            return;

        var binding = _organizationService.GetTenantBindings(organizationId, identity)
            .SingleOrDefault(item => item.CloudAccountId == selected.Recommendation.CloudAccountId
                && item.Provider == selected.Provider);
        if (binding is null)
        {
            StatusMessage = "The recommendation account is not bound to this organization; no provider request was made.";
            return;
        }

        try
        {
            var request = new CloudAdministrationRequest(
                selected.Provider,
                binding.ExternalTenantId,
                ProviderProfile.Trim(),
                ProviderOperation,
                ProviderScope.Trim(),
                ProviderSubjectKind.Trim(),
                ProviderSubject.Trim(),
                ProviderRoleOrPolicy.Trim(),
                ProviderPolicyJson,
                string.IsNullOrWhiteSpace(ProviderReason) ? selected.Recommendation.Reason : ProviderReason.Trim());
            var organization = _authenticatedContext.OrganizationId;
            var actor = _authenticatedContext.Identity;
            var preview = await _cloudAdministration.PreviewAsync(request);
            if (SelectedPlan?.Id != selected.Id || _authenticatedContext.OrganizationId != organization
                || _authenticatedContext.Identity != actor)
            {
                StatusMessage = "Context changed while preparing the provider preview; it was not attached to the remediation plan.";
                return;
            }

            var requestJson = JsonSerializer.Serialize(new
            {
                request.Provider,
                request.AccountId,
                request.Profile,
                request.Operation,
                request.Scope,
                request.SubjectKind,
                request.Subject,
                request.RoleOrPolicy,
                request.PolicyJson,
                request.Reason
            });
            var fingerprint = Hash($"{requestJson}\n{preview.BeforeJson}\n{preview.AfterJson}\n{preview.CloudIdentity.Identity}");
            var providerLink = new RemediationProviderExecutionLink(
                preview.Id,
                request.AccountId,
                request.Operation.ToString(),
                preview.CloudIdentity.Identity,
                preview.PreparedAtUtc,
                preview.BeforeJson,
                preview.AfterJson,
                fingerprint,
                preview.Status,
                identity.UserId);
            var plan = _workflow.AttachProviderPreview(selected, providerLink, identity.Email, identity.UserId);
            await _planStore.SaveAsync(plan);
            StatusMessage = $"Provider state preview prepared ({preview.Id:D}). No policy was changed. Review the exact before/after state and run the approved dry run.";
            await RefreshAsync();
            SelectedPlan = Plans.FirstOrDefault(item => item.Id == plan.Id);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or UnauthorizedAccessException or IOException or System.Data.Common.DbException)
        {
            StatusMessage = $"Provider preview failed; no write was attempted: {exception.Message}";
        }
    }

    public async Task ExecuteProviderPlanAsync(bool rollback, bool reconcile)
    {
        var plan = SelectedPlan;
        var link = plan?.ProviderExecution;
        if (plan is null || link is null || _cloudAdministration is null
            || _authenticatedContext.Identity is not { } identity
            || !IsCurrentOrganizationPlan(plan) || !HasPolicyRemediationPermission())
        {
            StatusMessage = "A current organization plan, bound provider preview, and remediation/policy permissions are required.";
            return;
        }
        if (reconcile)
        {
            if (!CanReconcileProviderPlan) return;
        }
        else if (rollback ? !CanRollbackApprovedProviderPlan : !CanApplyApprovedProviderPlan)
        {
            StatusMessage = "Execution is blocked. Require independent final approval, the plan creator's cloud session, a current provider preview, and explicit dependency/break-glass review.";
            return;
        }

        if (!reconcile)
        {
            var verb = rollback ? "ROLL BACK" : "APPLY";
            if (!DependenciesReviewed || _dialogs is null
                || !_dialogs.Confirm(
                    $"{verb} the reviewed {plan.Provider} remediation for {link.AccountId}?\n"
                    + $"Provider identity: {link.CloudIdentity}\nProvider plan: {link.ProviderPlanId:D}\n"
                    + $"Approval fingerprint: {plan.ApprovedChangeSetFingerprint}\n"
                    + "This is a REAL cloud write. The provider service will revalidate account identity and state immediately before writing.",
                    "Confirm approved remediation"))
            {
                StatusMessage = "Write cancelled or dependency review is incomplete; no provider change was requested.";
                return;
            }
        }

        var started = DateTime.UtcNow;
        CloudAdministrationPlan providerPlan;
        try
        {
            providerPlan = reconcile
                ? await _cloudAdministration.ReconcileAsync(link.ProviderPlanId)
                : rollback
                    ? await _cloudAdministration.RollbackAsync(link.ProviderPlanId, dependenciesReviewed: true)
                    : await _cloudAdministration.ApplyAsync(link.ProviderPlanId, dependenciesReviewed: true);
        }
        catch (CloudAdministrationException exception)
        {
            var unknown = exception.Plan.Status is "ApplyUnknown" or "RollbackUnknown";
            var execution = new RemediationExecution(
                Guid.NewGuid(), plan.Id, plan.Provider, RemediationExecutionMode.Live,
                unknown ? RemediationExecutionStatus.OutcomeUnknown : RemediationExecutionStatus.Failed,
                MutatedCloud: false,
                new[] { exception.Message, exception.Plan.Notice ?? exception.Plan.Status },
                started, DateTime.UtcNow)
            {
                MutationOutcomeKnown = !unknown
            };
            var updated = _workflow.RecordProviderExecution(plan, execution, identity.Email,
                exception.Plan.Status, exception.Plan.Notice ?? exception.Message,
                requestedByUserId: identity.UserId);
            await SaveExecutionOutcomeAsync(updated);
            StatusMessage = unknown
                ? "Provider write outcome is unknown. Use read-only reconciliation; do not retry or rollback until reconciled."
                : $"Provider write failed before a confirmed mutation: {exception.Message}";
            return;
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException
            or IOException or System.Data.Common.DbException)
        {
            var execution = new RemediationExecution(
                Guid.NewGuid(), plan.Id, plan.Provider, RemediationExecutionMode.Live,
                RemediationExecutionStatus.Failed, false, new[] { exception.Message }, started, DateTime.UtcNow);
            var failed = _workflow.RecordProviderExecution(plan, execution, identity.Email, "Rejected",
                exception.Message, requestedByUserId: identity.UserId);
            await SaveExecutionOutcomeAsync(failed);
            StatusMessage = $"Provider operation was rejected; no write was confirmed. Prepare a fresh preview and reapprove before retrying: {exception.Message}";
            return;
        }
        finally
        {
            DependenciesReviewed = false;
        }

        var successfulExecution = CreateExecutionRecord(plan, started, providerPlan.Status, reconcile, rollback, providerPlan.Notice);
        var completed = _workflow.RecordProviderExecution(plan, successfulExecution, identity.Email, providerPlan.Status,
            providerPlan.Notice ?? providerPlan.Status, requestedByUserId: identity.UserId);
        await SaveExecutionOutcomeAsync(completed);
        StatusMessage = $"{providerPlan.Status}: {providerPlan.Notice}";
    }

    private async Task SaveExecutionOutcomeAsync(RemediationPlan plan)
    {
        try
        {
            await _planStore.SaveAsync(plan);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.Data.Common.DbException)
        {
            _plans = _plans.Where(item => item.Id != plan.Id).Append(plan).ToArray();
            SelectedPlan = plan;
            NotifyCollections();
            ErrorMessage = $"Provider outcome is recorded in memory but remediation persistence failed: {exception.Message}. Do not retry; reconcile provider state before taking further action.";
            return;
        }

        await RefreshAsync();
        SelectedPlan = Plans.FirstOrDefault(item => item.Id == plan.Id);
    }

    private static RemediationExecution CreateExecutionRecord(
        RemediationPlan plan,
        DateTime started,
        string providerStatus,
        bool reconcile,
        bool rollback,
        string? notice)
    {
        var isApplied = providerStatus is "Applied" or "Verified";
        var rolledBack = providerStatus == "RolledBack";
        var unknown = providerStatus.EndsWith("Unknown", StringComparison.Ordinal);
        var executionStatus = unknown ? RemediationExecutionStatus.OutcomeUnknown
            : rolledBack ? RemediationExecutionStatus.RolledBack
            : isApplied ? RemediationExecutionStatus.Verified
            : providerStatus == "VerificationFailed" ? RemediationExecutionStatus.VerificationFailed
            : reconcile ? RemediationExecutionStatus.Reconciled
            : RemediationExecutionStatus.Failed;
        var verified = isApplied || rolledBack;
        return new RemediationExecution(
            Guid.NewGuid(), plan.Id, plan.Provider, RemediationExecutionMode.Live, executionStatus,
            MutatedCloud: isApplied || rolledBack,
            new[] { notice ?? providerStatus }, started, DateTime.UtcNow)
        {
            MutationOutcomeKnown = !unknown,
            Verification = verified
                ? new RemediationVerification(RemediationVerificationStatus.Verified, notice ?? providerStatus, DateTime.UtcNow)
                : providerStatus == "VerificationFailed"
                    ? new RemediationVerification(RemediationVerificationStatus.Failed, notice ?? providerStatus, DateTime.UtcNow)
                : unknown ? new RemediationVerification(RemediationVerificationStatus.Unavailable, notice ?? "Outcome requires reconciliation.", DateTime.UtcNow)
                : null
        };
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private bool IsApprovedLivePlan(RemediationPlan? plan)
        => plan is { Status: RemediationPlanStatus.Approved, ProviderExecution: not null }
            && RemediationPlanIntegrity.HasAuditedLiveApproval(plan)
            && plan.Simulation is not null
            && plan.WhatIfReportFingerprint is not null
            && plan.WhatIfEvaluatedAtUtc.HasValue
            && DateTime.UtcNow - plan.WhatIfEvaluatedAtUtc.Value <= TimeSpan.FromMinutes(15)
            && HasFreshExecutionEvidence(plan)
            && IsCurrentOrganizationPlan(plan)
            && HasPolicyRemediationPermission()
            && plan.ApprovedByUserId != _authenticatedContext.Identity?.UserId;

    private bool IsExecutionActorAllowed(RemediationPlan? plan)
        => plan?.ProviderExecution is { PreparedByUserId: var preparedBy }
            && _authenticatedContext.Identity is { } identity
            && identity.UserId == preparedBy;

    private bool HasPolicyRemediationPermission()
    {
        if (!HasRemediationPermission() || _authenticatedContext.Identity is not { } identity
            || _authenticatedContext.OrganizationId is not { } organization) return false;
        try { return _organizationService.Authorize(organization, identity, OrganizationAction.ManagePolicies).Allowed; }
        catch (KeyNotFoundException) { return false; }
    }

    private static CloudAdministrationOperation DefaultProviderOperation(IReadOnlyList<CloudAdministrationOperation> operations)
        => operations.FirstOrDefault();

    private static bool IsRemovalOperation(CloudProvider provider, CloudAdministrationOperation operation)
        => provider switch
        {
            CloudProvider.Aws => operation is CloudAdministrationOperation.AwsDetachManagedPolicy or CloudAdministrationOperation.AwsDeleteInlinePolicy,
            CloudProvider.Azure => operation is CloudAdministrationOperation.AzureRemoveRole,
            CloudProvider.Gcp => operation is CloudAdministrationOperation.GcpSetAllowPolicy,
            _ => false
        };

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
            var selected = SelectedPlan;
            var link = selected.ProviderExecution
                ?? throw new InvalidOperationException("The selected remediation has no provider plan to verify.");
            var started = DateTime.UtcNow;
            var providerPlan = await _cloudAdministration!.VerifyAsync(link.ProviderPlanId);
            var execution = CreateExecutionRecord(selected, started, providerPlan.Status, reconcile: false, rollback: false, providerPlan.Notice);
            var updated = _workflow.RecordProviderExecution(selected, execution, identity.Email, providerPlan.Status,
                providerPlan.Notice ?? providerPlan.Status, requestedByUserId: identity.UserId);
            await SaveExecutionOutcomeAsync(updated);
            StatusMessage = providerPlan.Notice ?? $"Provider verification returned {providerPlan.Status}.";
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
            rollbackPlan = await TryAttachRollbackWhatIfEvidenceAsync(rollbackPlan, SelectedPlan, identity);
            await _planStore.SaveAsync(rollbackPlan);
            StatusMessage = "Rollback draft created with an inverse What-if simulation and the original provider preview reference. It requires separate review, dry run and independent final approval.";
            await RefreshAsync();
            SelectedPlan = Plans.FirstOrDefault(item => item.Id == rollbackPlan.Id);
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to prepare rollback draft: {exception.Message}";
        }
    }

    private async Task<RemediationPlan> TryAttachRollbackWhatIfEvidenceAsync(
        RemediationPlan rollbackPlan,
        RemediationPlan appliedPlan,
        OrganizationIdentity actor)
    {
        if (_whatIfSavedData is null || _whatIfSimulator is null || string.IsNullOrWhiteSpace(appliedPlan.WhatIfReportJson))
            throw new InvalidOperationException("Rollback requires the original What-if report and simulator input.");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter());
        var original = JsonSerializer.Deserialize<WhatIfReport>(appliedPlan.WhatIfReportJson, options)
            ?? throw new InvalidOperationException("The original What-if report could not be read.");
        var input = await _whatIfSavedData.LoadAsync(appliedPlan.Recommendation.CloudAccountId, CancellationToken.None);
        if (input.SourceSnapshotAtUtc is not { } snapshotAt
        || appliedPlan.LastExecution is not { Status: RemediationExecutionStatus.Verified } appliedExecution
        || snapshotAt <= appliedExecution.CompletedAt
        || DateTime.UtcNow - snapshotAt > TimeSpan.FromMinutes(15))
        throw new InvalidOperationException("Rollback requires a fresh post-change provider inventory collected after the verified application. Run a new inventory scan first.");
        var inverse = original.Change with
        {
            Operation = WhatIfOperation.Add,
            ReplacementAction = null
        };
        var report = await _whatIfSimulator.SimulateAsync(input, inverse);
        return AttachWhatIfReport(rollbackPlan, report, actor);
    }

    private bool ContextHasEvidence(RemediationPlan plan)
        => plan.Recommendation.EffectiveAccessId.HasValue && _snapshot?.EffectiveAccess.Any(item => item.Id == plan.Recommendation.EffectiveAccessId.Value) == true;

    private static bool HasFreshWhatIfSnapshot(RemediationPlan plan)
        => plan.WhatIfSourceSnapshotAtUtc is { } snapshotAt
            && snapshotAt <= DateTime.UtcNow
            && DateTime.UtcNow - snapshotAt <= TimeSpan.FromMinutes(15);

    private static bool HasFreshProviderPreview(RemediationPlan plan)
        => plan.ProviderExecution is { PreparedAtUtc: var preparedAt }
            && plan.WhatIfEvaluatedAtUtc is { } evaluatedAt
            && preparedAt <= DateTimeOffset.UtcNow
            && DateTimeOffset.UtcNow - preparedAt <= TimeSpan.FromMinutes(15)
            && preparedAt.UtcDateTime >= DateTime.SpecifyKind(evaluatedAt, DateTimeKind.Utc)
            && HasFreshWhatIfSnapshot(plan);

    private static bool HasFreshRollbackWhatIf(RemediationPlan plan)
        => plan.IsRollbackDraft
            && plan.ProviderExecution is { Status: "Applied" or "Verified" }
            && plan.RollbackPreparedAfterUtc is { } appliedAt
            && plan.WhatIfEvaluatedAtUtc is { } evaluatedAt
            && plan.WhatIfSourceSnapshotAtUtc is { } snapshotAt
            && snapshotAt > appliedAt
            && snapshotAt <= DateTime.UtcNow
            && DateTime.UtcNow - snapshotAt <= TimeSpan.FromMinutes(15)
            && evaluatedAt >= snapshotAt
            && DateTime.UtcNow - evaluatedAt <= TimeSpan.FromMinutes(15);

    private static bool HasFreshExecutionEvidence(RemediationPlan plan)
        => plan.IsRollbackDraft ? HasFreshRollbackWhatIf(plan) : HasFreshProviderPreview(plan);

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
        OnPropertyChanged(nameof(CanPrepareProviderPreview));
        OnPropertyChanged(nameof(CanApplyApprovedProviderPlan));
        OnPropertyChanged(nameof(CanRollbackApprovedProviderPlan));
        OnPropertyChanged(nameof(CanReconcileProviderPlan));
        OnPropertyChanged(nameof(ProviderExecutionSummary));
        OnPropertyChanged(nameof(WhatIfSummary));
        OnPropertyChanged(nameof(DependenciesReviewed));
    }
}
