using System.Net.Http.Headers;
using System.Text.Json;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;

namespace CloudEntitle.LicenseServer.Services;

/// <summary>Routes Azure native PIM, client-owned AWS session brokers, and native GCP PAM.</summary>
public sealed class MultiCloudJitTransport(IAzurePimTransport azure, IConfiguration configuration) : IAzurePimTransport, IDisposable
{
    private readonly HttpClient _client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = TimeSpan.FromSeconds(20) };
    public Task<PimObservation> SubmitAsync(Guid tenant, PrivilegedAccessRequest request, CancellationToken token)
        => request.Provider == CloudProvider.Azure ? azure.SubmitAsync(tenant, request, token)
            : SubmitOtherAsync(request, token);
    public Task<PimObservation> ObserveAsync(Guid tenant, PrivilegedAccessRequest request, CancellationToken token)
        => request.Provider == CloudProvider.Azure ? azure.ObserveAsync(tenant, request, token)
            : ObserveOtherAsync(request, token);
    public Task<PimObservation> RevokeAsync(Guid tenant, PrivilegedAccessRequest request, CancellationToken token)
        => request.Provider == CloudProvider.Azure ? azure.RevokeAsync(tenant, request, token)
            : RevokeOtherAsync(request, token);

    public MultiCloudJitTransport(IAzurePimTransport azure, IConfiguration configuration, HttpMessageHandler handler)
        : this(azure, configuration)
    {
        _client.Dispose();
        _client = new(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }

    private async Task<PimObservation> SubmitOtherAsync(PrivilegedAccessRequest request, CancellationToken token)
    {
        if (request.StartsAtUtc is null || request.ExpiresAtUtc is null || request.DurationMinutes is < 1 or > 60
            || request.Emergency && request.DurationMinutes > 15)
            throw new ArgumentException("Bounded human access is required.");
        if (request.Provider == CloudProvider.Aws)
        {
            using var document = await SendAsync(HttpMethod.Put, Broker(request, $"/grants/{request.RequestId:D}"),
                request.AuthSecretKey, new { requestId = request.RequestId, requesterObjectId = request.RequestedBy,
                    accountId = request.SubscriptionId, role = request.RoleDefinitionId, startsAtUtc = request.StartsAtUtc,
                    expiresAtUtc = request.ExpiresAtUtc, reason = request.Reason, emergency = request.Emergency,
                    humanSessionRequired = true, requesterOwnedHandoffRequired = true }, token);
            if (document is null) return new("OutcomeUnknown", FailureCode: "broker_submission_unverified");
            var accepted = ReadBroker(request, document.RootElement);
            return accepted.Status == "Active" ? accepted with { Status = "Submitted" } : accepted;
        }
        ValidateGcp(request);
        using var identity = await SendAsync(HttpMethod.Get, "https://openidconnect.googleapis.com/v1/userinfo",
            request.AuthSecretKey, null, token);
        if (identity is null || Text(identity.RootElement, "email") != request.HumanPrincipal![5..]
            || !identity.RootElement.TryGetProperty("email_verified", out var verified) || !verified.GetBoolean())
            return new("Failed", FailureCode: "pam_native_human_token_mismatch");
        using var entitlement = await SendAsync(HttpMethod.Get,
            $"https://privilegedaccessmanager.googleapis.com/v1/{request.Scope}", request.AuthSecretKey, null, token);
        if (entitlement is null || Text(entitlement.RootElement, "name") != request.Scope
            || !MatchesGcpPrivilege(request, entitlement.RootElement))
            return new("Failed", FailureCode: "pam_entitlement_privilege_mismatch");
        // PAM requester is output-only: the configured OAuth token must belong to this mapped human.
        using var result = await SendAsync(HttpMethod.Post,
            $"https://privilegedaccessmanager.googleapis.com/v1/{request.Scope}/grants?requestId={request.RequestId:D}",
            request.AuthSecretKey, new { requestedDuration = $"{request.DurationMinutes * 60}s",
                justification = new { unstructuredJustification = request.Reason } }, token);
        if (result is null) return new("OutcomeUnknown", FailureCode: "pam_create_outcome_unknown_no_grant_id");
        var name = Text(result.RootElement, "name");
        if (!ExactGcpGrant(request, name))
            return new("OutcomeUnknown", FailureCode: "pam_create_outcome_unknown_no_grant_id");
        // Keep the exact ID even when native approval is pending. Only a later GET proves active access.
        return new("Submitted", name);
    }

    private async Task<PimObservation> ObserveOtherAsync(PrivilegedAccessRequest request, CancellationToken token)
    {
        if (request.Provider == CloudProvider.Aws)
        {
            using var document = await SendAsync(HttpMethod.Get, Broker(request, $"/grants/{request.RequestId:D}"),
                request.RevokeId is null ? request.AuthSecretKey : request.RevocationAuthSecretKey, null, token);
            return document is null ? new("OutcomeUnknown", request.ScheduleId, "broker_observation_unavailable")
                : ReadBroker(request, document.RootElement);
        }
        ValidateGcp(request);
        if (!ExactGcpGrant(request, request.ScheduleId))
            return new("OutcomeUnknown", FailureCode: "pam_unknown_create_requires_client_investigation_no_safe_get_correlation");
        using var result = await SendAsync(HttpMethod.Get,
            $"https://privilegedaccessmanager.googleapis.com/v1/{request.ScheduleId}",
            request.RevokeId is null ? request.AuthSecretKey : request.RevocationAuthSecretKey, null, token);
        if (result is null) return new("OutcomeUnknown", request.ScheduleId, "pam_observation_unavailable");
        var root = result.RootElement;
        if (Text(root, "name") != request.ScheduleId || Text(root, "requester") != request.HumanPrincipal![5..]
            || root.TryGetProperty("externallyModified", out var modified) && modified.GetBoolean())
            return new("OutcomeUnknown", request.ScheduleId, "pam_identity_or_external_modification_mismatch");
        var state = Text(root, "state");
        if (state is "REVOKED" or "ENDED") return new("Revoked", request.ScheduleId);
        if (state is "DENIED" or "WITHDRAWN" or "EXPIRED" or "ACTIVATION_FAILED") return new("Failed", request.ScheduleId, "pam_no_access_terminal");
        if (state != "ACTIVE") return new("Submitted", request.ScheduleId, state == "APPROVAL_AWAITED" ? "native_approval_required" : null);
        if (!root.TryGetProperty("auditTrail", out var audit) || !Date(audit, "accessGrantTime", out var starts)
            || Text(root, "requestedDuration") != $"{request.DurationMinutes * 60}s"
            || starts < request.StartsAtUtc!.Value.AddSeconds(-1) || starts > DateTimeOffset.UtcNow)
            return new("OutcomeUnknown", request.ScheduleId, "pam_native_lifetime_unverified");
        var ends = starts.AddMinutes(request.DurationMinutes);
        if (!MatchesGcpPrivilege(request, root))
            return new("OutcomeUnknown", request.ScheduleId, "pam_privilege_mismatch");
        return new(ends <= DateTimeOffset.UtcNow ? "ExpiryPending" : "Active", request.ScheduleId, StartsAtUtc: starts, ExpiresAtUtc: ends);
    }

    private static bool MatchesGcpPrivilege(PrivilegedAccessRequest request, JsonElement root)
        => root.TryGetProperty("privilegedAccess", out var access) && access.TryGetProperty("gcpIamAccess", out var iam)
            && Text(iam, "resource") == $"//cloudresourcemanager.googleapis.com/{request.Scope.Split('/')[0]}/{request.SubscriptionId}"
            && iam.TryGetProperty("roleBindings", out var roles) && roles.ValueKind == JsonValueKind.Array && roles.GetArrayLength() == 1
            && Text(roles[0], "role") == request.RoleDefinitionId;

    private async Task<PimObservation> RevokeOtherAsync(PrivilegedAccessRequest request, CancellationToken token)
    {
        if (request.RevokeId is null || request.ScheduleId is null)
            throw new ArgumentException("Durable revoke ID and exact saved grant are required.");
        Uri uri;
        object body;
        if (request.Provider == CloudProvider.Aws)
        {
            if (request.ScheduleId != request.RequestId.ToString("D")) throw new ArgumentException("Broker grant ID mismatch.");
            uri = Broker(request, $"/grants/{request.ScheduleId}/revocations/{request.RevokeId:D}");
            body = new { grantId = request.ScheduleId, revocationId = request.RevokeId, reason = request.RevokeReason };
        }
        else
        {
            ValidateGcp(request);
            if (!ExactGcpGrant(request, request.ScheduleId)) throw new ArgumentException("PAM grant mismatch.");
            uri = new($"https://privilegedaccessmanager.googleapis.com/v1/{request.ScheduleId}:revoke");
            body = new { reason = request.RevokeReason };
        }
        using var result = await SendAsync(request.Provider == CloudProvider.Aws ? HttpMethod.Put : HttpMethod.Post,
            uri, request.RevocationAuthSecretKey, body, token);
        return new("RevokeOutcomeUnknown", request.ScheduleId,
            result is null ? "revocation_write_unverified" : "revocation_accepted_unverified");
    }

    private static PimObservation ReadBroker(PrivilegedAccessRequest request, JsonElement root)
    {
        if (Text(root, "grantId") != request.RequestId.ToString("D")
            || Text(root, "requesterObjectId") != request.RequestedBy.ToString("D")
            || Text(root, "accountId") != request.SubscriptionId || Text(root, "role") != request.RoleDefinitionId)
            return new("OutcomeUnknown", request.ScheduleId, "broker_identity_mismatch");
        var id = request.RequestId.ToString("D");
        var status = Text(root, "status");
        if (status is "Revoked" or "Expired") return new("Revoked", id);
        if (status == "Failed") return new("Failed", id, "broker_failed");
        if (!root.TryGetProperty("nativeExpirationGuaranteed", out var expiry) || !expiry.GetBoolean()
            || !root.TryGetProperty("perGrantRevocationGuaranteed", out var revoke) || !revoke.GetBoolean()
            || !root.TryGetProperty("requesterOwnedHandoff", out var handoff) || !handoff.GetBoolean()
            || !Date(root, "startsAtUtc", out var starts) || !Date(root, "expiresAtUtc", out var ends)
            || starts < request.StartsAtUtc!.Value.AddSeconds(-1) || ends > request.ExpiresAtUtc || ends <= starts)
            return new("OutcomeUnknown", id, "broker_native_session_guarantees_unverified");
        return new(status == "Active" && starts <= DateTimeOffset.UtcNow
            ? ends <= DateTimeOffset.UtcNow ? "ExpiryPending" : "Active" : "Submitted", id);
    }

    private static void ValidateGcp(PrivilegedAccessRequest request)
    {
        if (request.Provider != CloudProvider.Gcp || !PrivilegedAccessPolicy.IsEntitlement(request.Scope)
            || request.HumanPrincipal is null || !request.HumanPrincipal.StartsWith("user:", StringComparison.Ordinal))
            throw new ArgumentException("Exact PAM entitlement and configured human mapping are required.");
    }
    private static bool ExactGcpGrant(PrivilegedAccessRequest request, string? name)
        => name is not null && name.StartsWith(request.Scope + "/grants/", StringComparison.Ordinal)
            && name.Split('/').Length == 8 && name.Split('/').Last().Length > 0
            && name.Split('/').Last().All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static Uri Broker(PrivilegedAccessRequest request, string path)
    {
        if (!PrivilegedAccessPolicy.SafeBroker(request.BrokerEndpoint)) throw new ArgumentException("Saved HTTPS broker endpoint required.");
        return new(request.BrokerEndpoint!.TrimEnd('/') + path);
    }
    private Task<JsonDocument?> SendAsync(HttpMethod method, string uri, string? key, object? payload, CancellationToken token)
        => SendAsync(method, new Uri(uri), key, payload, token);
    private async Task<JsonDocument?> SendAsync(HttpMethod method, Uri uri, string? key, object? payload, CancellationToken token)
    {
        var secret = key is null ? null : configuration[key];
        if (string.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException("Client-owned API authentication is unavailable.");
        using var message = new HttpRequestMessage(method, uri);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        if (payload is not null) message.Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) return null;
        const int maximum = 1024 * 1024;
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("JIT response exceeds the limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var bytes = new byte[maximum + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), token);
            if (read == 0) break;
            count += read;
        }
        if (count > maximum) throw new InvalidDataException("JIT response exceeds the limit.");
        return count == 0 ? JsonDocument.Parse("{}") : JsonDocument.Parse(bytes.AsMemory(0, count));
    }
    private static string? Text(JsonElement value, string property)
        => value.TryGetProperty(property, out var result) && result.ValueKind == JsonValueKind.String ? result.GetString() : null;
    private static bool Date(JsonElement value, string property, out DateTimeOffset date)
        => DateTimeOffset.TryParse(Text(value, property), out date);
    public void Dispose() => _client.Dispose();
}
