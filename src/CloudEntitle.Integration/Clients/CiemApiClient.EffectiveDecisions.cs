using CloudEntitle.Integration.Contracts;

namespace CloudEntitle.Integration.Clients;

public sealed partial class CiemApiClient
{
    public Task<ApiEnvelope<IReadOnlyList<EffectiveDecisionSource>>?> GetEffectiveDecisionSourcesAsync(Guid organization,
        string accessToken, CancellationToken token = default)
        => SendForEnvelopeAsync<IReadOnlyList<EffectiveDecisionSource>>(HttpMethod.Get,
            $"api/v1/organizations/{organization:D}/effective-decisions/sources", accessToken, cancellationToken: token);
    public Task<ApiEnvelope<EffectiveDecisionSnapshot>?> CaptureEffectiveDecisionAsync(Guid organization,
        CaptureEffectiveDecisionRequest request, string accessToken, CancellationToken token = default)
        => SendForEnvelopeAsync<EffectiveDecisionSnapshot>(HttpMethod.Post,
            $"api/v1/organizations/{organization:D}/effective-decisions/capture", accessToken, request, token);
    public Task<ApiEnvelope<IReadOnlyList<EffectiveDecisionSnapshot>>?> GetEffectiveDecisionHistoryAsync(Guid organization,
        string source, string accessToken, CancellationToken token = default)
        => SendForEnvelopeAsync<IReadOnlyList<EffectiveDecisionSnapshot>>(HttpMethod.Get,
            $"api/v1/organizations/{organization:D}/effective-decisions?sourceId={Uri.EscapeDataString(source)}",
            accessToken, cancellationToken: token);
    public Task<ApiEnvelope<EffectiveDecisionSnapshot>?> GetEffectiveDecisionSnapshotAsync(Guid organization, string source,
        Guid snapshot, string accessToken, CancellationToken token = default)
        => SendForEnvelopeAsync<EffectiveDecisionSnapshot>(HttpMethod.Get,
            $"api/v1/organizations/{organization:D}/effective-decisions/{snapshot:D}?sourceId={Uri.EscapeDataString(source)}",
            accessToken, cancellationToken: token);
    public Task<ApiEnvelope<DecisionEscalationReport>?> AnalyzeDecisionEscalationAsync(Guid organization,
        AnalyzeDecisionEscalationRequest request, string accessToken, CancellationToken token = default)
        => SendForEnvelopeAsync<DecisionEscalationReport>(HttpMethod.Post,
            $"api/v1/organizations/{organization:D}/effective-decisions/analyze", accessToken, request, token);
    public Task<ApiEnvelope<DecisionEscalationReport>?> GetDecisionEscalationAnalysisAsync(Guid organization,
        string source, Guid analysis, string accessToken, CancellationToken token = default)
        => SendForEnvelopeAsync<DecisionEscalationReport>(HttpMethod.Get,
            $"api/v1/organizations/{organization:D}/effective-decisions/analyses/{analysis:D}?sourceId={Uri.EscapeDataString(source)}",
            accessToken, cancellationToken: token);
}
