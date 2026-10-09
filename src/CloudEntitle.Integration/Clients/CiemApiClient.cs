using System.Net.Http.Json;
using System.Net.Http.Headers;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.IdentityGovernance.Models;
using CloudEntitle.OrganizationManagement.Models;

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

    public Task<ApiEnvelope<IReadOnlyList<OrganizationProfile>>?> GetOrganizationsAsync(string accessToken, CancellationToken cancellationToken = default)
        => SendForEnvelopeAsync<IReadOnlyList<OrganizationProfile>>(HttpMethod.Get, "api/v1/organizations", accessToken, cancellationToken: cancellationToken);

    public Task<ApiEnvelope<OrganizationProfile>?> CreateOrganizationAsync(CreateOrganizationRequest request, string accessToken, CancellationToken cancellationToken = default)
        => SendForEnvelopeAsync<OrganizationProfile>(HttpMethod.Post, "api/v1/organizations", accessToken, request, cancellationToken);

    public Task<ApiEnvelope<OrganizationWorkspaceDto>?> GetOrganizationWorkspaceAsync(Guid organizationId, string accessToken, CancellationToken cancellationToken = default)
        => SendForEnvelopeAsync<OrganizationWorkspaceDto>(HttpMethod.Get, $"api/v1/organizations/{organizationId:D}/workspace", accessToken, cancellationToken: cancellationToken);

    public Task<ApiEnvelope<OrganizationMember>?> InviteOrganizationMemberAsync(Guid organizationId, InviteOrganizationMemberRequest request, string accessToken, CancellationToken cancellationToken = default)
        => SendForEnvelopeAsync<OrganizationMember>(HttpMethod.Post, $"api/v1/organizations/{organizationId:D}/members", accessToken, request, cancellationToken);

    public Task<ApiEnvelope<OrganizationMember>?> UpdateOrganizationMemberRoleAsync(Guid organizationId, Guid memberId, UpdateOrganizationMemberRoleRequest request, string accessToken, CancellationToken cancellationToken = default)
        => SendForEnvelopeAsync<OrganizationMember>(HttpMethod.Put, $"api/v1/organizations/{organizationId:D}/members/{memberId:D}/role", accessToken, request, cancellationToken);

    public Task<ApiEnvelope<OrganizationMember>?> SetOrganizationMemberStatusAsync(Guid organizationId, Guid memberId, SetOrganizationMemberStatusRequest request, string accessToken, CancellationToken cancellationToken = default)
        => SendForEnvelopeAsync<OrganizationMember>(HttpMethod.Put, $"api/v1/organizations/{organizationId:D}/members/{memberId:D}/status", accessToken, request, cancellationToken);

    public Task<ApiEnvelope<TenantBinding>?> BindOrganizationAccountAsync(Guid organizationId, BindOrganizationAccountRequest request, string accessToken, CancellationToken cancellationToken = default)
        => SendForEnvelopeAsync<TenantBinding>(HttpMethod.Post, $"api/v1/organizations/{organizationId:D}/bindings", accessToken, request, cancellationToken);

    public Task<ApiEnvelope<OrganizationCustomRole>?> SaveOrganizationCustomRoleAsync(Guid organizationId, Guid? roleId, SaveOrganizationCustomRoleRequest request, string accessToken, CancellationToken cancellationToken = default)
        => SendForEnvelopeAsync<OrganizationCustomRole>(roleId.HasValue ? HttpMethod.Put : HttpMethod.Post,
            roleId.HasValue ? $"api/v1/organizations/{organizationId:D}/custom-roles/{roleId.Value:D}" : $"api/v1/organizations/{organizationId:D}/custom-roles",
            accessToken, request, cancellationToken);

    public async Task DeleteOrganizationCustomRoleAsync(Guid organizationId, Guid roleId, string accessToken, CancellationToken cancellationToken = default)
        => await SendWithoutEnvelopeAsync(HttpMethod.Delete, $"api/v1/organizations/{organizationId:D}/custom-roles/{roleId:D}", accessToken, cancellationToken);

    public Task<ApiEnvelope<IReadOnlyList<OrganizationAuditEntry>>?> GetOrganizationAuditHistoryAsync(Guid organizationId, string accessToken, CancellationToken cancellationToken = default)
        => SendForEnvelopeAsync<IReadOnlyList<OrganizationAuditEntry>>(HttpMethod.Get, $"api/v1/organizations/{organizationId:D}/audit-history", accessToken, cancellationToken: cancellationToken);

    public Task<ApiEnvelope<IReadOnlyList<SharedScanSummaryDto>>?> GetSharedScanSummariesAsync(Guid organizationId, int limit, string accessToken, CancellationToken cancellationToken = default)
        => SendForEnvelopeAsync<IReadOnlyList<SharedScanSummaryDto>>(HttpMethod.Get, $"api/v1/organizations/{organizationId:D}/scan-summaries?limit={limit}", accessToken, cancellationToken: cancellationToken);

    public Task<ApiEnvelope<SharedScanSummaryDto>?> UploadSharedScanSummaryAsync(Guid organizationId, UploadSharedScanSummaryRequest request, string accessToken, CancellationToken cancellationToken = default)
        => SendForEnvelopeAsync<SharedScanSummaryDto>(HttpMethod.Post, $"api/v1/organizations/{organizationId:D}/scan-summaries", accessToken, request, cancellationToken);

    public Task<ApiEnvelope<IReadOnlyList<AccessReviewCampaign>>?> GetSharedAccessReviewsAsync(Guid organizationId, string accessToken, CancellationToken cancellationToken = default)
        => SendForEnvelopeAsync<IReadOnlyList<AccessReviewCampaign>>(HttpMethod.Get, $"api/v1/organizations/{organizationId:D}/access-reviews", accessToken, cancellationToken: cancellationToken);

    public Task<ApiEnvelope<AccessReviewCampaign>?> SaveSharedAccessReviewAsync(Guid organizationId, AccessReviewCampaign campaign, string accessToken, CancellationToken cancellationToken = default)
        => SendForEnvelopeAsync<AccessReviewCampaign>(HttpMethod.Put, $"api/v1/organizations/{organizationId:D}/access-reviews/{campaign.Id:D}", accessToken, campaign, cancellationToken);

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

    private async Task<ApiEnvelope<T>?> SendForEnvelopeAsync<T>(HttpMethod method, string path, string accessToken, object? body = null, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(method, path, accessToken, body);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ApiEnvelope<T>>(cancellationToken);
    }

    private async Task SendWithoutEnvelopeAsync(HttpMethod method, string path, string accessToken, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, path, accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, string accessToken, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }
}
