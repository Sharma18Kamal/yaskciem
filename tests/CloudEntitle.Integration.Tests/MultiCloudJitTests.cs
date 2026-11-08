using System.Net;
using System.Text.Json;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.LicenseServer.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed class MultiCloudJitTests
{
    [Theory]
    [InlineData("http://broker.example.invalid")]
    [InlineData("https://user:password@broker.example.invalid")]
    [InlineData("https://broker.example.invalid?token=secret")]
    [InlineData("https://localhost")]
    public void Aws_ClientMustConfigureSafeHttpsBrokerAndNativeSecret(string endpoint)
    {
        var tenant = Guid.NewGuid();
        var organization = Guid.NewGuid();
        var root = $"PrivilegedAccess:Organizations:{tenant:D}:{organization:D}";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            [$"{root}:TenantId"] = tenant.ToString("D"), [$"{root}:Targets:aws:Provider"] = "Aws",
            [$"{root}:Targets:aws:ExternalTenantId"] = "123456789012", [$"{root}:Targets:aws:RoleDefinitionId"] = "arn:aws:iam::123456789012:role/Human",
            [$"{root}:Targets:aws:BrokerEndpoint"] = endpoint, [$"{root}:Targets:aws:AuthSecretKey"] = "ClientSecrets:Broker",
            ["ClientSecrets:Broker"] = "fake-token" }).Build();
        Assert.Throws<ArgumentException>(() => PrivilegedAccessPolicy.Target(configuration, tenant, organization, "aws"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("serviceAccount:robot@example.invalid")]
    [InlineData("group:admins@example.invalid")]
    [InlineData("user:arbitrary")]
    public void Gcp_RequiresExplicitPerRequesterHumanMapping(string? principal)
    {
        var tenant = Guid.NewGuid(); var organization = Guid.NewGuid(); var requester = Guid.NewGuid();
        var root = $"PrivilegedAccess:Organizations:{tenant:D}:{organization:D}";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            [$"{root}:HumanPrincipals:{requester:D}:Principal"] = principal,
            [$"{root}:HumanPrincipals:{requester:D}:AccessTokenSecretKey"] = "ClientSecrets:Human",
            ["ClientSecrets:Human"] = "fake-native-user-token" }).Build();
        var target = new PrivilegedAccessTarget("gcp", "client", "projects/client/locations/global/entitlements/investigation",
            "roles/viewer", 60, 15, true, CloudProvider.Gcp);
        Assert.Throws<ArgumentException>(() => PrivilegedAccessPolicy.Human(configuration, tenant, organization, target, requester));
    }

    [Theory]
    [InlineData("PendingApproval", "Cancelled")]
    [InlineData("Approved", "Cancelled")]
    [InlineData("Queued", "Cancelled")]
    [InlineData("Active", "RevokeQueued")]
    [InlineData("Issuing", "RevokeQueued")]
    [InlineData("OutcomeUnknown", "RevokeQueued")]
    public void Revoke_IsVersionedConsentedSingleActorAndDurableBeforeWrites(string status, string expected)
    {
        var current = Request(CloudProvider.Aws) with { Status = status, Version = 8,
            StartsAtUtc = status is "PendingApproval" or "Approved" or "Queued" ? null : DateTimeOffset.UtcNow };
        Assert.Throws<InvalidOperationException>(() => PrivilegedAccessPolicy.Revoke(current, current.RequestedBy, new(7, "End", true), false));
        Assert.Throws<InvalidOperationException>(() => PrivilegedAccessPolicy.Revoke(current, current.RequestedBy, new(8, "End"), false));
        Assert.Throws<UnauthorizedAccessException>(() => PrivilegedAccessPolicy.Revoke(current, Guid.NewGuid(), new(8, "End", true), false));
        Assert.Throws<ArgumentException>(() => PrivilegedAccessPolicy.Revoke(current, current.RequestedBy, new(8, "", true), false));
        var revoked = PrivilegedAccessPolicy.Revoke(current, Guid.NewGuid(), new(8, "Security revoke", true), true);
        Assert.Equal(expected, revoked.Status);
        Assert.Equal(9, revoked.Version);
        Assert.NotNull(revoked.RevokeId);
        Assert.NotEqual(current.RequestId, revoked.RevokeId);
        Assert.Equal("Security revoke", revoked.RevokeReason);
        Assert.Equal(current.Scope, revoked.Scope);
        Assert.Equal(current.RoleDefinitionId, revoked.RoleDefinitionId);
        Assert.Throws<InvalidOperationException>(() => PrivilegedAccessPolicy.Revoke(revoked, current.RequestedBy, new(9, "Again", true), false));
        Assert.Equal(expected, PrivilegedAccessPolicy.Revoke(current, current.RequestedBy, new(8, "Requester ends", true), false).Status);
    }

    [Fact]
    public async Task Aws_BrokerAcceptanceIsNotActiveAndExactBoundedHumanGrantIsReadBeforeRevocation()
    {
        var request = Request(CloudProvider.Aws);
        using var handler = new Handler(Broker(request), Broker(request), "{}", Broker(request, "Active"), Broker(request, "Revoked"));
        using var transport = new MultiCloudJitTransport(new NoAzure(), Configuration(), handler);
        Assert.Equal("Submitted", (await transport.SubmitAsync(Guid.NewGuid(), request, default)).Status);
        var active = await transport.ObserveAsync(Guid.NewGuid(), request, default);
        Assert.Equal("Active", active.Status);
        request = request with { ScheduleId = active.ScheduleId, RevokeId = Guid.NewGuid(), RevokeReason = "End now" };
        Assert.Equal("RevokeOutcomeUnknown", (await transport.RevokeAsync(Guid.NewGuid(), request, default)).Status);
        Assert.Equal("Active", (await transport.ObserveAsync(Guid.NewGuid(), request, default)).Status);
        Assert.Equal("Revoked", (await transport.ObserveAsync(Guid.NewGuid(), request, default)).Status);
        Assert.Equal(new[] { HttpMethod.Put, HttpMethod.Get, HttpMethod.Put, HttpMethod.Get, HttpMethod.Get }, handler.Methods);
        Assert.EndsWith($"/grants/{request.RequestId:D}/revocations/{request.RevokeId:D}", handler.Uris[2]);
        using var payload = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal(request.RequestedBy, payload.RootElement.GetProperty("requesterObjectId").GetGuid());
        Assert.Equal(request.ExpiresAtUtc, payload.RootElement.GetProperty("expiresAtUtc").GetDateTimeOffset());
        Assert.True(payload.RootElement.GetProperty("humanSessionRequired").GetBoolean());
        Assert.True(payload.RootElement.GetProperty("requesterOwnedHandoffRequired").GetBoolean());
        Assert.DoesNotContain("AccessKey", string.Join("", handler.Bodies), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("other-principal")]
    [InlineData("other-role")]
    [InlineData("other-id")]
    [InlineData("extended-end")]
    [InlineData("missing-native-expiry")]
    [InlineData("missing-revocation")]
    [InlineData("missing-handoff")]
    public async Task Aws_NeverTrustsMismatchedOrUnboundedBrokerEvidence(string scenario)
    {
        var request = Request(CloudProvider.Aws);
        var body = BrokerFields(request);
        if (scenario == "other-principal") body["requesterObjectId"] = Guid.NewGuid().ToString("D");
        if (scenario == "other-role") body["role"] = "other";
        if (scenario == "other-id") body["grantId"] = Guid.NewGuid().ToString("D");
        if (scenario == "extended-end") body["expiresAtUtc"] = request.ExpiresAtUtc!.Value.AddMinutes(1);
        if (scenario == "missing-native-expiry") body.Remove("nativeExpirationGuaranteed");
        if (scenario == "missing-revocation") body.Remove("perGrantRevocationGuaranteed");
        if (scenario == "missing-handoff") body.Remove("requesterOwnedHandoff");
        using var handler = new Handler(JsonSerializer.Serialize(body));
        using var transport = new MultiCloudJitTransport(new NoAzure(), Configuration(), handler);
        Assert.Equal("OutcomeUnknown", (await transport.ObserveAsync(Guid.NewGuid(), request, default)).Status);
        Assert.Equal(HttpMethod.Get, Assert.Single(handler.Methods));
    }

    [Fact]
    public async Task Gcp_UsesNativePamWithVerifiedMappedHumanAndExactEntitlementThenGetOnlyRevokeEvidence()
    {
        var request = Request(CloudProvider.Gcp);
        var grant = request.Scope + "/grants/native-id";
        using var handler = new Handler(
            """{"email":"human@example.invalid","email_verified":true}""",
            JsonSerializer.Serialize(new { name = request.Scope, privilegedAccess = Privilege(request) }),
            JsonSerializer.Serialize(new { name = grant, requester = "human@example.invalid", state = "APPROVAL_AWAITED" }),
            Gcp(request, grant, "APPROVAL_AWAITED"), Gcp(request, grant, "ACTIVE"), "{}",
            Gcp(request, grant, "REVOKING"), Gcp(request, grant, "REVOKED"));
        using var transport = new MultiCloudJitTransport(new NoAzure(), Configuration(), handler);
        var submitted = await transport.SubmitAsync(Guid.NewGuid(), request, default);
        Assert.Equal("Submitted", submitted.Status);
        Assert.Equal(grant, submitted.ScheduleId);
        using var payload = JsonDocument.Parse(handler.Bodies[2]);
        Assert.Equal("3600s", payload.RootElement.GetProperty("requestedDuration").GetString());
        Assert.False(payload.RootElement.TryGetProperty("requester", out _));
        Assert.Contains($"requestId={request.RequestId:D}", handler.Uris[2]);
        request = request with { ScheduleId = grant };
        Assert.Equal("native_approval_required", (await transport.ObserveAsync(Guid.NewGuid(), request, default)).FailureCode);
        var active = await transport.ObserveAsync(Guid.NewGuid(), request, default);
        Assert.Equal("Active", active.Status);
        Assert.Equal(TimeSpan.FromMinutes(60), active.ExpiresAtUtc - active.StartsAtUtc);
        request = request with { RevokeId = Guid.NewGuid(), RevokeReason = "Security ends" };
        Assert.Equal("RevokeOutcomeUnknown", (await transport.RevokeAsync(Guid.NewGuid(), request, default)).Status);
        Assert.Equal("Submitted", (await transport.ObserveAsync(Guid.NewGuid(), request, default)).Status);
        Assert.Equal("Revoked", (await transport.ObserveAsync(Guid.NewGuid(), request, default)).Status);
        Assert.EndsWith("/grants/native-id:revoke", handler.Uris[5]);
        Assert.Equal(new[] { HttpMethod.Get, HttpMethod.Get, HttpMethod.Post, HttpMethod.Get, HttpMethod.Get,
            HttpMethod.Post, HttpMethod.Get, HttpMethod.Get }, handler.Methods);
    }

    [Theory]
    [InlineData("other-human", "pam_native_human_token_mismatch")]
    [InlineData("other-entitlement", "pam_entitlement_privilege_mismatch")]
    public async Task Gcp_NativeIdentityOrPrivilegeMismatchNeverCreatesGrant(string scenario, string failure)
    {
        var request = Request(CloudProvider.Gcp);
        using var handler = new Handler(JsonSerializer.Serialize(new { email = scenario == "other-human" ? "other@example.invalid" : "human@example.invalid", email_verified = true }),
            JsonSerializer.Serialize(new { name = "projects/other/locations/global/entitlements/other", privilegedAccess = Privilege(request) }));
        using var transport = new MultiCloudJitTransport(new NoAzure(), Configuration(), handler);
        Assert.Equal(failure, (await transport.SubmitAsync(Guid.NewGuid(), request, default)).FailureCode);
        Assert.All(handler.Methods, method => Assert.Equal(HttpMethod.Get, method));
    }

    [Fact]
    public async Task Gcp_UnknownCreateHasNoSafeGrantCorrelationAndCannotManufactureRevokeSuccess()
    {
        var request = Request(CloudProvider.Gcp) with { Status = "OutcomeUnknown", RevokeId = Guid.NewGuid() };
        using var handler = new Handler();
        using var transport = new MultiCloudJitTransport(new NoAzure(), Configuration(), handler);
        Assert.Equal("OutcomeUnknown", (await transport.ObserveAsync(Guid.NewGuid(), request, default)).Status);
        await Assert.ThrowsAsync<ArgumentException>(() => transport.RevokeAsync(Guid.NewGuid(), request, default));
        Assert.Empty(handler.Methods);
    }

    [Theory]
    [InlineData("other-human")]
    [InlineData("extended-duration")]
    [InlineData("other-role")]
    [InlineData("externally-modified")]
    public async Task Gcp_MismatchedNativeEvidenceIsNeverActiveOrRevoked(string scenario)
    {
        var request = Request(CloudProvider.Gcp) with { ScheduleId = "projects/client/locations/global/entitlements/investigation/grants/native-id" };
        using var handler = new Handler(JsonSerializer.Serialize(new { name = request.ScheduleId,
            requester = scenario == "other-human" ? "other@example.invalid" : "human@example.invalid", state = "ACTIVE",
            requestedDuration = scenario == "extended-duration" ? "7200s" : "3600s",
            externallyModified = scenario == "externally-modified",
            auditTrail = new { accessGrantTime = request.StartsAtUtc },
            privilegedAccess = Privilege(scenario == "other-role" ? request with { RoleDefinitionId = "roles/owner" } : request) }));
        using var transport = new MultiCloudJitTransport(new NoAzure(), Configuration(), handler);
        Assert.Equal("OutcomeUnknown", (await transport.ObserveAsync(Guid.NewGuid(), request, default)).Status);
        Assert.Equal(HttpMethod.Get, Assert.Single(handler.Methods));
    }

    private static object Privilege(PrivilegedAccessRequest request) => new { gcpIamAccess = new {
        resource = "//cloudresourcemanager.googleapis.com/projects/client", roleBindings = new[] { new { role = request.RoleDefinitionId } } } };
    private static string Gcp(PrivilegedAccessRequest request, string grant, string state) => JsonSerializer.Serialize(new {
        name = grant, requester = "human@example.invalid", state, requestedDuration = "3600s",
        auditTrail = new { accessGrantTime = request.StartsAtUtc }, privilegedAccess = Privilege(request) });
    private static Dictionary<string, object?> BrokerFields(PrivilegedAccessRequest request) => new() {
        ["grantId"] = request.RequestId.ToString("D"), ["requesterObjectId"] = request.RequestedBy.ToString("D"),
        ["accountId"] = request.SubscriptionId, ["role"] = request.RoleDefinitionId, ["status"] = "Active",
        ["startsAtUtc"] = request.StartsAtUtc, ["expiresAtUtc"] = request.ExpiresAtUtc,
        ["nativeExpirationGuaranteed"] = true, ["perGrantRevocationGuaranteed"] = true, ["requesterOwnedHandoff"] = true };
    private static string Broker(PrivilegedAccessRequest request, string status = "Active")
    { var fields = BrokerFields(request); fields["status"] = status; return JsonSerializer.Serialize(fields); }
    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
        ["ClientSecrets:Human"] = "fake-human-token", ["ClientSecrets:Revoke"] = "fake-revoker-token" }).Build();
    private static PrivilegedAccessRequest Request(CloudProvider provider)
    {
        var now = DateTimeOffset.UtcNow.AddSeconds(-1);
        return new(Guid.NewGuid(), Guid.NewGuid(), "investigation", provider == CloudProvider.Aws ? "123456789012" : "client",
            provider == CloudProvider.Gcp ? "projects/client/locations/global/entitlements/investigation" : "123456789012",
            provider == CloudProvider.Gcp ? "roles/viewer" : "arn:aws:iam::123456789012:role/Investigator",
            60, false, "Investigate", now, now.AddMinutes(30), "Issuing", 3, [Guid.NewGuid(), Guid.NewGuid()], now, now.AddMinutes(60),
            Provider: provider, HumanPrincipal: "user:human@example.invalid", BrokerEndpoint: "https://broker.example.invalid",
            AuthSecretKey: "ClientSecrets:Human", RevocationAuthSecretKey: "ClientSecrets:Revoke");
    }
    private sealed class NoAzure : IAzurePimTransport
    {
        public Task<PimObservation> SubmitAsync(Guid tenant, PrivilegedAccessRequest request, CancellationToken token) => throw new InvalidOperationException("No Azure calls.");
        public Task<PimObservation> ObserveAsync(Guid tenant, PrivilegedAccessRequest request, CancellationToken token) => throw new InvalidOperationException("No Azure calls.");
    }
    private sealed class Handler(params string[] responses) : HttpMessageHandler
    {
        public List<HttpMethod> Methods { get; } = [];
        public List<string> Uris { get; } = [];
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Methods.Add(request.Method); Uris.Add(request.RequestUri!.AbsoluteUri);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(token));
            Assert.True(responses.Length >= Methods.Count, "Unexpected external request.");
            return new(HttpStatusCode.OK) { Content = new StringContent(responses[Methods.Count - 1]) };
        }
    }
}
