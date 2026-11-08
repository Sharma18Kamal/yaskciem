using CloudEntitle.Desktop.Services;
using CloudEntitle.OrganizationManagement.Models;

namespace CloudEntitle.Desktop.ViewModels;

public sealed partial class OrganizationViewModel
{
    private readonly ICloudAdministrationService? _administration;
    private readonly IDialogService? _administrationDialogs;
    private TenantBinding? _administrationBinding;
    private CloudAdministrationOperation _administrationOperation;
    private string _adminProfile = "", _adminScope = "", _adminSubject = "", _adminSubjectKind = "",
        _adminRole = "", _adminPolicy = "{}", _adminReason = "";
    private bool _adminWriteMode, _adminReview, _adminBusy;
    private long _adminRevision;
    private CloudAdministrationPlan? _adminPlan;
    private string _adminStatus = "Read-only by default. Enable administrator write mode only for an explicitly reviewed change.";
    private AsyncRelayCommand? _adminPreviewCommand, _adminApplyCommand, _adminRollbackCommand, _adminReconcileCommand;
    private IReadOnlyList<CloudAdministrationPlan> _adminPlans = Array.Empty<CloudAdministrationPlan>();

    public AsyncRelayCommand AdminPreviewCommand => _adminPreviewCommand ??= new(PreviewAdministrationAsync);
    public AsyncRelayCommand AdminApplyCommand => _adminApplyCommand ??= new(() => ExecuteAdministrationAsync(false, false));
    public AsyncRelayCommand AdminRollbackCommand => _adminRollbackCommand ??= new(() => ExecuteAdministrationAsync(true, false));
    public AsyncRelayCommand AdminReconcileCommand => _adminReconcileCommand ??= new(() => ExecuteAdministrationAsync(false, true));
    public IReadOnlyList<CloudAdministrationOperation> AdministrationOperations => SelectedAdministrationBinding?.Provider switch
    {
        Domain.CloudProvider.Azure => new[] { CloudAdministrationOperation.AzureAssignRole, CloudAdministrationOperation.AzureRemoveRole, CloudAdministrationOperation.AzureUpdateCustomRole },
        Domain.CloudProvider.Aws => new[] { CloudAdministrationOperation.AwsAttachManagedPolicy, CloudAdministrationOperation.AwsDetachManagedPolicy, CloudAdministrationOperation.AwsPutInlinePolicy, CloudAdministrationOperation.AwsDeleteInlinePolicy },
        Domain.CloudProvider.Gcp => new[] { CloudAdministrationOperation.GcpSetAllowPolicy, CloudAdministrationOperation.GcpPutDenyPolicy, CloudAdministrationOperation.GcpDeleteDenyPolicy },
        _ => Array.Empty<CloudAdministrationOperation>()
    };
    public TenantBinding? SelectedAdministrationBinding
    {
        get => _administrationBinding;
        set
        {
            if (!SetProperty(ref _administrationBinding, value)) return;
            InvalidateAdministration();
            OnPropertyChanged(nameof(AdministrationOperations));
            if (AdministrationOperations.Count > 0) AdministrationOperation = AdministrationOperations[0];
            OnPropertyChanged(nameof(AdministrationInstructions));
        }
    }
    public CloudAdministrationOperation AdministrationOperation
    {
        get => _administrationOperation;
        set { if (SetProperty(ref _administrationOperation, value)) { InvalidateAdministration(); OnPropertyChanged(nameof(AdministrationInstructions)); } }
    }
    public bool AdministratorWriteMode
    {
        get => _adminWriteMode;
        set { if (SetProperty(ref _adminWriteMode, value)) InvalidateAdministration(); }
    }
    public bool DependenciesReviewed { get => _adminReview; set => SetProperty(ref _adminReview, value); }
    public bool AdministrationBusy { get => _adminBusy; private set => SetProperty(ref _adminBusy, value); }
    public bool CanAdministerCloud => AdministratorWriteMode && _administration is not null && _identity is not null
        && SelectedOrganization is not null && SelectedAdministrationBinding is not null
        && IsAllowed(OrganizationAction.ManagePolicies) && IsAllowed(OrganizationAction.ManageRemediations);
    public string AdminProfile { get => _adminProfile; set { if (SetProperty(ref _adminProfile, value)) InvalidateAdministration(); } }
    public string AdminScope { get => _adminScope; set { if (SetProperty(ref _adminScope, value)) InvalidateAdministration(); } }
    public string AdminSubject { get => _adminSubject; set { if (SetProperty(ref _adminSubject, value)) InvalidateAdministration(); } }
    public string AdminSubjectKind { get => _adminSubjectKind; set { if (SetProperty(ref _adminSubjectKind, value)) InvalidateAdministration(); } }
    public string AdminRoleOrPolicy { get => _adminRole; set { if (SetProperty(ref _adminRole, value)) InvalidateAdministration(); } }
    public string AdminPolicyJson { get => _adminPolicy; set { if (SetProperty(ref _adminPolicy, value)) InvalidateAdministration(); } }
    public string AdminReason { get => _adminReason; set { if (SetProperty(ref _adminReason, value)) InvalidateAdministration(); } }
    public string AdministrationStatus { get => _adminStatus; private set => SetProperty(ref _adminStatus, value); }
    public IReadOnlyList<CloudAdministrationPlan> AdministrationPlans { get => _adminPlans; private set => SetProperty(ref _adminPlans, value); }
    public CloudAdministrationPlan? SelectedAdministrationPlan
    {
        get => _adminPlan;
        set
        {
            if (!SetProperty(ref _adminPlan, value)) return;
            DependenciesReviewed = false;
            OnPropertyChanged(nameof(AdministrationBeforeJson));
            OnPropertyChanged(nameof(AdministrationAfterJson));
        }
    }
    public string AdministrationBeforeJson => SelectedAdministrationPlan?.BeforeJson ?? "";
    public string AdministrationAfterJson => SelectedAdministrationPlan?.AfterJson ?? "";
    public string AdministrationInstructions => SelectedAdministrationBinding?.Provider switch
    {
        Domain.CloudProvider.Azure => "Azure: scope must be within the bound subscription. Subject = Entra user/group/service-principal object GUID (managed identity for a device/service). Target kind/ID = role-assignment GUID; Role/policy = role definition GUID. Assign creates an exact assignment, Remove deletes that exact assignment. Custom-role update takes ARM properties with roleName, permissions and assignableScopes. Built-in roles are not editable. actions/dataActions allow; notActions/notDataActions EXCLUDE, they are NOT explicit deny. Arbitrary customer deny assignments are not generally available. Never use device object IDs as service principals.",
        Domain.CloudProvider.Aws => "AWS: Subject = IAM user, role or group NAME; Target kind/ID = user, role or group. Role/policy = managed policy ARN for attach/detach, or inline policy name for put/delete. Inline JSON supports explicit Effect Allow/Deny and conditions. Managed-policy attachment grants a policy, not a role-assumption relationship. A device/service normally uses an IAM role; edit its permissions here. Trust policies and credential creation are not supported. Existing identity permissions boundaries/SCPs still apply.",
        Domain.CloudProvider.Gcp => "GCP: SetAllowPolicy replaces the PROJECT IAM policy JSON (bindings, conditions, auditConfigs); preserve unrelated bindings and audit logging. Membership uses user:, group:, serviceAccount: or supported federated principals, not a device ID. Deny operations use Role/policy = deny policy ID and JSON rules/denyRule; etags protect provider versions. Deny permissions use the IAM v2 fully qualified format, not allow-policy action names. Please review break-glass exceptions and propagation. The selected gcloud identity must have setIamPolicy or deny-policy write permissions.",
        _ => "Select a bound account. Prepare the desktop CLI identity with only approved write permissions on that scope. No endpoint, role or permission is created automatically."
    };

    private void InvalidateAdministration()
    {
        _adminRevision++;
        SelectedAdministrationPlan = null;
        DependenciesReviewed = false;
        OnPropertyChanged(nameof(CanAdministerCloud));
    }
    private void ResetAdministration()
    {
        AdministratorWriteMode = false;
        SelectedAdministrationBinding = null;
        SelectedAdministrationPlan = null;
        AdministrationPlans = Array.Empty<CloudAdministrationPlan>();
        AdministrationStatus = "Read-only mode. Organization changes/sign-out disable writes and clear displayed plans.";
    }

    public async Task PreviewAdministrationAsync()
    {
        if (!CanAdministerCloud || _administration is null || SelectedAdministrationBinding is null)
        { AdministrationStatus = "Enable administrator write mode, select a bound account and obtain ManagePolicies/ManageRemediations access."; return; }
        if (AdministrationBusy) { AdministrationStatus = "An administration operation is already running."; return; }
        var organization = SelectedOrganization;
        var identity = _identity;
        var binding = SelectedAdministrationBinding;
        var revision = _adminRevision;
        AdministrationBusy = true;
        try
        {
            var plan = await _administration.PreviewAsync(new(binding.Provider, binding.ExternalTenantId, AdminProfile.Trim(),
                AdministrationOperation, AdminScope.Trim(), AdminSubjectKind.Trim(), AdminSubject.Trim(), AdminRoleOrPolicy.Trim(),
                AdminPolicyJson, AdminReason.Trim()));
            if (SelectedOrganization != organization || _identity != identity || SelectedAdministrationBinding != binding || _adminRevision != revision) return;
            RecordAdministrationPlan(plan);
            AdministrationStatus = $"Preview {plan.Id:D}: {plan.Status}. Cloud identity: {plan.CloudIdentity.Identity}. Review the exact before/after JSON; no write was made. {plan.Notice}";
        }
        catch (Exception exception)
        {
            if (SelectedOrganization == organization && _identity == identity)
                AdministrationStatus = $"Administration preview failed: {exception.Message}";
        }
        finally { AdministrationBusy = false; }
    }

    private async Task ExecuteAdministrationAsync(bool rollback, bool reconcile)
    {
        if (!CanAdministerCloud || _administration is null || SelectedAdministrationPlan is null)
        { AdministrationStatus = "Enable administrator write mode and select a prepared plan for this organization."; return; }
        if (AdministrationBusy) { AdministrationStatus = "An administration operation is already running."; return; }
        var plan = SelectedAdministrationPlan;
        if (!reconcile && (!DependenciesReviewed || _administrationDialogs is null))
        { AdministrationStatus = "Review operational dependencies and break-glass access before approval; a confirmation dialog is required."; return; }
        if (!reconcile && !_administrationDialogs!.Confirm(
            $"{(rollback ? "ROLL BACK" : "APPROVE AND APPLY")} cloud permissions for {plan.Request.Provider}/{plan.Request.AccountId}?\n"
            + $"Operation: {plan.Request.Operation}\nCloud identity: {plan.CloudIdentity.Identity}\nPlan: {plan.Id:D}\n"
            + "This is a REAL cloud write, not a simulation. Review the before/after policy and workload dependencies first.",
            "Confirm live cloud administration"))
        { AdministrationStatus = "Write cancelled; no cloud changes requested."; return; }
        var organization = SelectedOrganization;
        var identity = _identity;
        AdministrationBusy = true;
        DependenciesReviewed = false;
        try
        {
            var result = reconcile ? await _administration.ReconcileAsync(plan.Id)
                : rollback ? await _administration.RollbackAsync(plan.Id, true) : await _administration.ApplyAsync(plan.Id, true);
            if (SelectedOrganization != organization || _identity != identity) return;
            RecordAdministrationPlan(result);
            AdministrationStatus = $"{result.Status}: {result.Notice}";
        }
        catch (Exception exception)
        {
            if (SelectedOrganization == organization && _identity == identity)
            {
                if (exception is CloudAdministrationException writeException) RecordAdministrationPlan(writeException.Plan);
                AdministrationStatus = $"Administration failed: {exception.Message} If a write started, use Reconcile (read only), never blind retry. Check the local audit journal.";
            }
        }
        finally { AdministrationBusy = false; }
    }
    private void RecordAdministrationPlan(CloudAdministrationPlan plan)
    {
        AdministrationPlans = AdministrationPlans.Where(item => item.Id != plan.Id).Append(plan).ToArray();
        SelectedAdministrationPlan = plan;
    }
}
