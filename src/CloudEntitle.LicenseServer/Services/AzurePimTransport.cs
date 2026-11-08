using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using CloudEntitle.Integration.Contracts;

namespace CloudEntitle.LicenseServer.Services;

public sealed record PimObservation(string Status, string? ScheduleId = null, string? FailureCode = null,
    DateTimeOffset? StartsAtUtc = null, DateTimeOffset? ExpiresAtUtc = null);
public interface IAzurePimTransport
{
    Task<PimObservation> SubmitAsync(Guid tenant, PrivilegedAccessRequest request, CancellationToken token);
    Task<PimObservation> ObserveAsync(Guid tenant, PrivilegedAccessRequest request, CancellationToken token);
    Task<PimObservation> RevokeAsync(Guid tenant, PrivilegedAccessRequest request, CancellationToken token)
        => Task.FromResult(new PimObservation("RevokeOutcomeUnknown", request.ScheduleId, "revocation_transport_not_supported"));
}

public sealed class AzurePimTransport : IAzurePimTransport, IDisposable
{
    private readonly HttpClient _client;
    private readonly TokenCredential _credential;
    private const string Api = "?api-version=2020-10-01";
    public AzurePimTransport() : this(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }, new ManagedIdentityCredential()) { }
    public AzurePimTransport(HttpMessageHandler handler, TokenCredential credential)
    {
        _client = new(handler) { Timeout = TimeSpan.FromSeconds(20) };
        _credential = credential;
    }
    private static string RequestPath(PrivilegedAccessRequest request)
        => $"{request.Scope}/providers/Microsoft.Authorization/roleAssignmentScheduleRequests/{request.RequestId:D}{Api}";

    public async Task<PimObservation> SubmitAsync(Guid tenant, PrivilegedAccessRequest request, CancellationToken token)
    {
        if (request.StartsAtUtc is null || request.ExpiresAtUtc is null || request.ExpiresAtUtc <= request.StartsAtUtc
            || request.ExpiresAtUtc - request.StartsAtUtc > TimeSpan.FromMinutes(60))
            throw new ArgumentException("An absolute bounded PIM lifetime is required.");
        using var subscription = await SendAsync(HttpMethod.Get, $"/subscriptions/{request.SubscriptionId}?api-version=2022-12-01", null, token);
        if (!subscription.IsSuccessStatusCode) return new("Failed", FailureCode: "subscription_unverified");
        using var subscriptionJson = await ReadJsonAsync(subscription, token);
        if (!subscriptionJson.RootElement.TryGetProperty("tenantId", out var tenantId) || !Guid.TryParse(tenantId.GetString(), out var actualTenant) || actualTenant != tenant)
            return new("Failed", FailureCode: "subscription_tenant_mismatch");
        object payload = new { properties = new { principalId = request.RequestedBy, roleDefinitionId = request.RoleDefinitionId,
            requestType = "AdminAssign", justification = request.Reason,
            scheduleInfo = new { startDateTime = request.StartsAtUtc, expiration = new { type = "AfterDateTime", endDateTime = request.ExpiresAtUtc } },
            ticketInfo = new { ticketNumber = request.RequestId.ToString("D"), ticketSystem = "YASK-CIEM dual approval" } } };
        using var response = await SendAsync(HttpMethod.Put, RequestPath(request), payload, token);
        if (!response.IsSuccessStatusCode)
            return new((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout ? "OutcomeUnknown" : "Failed", FailureCode: "pim_request_rejected");
        // Acceptance is not permission activation; the worker reconciles the same deterministic request ID with GET.
        return new("Submitted");
    }

    public async Task<PimObservation> ObserveAsync(Guid tenant, PrivilegedAccessRequest request, CancellationToken token)
    {
        ValidateSchedule(request);
        var path = request.ScheduleId is null ? RequestPath(request) : request.ScheduleId + Api;
        using var response = await SendAsync(HttpMethod.Get, path, null, token);
        if (!response.IsSuccessStatusCode) return new("OutcomeUnknown", request.ScheduleId, "pim_observation_unavailable");
        using var document = await ReadJsonAsync(response, token);
        var root = document.RootElement;
        if (!root.TryGetProperty("properties", out var properties)
            || !properties.TryGetProperty("principalId", out var principal) || !Guid.TryParse(principal.GetString(), out var principalId) || principalId != request.RequestedBy
            || !properties.TryGetProperty("roleDefinitionId", out var role) || !string.Equals(role.GetString(), request.RoleDefinitionId, StringComparison.OrdinalIgnoreCase))
            return new("OutcomeUnknown", request.ScheduleId, "pim_identity_mismatch");
        if (!root.TryGetProperty("id", out var id) || !string.Equals(id.GetString(), path.Split('?')[0], StringComparison.OrdinalIgnoreCase))
            return new("OutcomeUnknown", request.ScheduleId, "pim_scope_mismatch");
        var status = properties.TryGetProperty("status", out var value) ? value.GetString() : null;
        if (status is "Denied" or "Failed" or "Canceled") return new("Failed", request.ScheduleId, "pim_denied");
        if (request.ScheduleId is null)
        {
            var schedule = properties.TryGetProperty("targetRoleAssignmentScheduleId", out var target) ? target.GetString() : null;
            if (status is not ("Granted" or "Provisioned") || !Guid.TryParse(schedule, out var scheduleId))
                return request.ExpiresAtUtc <= DateTimeOffset.UtcNow
                    ? new("ExpiryPending", FailureCode: "revocation_not_verified") : new("Submitted");
            return new("Submitted", $"{request.Scope}/providers/Microsoft.Authorization/roleAssignmentSchedules/{scheduleId:D}");
        }

        if (status is "Revoked" or "Expired") return new("Revoked", request.ScheduleId);
        if (!properties.TryGetProperty("endDateTime", out var end) || !DateTimeOffset.TryParse(end.GetString(), out var ends)
            || request.ExpiresAtUtc is null || ends > request.ExpiresAtUtc || ends <= request.StartsAtUtc)
            return new("OutcomeUnknown", request.ScheduleId, "pim_expiration_mismatch");
        if (request.ExpiresAtUtc <= DateTimeOffset.UtcNow) return new("ExpiryPending", request.ScheduleId, "revocation_not_verified");
        if (!properties.TryGetProperty("startDateTime", out var start) || !DateTimeOffset.TryParse(start.GetString(), out var starts)
            || request.StartsAtUtc is null || starts < request.StartsAtUtc.Value.AddSeconds(-1))
            return new("OutcomeUnknown", request.ScheduleId, "pim_start_mismatch");
        if (starts > DateTimeOffset.UtcNow) return new("Submitted", request.ScheduleId);
        return status is "Provisioned" or "Granted" ? new("Active", request.ScheduleId) : new("Submitted", request.ScheduleId);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken token)
    {
        var accessToken = await _credential.GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"]), token);
        using var message = new HttpRequestMessage(method, new Uri("https://management.azure.com" + path));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        if (body is not null) message.Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
        return await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
    }
    public async Task<PimObservation> RevokeAsync(Guid tenant, PrivilegedAccessRequest request, CancellationToken token)
    {
        ValidateSchedule(request);
        if (request.RevokeId is null || request.ScheduleId is null)
            throw new ArgumentException("Durable revocation ID and exact native schedule are required.");
        var schedule = request.ScheduleId.Split('/').Last();
        var payload = new { properties = new { principalId = request.RequestedBy, roleDefinitionId = request.RoleDefinitionId,
            requestType = "AdminRemove", targetRoleAssignmentScheduleId = schedule, justification = request.RevokeReason } };
        using var response = await SendAsync(HttpMethod.Put,
            $"{request.Scope}/providers/Microsoft.Authorization/roleAssignmentScheduleRequests/{request.RevokeId:D}{Api}", payload, token);
        return new("RevokeOutcomeUnknown", request.ScheduleId,
            response.IsSuccessStatusCode ? "revocation_accepted_unverified" : "revocation_write_unverified");
    }

    private static void ValidateSchedule(PrivilegedAccessRequest request)
    {
        if (request.ScheduleId is not null && (!request.ScheduleId.StartsWith(
                $"{request.Scope}/providers/Microsoft.Authorization/roleAssignmentSchedules/", StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParse(request.ScheduleId.Split('/').Last(), out _)
            || request.ScheduleId.Split('/').Length != request.Scope.Split('/').Length + 4))
            throw new ArgumentException("Only the exact saved schedule inside the approved scope can be observed or removed.");
    }
    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken token)
    {
        const int maximum = 1024 * 1024;
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("PIM response exceeds the limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[maximum + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), token);
            if (read == 0) break;
            count += read;
        }
        if (count > maximum) throw new InvalidDataException("PIM response exceeds the limit.");
        return JsonDocument.Parse(buffer.AsMemory(0, count));
    }
    public void Dispose() => _client.Dispose();
}
