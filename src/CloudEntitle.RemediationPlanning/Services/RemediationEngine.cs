using CloudEntitle.Domain;
using CloudEntitle.RemediationPlanning.Abstractions;
using CloudEntitle.RemediationPlanning.Models;

namespace CloudEntitle.RemediationPlanning.Services;

public sealed class RemediationEngine : IRemediationEngine
{
    private readonly IReadOnlyDictionary<CloudProvider, IRemediationProviderAdapter> _adapters;

    public RemediationEngine(IEnumerable<IRemediationProviderAdapter>? adapters = null)
    {
        _adapters = (adapters ?? new IRemediationProviderAdapter[]
        {
            new AzureRemediationAdapter(), new AwsRemediationAdapter(), new GcpRemediationAdapter()
        }).ToDictionary(adapter => adapter.Provider);
    }

    public async Task<RemediationExecution> ExecuteAsync(RemediationExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var started = DateTime.UtcNow;
        var diagnostics = new List<string>();
        if (!_adapters.TryGetValue(request.Plan.Provider, out var adapter))
        {
            return Result(request, RemediationExecutionStatus.Blocked, false, new[] { "No provider adapter is registered." }, started);
        }

        if (request.Mode == RemediationExecutionMode.Live
            && (!request.ExplicitApproval
                || request.Plan.Status != RemediationPlanStatus.Approved
                || string.IsNullOrWhiteSpace(request.Plan.ApprovedBy)
                || !request.Plan.ApprovedAt.HasValue
                || string.IsNullOrWhiteSpace(request.Plan.ApprovalComment)))
        {
            return Result(request, RemediationExecutionStatus.Blocked, false, new[] { "Live execution requires an audited approval: approved plan, approver, approval time, and rationale." }, started);
        }

        var validation = await adapter.ValidateAsync(request.Plan, cancellationToken);
        diagnostics.AddRange(validation.Diagnostics);
        if (!validation.IsValid)
        {
            return Result(request, RemediationExecutionStatus.Blocked, false, diagnostics, started);
        }

        if (request.Mode == RemediationExecutionMode.DryRun)
        {
            if (!HasAuditedApproval(request.Plan))
                return Result(request, RemediationExecutionStatus.Blocked, false, diagnostics.Append("Dry run requires an explicitly approved plan with approver, time, and rationale.").ToArray(), started);
            var dryRun = await adapter.DryRunAsync(request.Plan, cancellationToken);
            diagnostics.AddRange(dryRun.Diagnostics);
            if (!dryRun.IsValid) return Result(request, RemediationExecutionStatus.Blocked, false, diagnostics, started);
            diagnostics.Add("Dry run completed. No cloud mutation was attempted.");
            return Result(request, RemediationExecutionStatus.DryRunCompleted, false, diagnostics, started);
        }

        if (request.Plan.Changes.Any(change => !change.Executable))
        {
            diagnostics.Add("Live execution is blocked because this plan contains no provider-approved executable changes.");
            return Result(request, RemediationExecutionStatus.Blocked, false, diagnostics, started);
        }

        var applied = await adapter.ApplyAsync(request.Plan, cancellationToken);
        diagnostics.AddRange(applied.Diagnostics);
        if (!applied.Succeeded || !applied.MutatedCloud)
        {
            diagnostics.Add("Provider mutation was not confirmed; verification and rollback were not attempted.");
            return Result(request, RemediationExecutionStatus.Blocked, false, diagnostics, started);
        }

        if (!request.RequireVerification) return Result(request, RemediationExecutionStatus.Executed, true, diagnostics, started);
        var verification = await adapter.VerifyAsync(request.Plan, cancellationToken);
        diagnostics.AddRange(verification.Diagnostics);
        var verificationRecord = new RemediationVerification(
            verification.IsValid ? RemediationVerificationStatus.Verified : RemediationVerificationStatus.Failed,
            string.Join("; ", verification.Diagnostics), DateTime.UtcNow);
        return Result(request, verification.IsValid ? RemediationExecutionStatus.Verified : RemediationExecutionStatus.VerificationFailed, true, diagnostics, started) with
        {
            Verification = verificationRecord
        };
    }

    public Task<ProviderChangeValidation> VerifyAsync(RemediationPlan plan, CancellationToken cancellationToken = default)
        => _adapters.TryGetValue(plan.Provider, out var adapter)
            ? adapter.VerifyAsync(plan, cancellationToken)
            : Task.FromResult(new ProviderChangeValidation(false, new[] { "No provider adapter is registered for verification." }));

    private static bool HasAuditedApproval(RemediationPlan plan)
        => plan.Status is RemediationPlanStatus.Approved or RemediationPlanStatus.DryRunCompleted
            && !string.IsNullOrWhiteSpace(plan.ApprovedBy)
            && plan.ApprovedAt.HasValue
            && !string.IsNullOrWhiteSpace(plan.ApprovalComment);

    private static RemediationExecution Result(RemediationExecutionRequest request, RemediationExecutionStatus status, bool mutated, IReadOnlyList<string> diagnostics, DateTime started) =>
        new(Guid.NewGuid(), request.Plan.Id, request.Plan.Provider, request.Mode, status, mutated, diagnostics, started, DateTime.UtcNow);
}

public abstract class NonMutatingProviderAdapter : IRemediationProviderAdapter
{
    public abstract CloudProvider Provider { get; }

    public Task<ProviderChangeValidation> ValidateAsync(RemediationPlan plan, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProviderChangeValidation(plan.Changes.Count > 0 && plan.Changes.All(change => !change.Executable), new[] { "Plan validated as non-executable review data." }));

    public Task<ProviderChangeValidation> DryRunAsync(RemediationPlan plan, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProviderChangeValidation(plan.Changes.Count > 0 && plan.Changes.All(change => !change.Executable), new[] { "Provider dry run is structural only; no commands or cloud changes were executed." }));

    public Task<ProviderChangeResult> ApplyAsync(RemediationPlan plan, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProviderChangeResult(false, false, new[] { "Provider mutation is intentionally unavailable." }));

    public Task<ProviderChangeValidation> VerifyAsync(RemediationPlan plan, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProviderChangeValidation(false, new[] { "Verification requires a future provider-side remediation adapter." }));

    public Task<ProviderChangeResult> RollbackAsync(RemediationPlan rollbackPlan, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProviderChangeResult(false, false, new[] { "Provider rollback is unavailable because this adapter is non-mutating." }));
}

public sealed class AzureRemediationAdapter : NonMutatingProviderAdapter { public override CloudProvider Provider => CloudProvider.Azure; }
public sealed class AwsRemediationAdapter : NonMutatingProviderAdapter { public override CloudProvider Provider => CloudProvider.Aws; }
public sealed class GcpRemediationAdapter : NonMutatingProviderAdapter { public override CloudProvider Provider => CloudProvider.Gcp; }
