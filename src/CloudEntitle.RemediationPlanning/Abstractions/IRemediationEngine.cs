using CloudEntitle.Domain;
using CloudEntitle.RemediationPlanning.Models;

namespace CloudEntitle.RemediationPlanning.Abstractions;

public interface IRemediationProviderAdapter
{
    CloudProvider Provider { get; }
    Task<ProviderChangeValidation> ValidateAsync(RemediationPlan plan, CancellationToken cancellationToken = default);
    Task<ProviderChangeValidation> DryRunAsync(RemediationPlan plan, CancellationToken cancellationToken = default);
    Task<ProviderChangeResult> ApplyAsync(RemediationPlan plan, CancellationToken cancellationToken = default);
    Task<ProviderChangeValidation> VerifyAsync(RemediationPlan plan, CancellationToken cancellationToken = default);
    Task<ProviderChangeResult> RollbackAsync(RemediationPlan rollbackPlan, CancellationToken cancellationToken = default);
}

public interface IRemediationEngine
{
    Task<RemediationExecution> ExecuteAsync(RemediationExecutionRequest request, CancellationToken cancellationToken = default);
    Task<ProviderChangeValidation> VerifyAsync(RemediationPlan plan, CancellationToken cancellationToken = default);
}
