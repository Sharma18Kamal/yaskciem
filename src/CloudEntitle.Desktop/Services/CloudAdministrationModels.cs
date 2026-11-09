using CloudEntitle.Domain;

namespace CloudEntitle.Desktop.Services;

public enum CloudAdministrationOperation
{
    AzureAssignRole,
    AzureRemoveRole,
    AzureUpdateCustomRole,
    AwsAttachManagedPolicy,
    AwsDetachManagedPolicy,
    AwsPutInlinePolicy,
    AwsDeleteInlinePolicy,
    GcpSetAllowPolicy,
    GcpPutDenyPolicy,
    GcpDeleteDenyPolicy
}

public sealed record CloudAdministrationRequest(CloudProvider Provider, string AccountId, string Profile,
    CloudAdministrationOperation Operation, string Scope, string SubjectKind, string Subject,
    string RoleOrPolicy, string PolicyJson, string Reason);

public sealed record CloudAdministrationPlan(Guid Id, Guid OrganizationId, Guid ActorId,
    CloudAdministrationRequest Request, CloudAccountVerification CloudIdentity,
    string BeforeJson, string AfterJson, DateTimeOffset PreparedAtUtc, string Status, string? Notice = null);

public sealed class CloudAdministrationException(CloudAdministrationPlan plan, Exception cause, bool auditFailed = false)
    : InvalidOperationException(auditFailed
        ? "Cloud write outcome is unknown and the audit append failed. Preserve this session and reconcile; do not retry."
        : "Cloud write outcome is unknown. Use read-only reconciliation; do not retry.", cause)
{
    public CloudAdministrationPlan Plan { get; } = plan;
}

public interface ICloudAdministrationService
{
    Task<CloudAdministrationPlan> PreviewAsync(CloudAdministrationRequest request, CancellationToken token = default);
    Task<CloudAdministrationPlan> ApplyAsync(Guid planId, bool dependenciesReviewed, CancellationToken token = default);
    Task<CloudAdministrationPlan> VerifyAsync(Guid planId, CancellationToken token = default);
    Task<CloudAdministrationPlan> RollbackAsync(Guid planId, bool dependenciesReviewed, CancellationToken token = default);
    Task<CloudAdministrationPlan> ReconcileAsync(Guid planId, CancellationToken token = default);
}
