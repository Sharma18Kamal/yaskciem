using System.Net.Http;
using CloudEntitle.Integration.Contracts;

namespace CloudEntitle.Integration.Clients;

public sealed partial class CiemApiClient
{
    public Task<ApiEnvelope<IReadOnlyList<ContinuousChangeFeedStatus>>?> GetContinuousChangeFeedsAsync(Guid organizationId,
        string accessToken, CancellationToken token = default)
        => SendForEnvelopeAsync<IReadOnlyList<ContinuousChangeFeedStatus>>(HttpMethod.Get,
            $"api/v1/organizations/{organizationId:D}/continuous-change-feeds", accessToken, cancellationToken: token);

    public Task<ApiEnvelope<ContinuousChangeFeedStatus>?> ConfigureContinuousChangeFeedAsync(Guid organizationId, string alias,
        ConfigureContinuousChangeFeedRequest request, string accessToken, CancellationToken token = default)
        => SendForEnvelopeAsync<ContinuousChangeFeedStatus>(HttpMethod.Put,
            $"api/v1/organizations/{organizationId:D}/continuous-change-feeds/{Uri.EscapeDataString(alias)}", accessToken, request, token);

    public Task<ApiEnvelope<IReadOnlyList<StoredContinuousChange>>?> GetContinuousCloudChangesAsync(Guid organizationId,
        string alias, string accessToken, CancellationToken token = default)
        => SendForEnvelopeAsync<IReadOnlyList<StoredContinuousChange>>(HttpMethod.Get,
            $"api/v1/organizations/{organizationId:D}/continuous-change-feeds/{Uri.EscapeDataString(alias)}/changes?limit=100",
            accessToken, cancellationToken: token);

    public Task PauseContinuousChangeFeedAsync(Guid organizationId, string alias, string accessToken, CancellationToken token = default)
        => SendWithoutEnvelopeAsync(HttpMethod.Post,
            $"api/v1/organizations/{organizationId:D}/continuous-change-feeds/{Uri.EscapeDataString(alias)}/pause",
            accessToken, token);
}
