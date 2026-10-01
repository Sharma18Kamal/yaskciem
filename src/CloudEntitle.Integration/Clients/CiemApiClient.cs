using System.Net.Http.Json;
using CloudEntitle.Integration.Contracts;

namespace CloudEntitle.Integration.Clients;

public sealed class CiemApiClient
{
    private readonly HttpClient _httpClient;

    public CiemApiClient(HttpClient httpClient) => _httpClient = httpClient;

    public async Task<ApiHealthResponse?> GetHealthAsync(CancellationToken cancellationToken = default) =>
        await _httpClient.GetFromJsonAsync<ApiHealthResponse>("api/v1/health", cancellationToken);

    public async Task<ApiReadinessResponse?> GetReadinessAsync(CancellationToken cancellationToken = default) =>
        await _httpClient.GetFromJsonAsync<ApiReadinessResponse>("api/v1/readiness", cancellationToken);

    public async Task<ApiEnvelope<IReadOnlyList<ProviderCapabilityDto>>?> GetProvidersAsync(CancellationToken cancellationToken = default) =>
        await _httpClient.GetFromJsonAsync<ApiEnvelope<IReadOnlyList<ProviderCapabilityDto>>>("api/v1/providers", cancellationToken);
}
