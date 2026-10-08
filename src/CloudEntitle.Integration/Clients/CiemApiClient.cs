using System.Net.Http.Json;
using System.Net.Http.Headers;
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

    public async Task<ApiEnvelope<ApiIdentityDto>?> GetIdentityAsync(CancellationToken cancellationToken = default) =>
        await _httpClient.GetFromJsonAsync<ApiEnvelope<ApiIdentityDto>>("api/v1/identity", cancellationToken);

    public async Task<ApiEnvelope<CentralAuditEventDto>?> AppendAuditEventAsync(CentralAuditAppendRequest auditEvent, string accessToken, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/audit/events") { Content = JsonContent.Create(auditEvent) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ApiEnvelope<CentralAuditEventDto>>(cancellationToken);
    }

    public async Task<ApiEnvelope<IReadOnlyList<CentralAuditEventDto>>?> GetAuditEventsAsync(string accessToken, long afterSequence = 0, int limit = 100, CancellationToken cancellationToken = default)
    {
        var uri = $"api/v1/audit/events?afterSequence={afterSequence}&limit={limit}";
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ApiEnvelope<IReadOnlyList<CentralAuditEventDto>>>(cancellationToken);
    }
}
