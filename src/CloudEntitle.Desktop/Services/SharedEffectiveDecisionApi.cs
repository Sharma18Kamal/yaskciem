using CloudEntitle.Integration.Contracts;

namespace CloudEntitle.Desktop.Services;

public interface ISharedEffectiveDecisionApi
{
    Task<IReadOnlyList<EffectiveDecisionSource>> GetEffectiveDecisionSourcesAsync(Guid organization, CancellationToken token = default);
    Task<EffectiveDecisionSnapshot> CaptureEffectiveDecisionAsync(Guid organization, CaptureEffectiveDecisionRequest request,
        CancellationToken token = default);
    Task<IReadOnlyList<EffectiveDecisionSnapshot>> GetEffectiveDecisionHistoryAsync(Guid organization, string source, CancellationToken token = default);
    Task<DecisionEscalationReport> AnalyzeDecisionEscalationAsync(Guid organization, AnalyzeDecisionEscalationRequest request,
        CancellationToken token = default);
}

public sealed partial class SharedOrganizationApi : ISharedEffectiveDecisionApi
{
    public async Task<IReadOnlyList<EffectiveDecisionSource>> GetEffectiveDecisionSourcesAsync(Guid organization, CancellationToken token = default)
        => await RequireDataAsync(client.GetEffectiveDecisionSourcesAsync(organization, await TokenAsync(token), token));
    public async Task<EffectiveDecisionSnapshot> CaptureEffectiveDecisionAsync(Guid organization, CaptureEffectiveDecisionRequest request,
        CancellationToken token = default)
        => await RequireDataAsync(client.CaptureEffectiveDecisionAsync(organization, request, await TokenAsync(token), token));
    public async Task<IReadOnlyList<EffectiveDecisionSnapshot>> GetEffectiveDecisionHistoryAsync(Guid organization, string source, CancellationToken token = default)
        => await RequireDataAsync(client.GetEffectiveDecisionHistoryAsync(organization, source, await TokenAsync(token), token));
    public async Task<DecisionEscalationReport> AnalyzeDecisionEscalationAsync(Guid organization, AnalyzeDecisionEscalationRequest request,
        CancellationToken token = default)
        => await RequireDataAsync(client.AnalyzeDecisionEscalationAsync(organization, request, await TokenAsync(token), token));
}
