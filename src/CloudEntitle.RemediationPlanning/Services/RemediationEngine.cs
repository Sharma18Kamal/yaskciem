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

        var validation = await adapter.ValidateAsync(request.Plan, cancellationToken);
        diagnostics.AddRange(validation.Diagnostics);
        if (!validation.IsValid)
        {
            return Result(request, RemediationExecutionStatus.Blocked, false, diagnostics, started);
        }

        if (request.Mode == RemediationExecutionMode.DryRun)
        {
            diagnostics.Add("Dry run completed. No cloud mutation was attempted.");
            return Result(request, RemediationExecutionStatus.DryRunCompleted, false, diagnostics, started);
        }

        if (!request.ExplicitApproval || request.Plan.Status != RemediationPlanStatus.Approved)
        {
            diagnostics.Add("Live execution is blocked until the plan is explicitly approved.");
            return Result(request, RemediationExecutionStatus.Blocked, false, diagnostics, started);
        }

        diagnostics.Add("Live provider mutation is disabled in this build. No cloud mutation was attempted.");
        return Result(request, RemediationExecutionStatus.Blocked, false, diagnostics, started);
    }

    private static RemediationExecution Result(RemediationExecutionRequest request, RemediationExecutionStatus status, bool mutated, IReadOnlyList<string> diagnostics, DateTime started) =>
        new(Guid.NewGuid(), request.Plan.Id, request.Plan.Provider, request.Mode, status, mutated, diagnostics, started, DateTime.UtcNow);
}

public abstract class NonMutatingProviderAdapter : IRemediationProviderAdapter
{
    public abstract CloudProvider Provider { get; }

    public Task<ProviderChangeValidation> ValidateAsync(RemediationPlan plan, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProviderChangeValidation(plan.Changes.Count > 0 && plan.Changes.All(change => !change.Executable), new[] { "Plan validated as non-executable review data." }));

    public Task<ProviderChangeResult> ApplyAsync(RemediationPlan plan, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProviderChangeResult(false, false, new[] { "Provider mutation is intentionally unavailable." }));

    public Task<ProviderChangeValidation> VerifyAsync(RemediationPlan plan, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProviderChangeValidation(false, new[] { "Verification requires a future provider-side remediation adapter." }));
}

public sealed class AzureRemediationAdapter : NonMutatingProviderAdapter { public override CloudProvider Provider => CloudProvider.Azure; }
public sealed class AwsRemediationAdapter : NonMutatingProviderAdapter { public override CloudProvider Provider => CloudProvider.Aws; }
public sealed class GcpRemediationAdapter : NonMutatingProviderAdapter { public override CloudProvider Provider => CloudProvider.Gcp; }
