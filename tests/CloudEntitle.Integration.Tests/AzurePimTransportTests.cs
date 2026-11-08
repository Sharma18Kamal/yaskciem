using System.Net;
using System.Text.Json;
using Azure.Core;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.LicenseServer.Services;
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed class AzurePimTransportTests
{
    [Fact]
    public async Task Pim_SubmitsFixedExpiryForExactApprovedPrincipalThenReconcilesBeforeActive()
    {
        var tenant = Guid.NewGuid();
        var request = Request();
        var scheduleId = Guid.NewGuid();
        var schedule = $"{request.Scope}/providers/Microsoft.Authorization/roleAssignmentSchedules/{scheduleId:D}";
        using var handler = new Handler(
            JsonSerializer.Serialize(new { tenantId = tenant }), "{}",
            JsonSerializer.Serialize(new { id = RequestId(request), properties = new { principalId = request.RequestedBy, roleDefinitionId = request.RoleDefinitionId,
                status = "Granted", targetRoleAssignmentScheduleId = scheduleId } }),
            JsonSerializer.Serialize(new { id = schedule, properties = new { principalId = request.RequestedBy, roleDefinitionId = request.RoleDefinitionId,
                status = "Provisioned", startDateTime = request.StartsAtUtc, endDateTime = request.ExpiresAtUtc } }));
        using var transport = new AzurePimTransport(handler, new Credential());
        Assert.Equal("Submitted", (await transport.SubmitAsync(tenant, request, default)).Status);
        using var payload = JsonDocument.Parse(handler.Bodies[1]);
        var properties = payload.RootElement.GetProperty("properties");
        Assert.Equal("AdminAssign", properties.GetProperty("requestType").GetString());
        Assert.Equal(request.RequestedBy, properties.GetProperty("principalId").GetGuid());
        var expiration = properties.GetProperty("scheduleInfo").GetProperty("expiration");
        Assert.Equal("AfterDateTime", expiration.GetProperty("type").GetString());
        Assert.Equal(request.ExpiresAtUtc, expiration.GetProperty("endDateTime").GetDateTimeOffset());
        var observed = await transport.ObserveAsync(tenant, request, default);
        Assert.Equal("Submitted", observed.Status);
        Assert.Equal(schedule, observed.ScheduleId);
        Assert.Equal("Active", (await transport.ObserveAsync(tenant, request with { ScheduleId = schedule }, default)).Status);
        Assert.Equal(new[] { HttpMethod.Get, HttpMethod.Put, HttpMethod.Get, HttpMethod.Get }, handler.Methods);
    }
    [Fact]
    public async Task Pim_ForeignTenantNeverWritesAndElapsedLocalTimeDoesNotProveRevocation()
    {
        var request = Request();
        using var foreign = new Handler(JsonSerializer.Serialize(new { tenantId = Guid.NewGuid() }));
        using var transport = new AzurePimTransport(foreign, new Credential());
        Assert.Equal("subscription_tenant_mismatch", (await transport.SubmitAsync(Guid.NewGuid(), request, default)).FailureCode);
        Assert.Equal(HttpMethod.Get, Assert.Single(foreign.Methods));
        request = request with { StartsAtUtc = DateTimeOffset.UtcNow.AddMinutes(-60), ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            ScheduleId = $"{request.Scope}/providers/Microsoft.Authorization/roleAssignmentSchedules/{Guid.NewGuid():D}" };
        using var elapsed = new Handler(JsonSerializer.Serialize(new { id = request.ScheduleId, properties = new { principalId = request.RequestedBy,
            roleDefinitionId = request.RoleDefinitionId, status = "Provisioned", endDateTime = request.ExpiresAtUtc } }),
            JsonSerializer.Serialize(new { id = request.ScheduleId, properties = new { principalId = request.RequestedBy, roleDefinitionId = request.RoleDefinitionId, status = "Revoked" } }));
        using var observer = new AzurePimTransport(elapsed, new Credential());
        Assert.Equal("ExpiryPending", (await observer.ObserveAsync(Guid.NewGuid(), request, default)).Status);
        Assert.Equal("Revoked", (await observer.ObserveAsync(Guid.NewGuid(), request, default)).Status);
    }
    [Theory]
    [InlineData("extended-end", "pim_expiration_mismatch")]
    [InlineData("missing-end", "pim_expiration_mismatch")]
    [InlineData("end-before-start", "pim_expiration_mismatch")]
    [InlineData("early-start", "pim_start_mismatch")]
    [InlineData("other-principal", "pim_identity_mismatch")]
    [InlineData("other-role", "pim_identity_mismatch")]
    [InlineData("other-scope", "pim_scope_mismatch")]
    public async Task Pim_NeverMarksMismatchedOrUnboundedSchedulesActive(string scenario, string failure)
    {
        var request = Request();
        var schedule = $"{request.Scope}/providers/Microsoft.Authorization/roleAssignmentSchedules/{Guid.NewGuid():D}";
        request = request with { ScheduleId = schedule };
        var properties = new Dictionary<string, object?>
        {
            ["principalId"] = request.RequestedBy, ["roleDefinitionId"] = request.RoleDefinitionId, ["status"] = "Provisioned",
            ["startDateTime"] = request.StartsAtUtc, ["endDateTime"] = request.ExpiresAtUtc
        };
        if (scenario == "extended-end") properties["endDateTime"] = request.ExpiresAtUtc!.Value.AddMinutes(1);
        if (scenario == "missing-end") properties.Remove("endDateTime");
        if (scenario == "end-before-start") properties["endDateTime"] = request.StartsAtUtc;
        if (scenario == "early-start") properties["startDateTime"] = request.StartsAtUtc!.Value.AddMinutes(-1);
        if (scenario == "other-principal") properties["principalId"] = Guid.NewGuid();
        if (scenario == "other-role") properties["roleDefinitionId"] = request.RoleDefinitionId.Replace(request.RoleDefinitionId.Split('/').Last(), Guid.NewGuid().ToString("D"));
        using var handler = new Handler(JsonSerializer.Serialize(new { id = scenario == "other-scope" ? "/subscriptions/other" : schedule, properties }));
        using var transport = new AzurePimTransport(handler, new Credential());
        var result = await transport.ObserveAsync(Guid.NewGuid(), request, default);
        Assert.Equal("OutcomeUnknown", result.Status);
        Assert.Equal(failure, result.FailureCode);
        Assert.Equal(HttpMethod.Get, Assert.Single(handler.Methods));
    }
    [Fact]
    public async Task Pim_ElapsedRequestWithoutScheduleStillRequiresRevocationEvidence()
    {
        var request = Request() with { StartsAtUtc = DateTimeOffset.UtcNow.AddMinutes(-60), ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) };
        using var handler = new Handler(JsonSerializer.Serialize(new { id = RequestId(request),
            properties = new { principalId = request.RequestedBy, roleDefinitionId = request.RoleDefinitionId, status = "Provisioned" } }));
        using var transport = new AzurePimTransport(handler, new Credential());
        Assert.Equal("ExpiryPending", (await transport.ObserveAsync(Guid.NewGuid(), request, default)).Status);
    }
    [Fact]
    public async Task Pim_EarlyRemoveWritesOnlyExactScheduleAndSeparateDurableIdAcceptanceNotRevoked()
    {
        var request = Request();
        request = request with { ScheduleId = $"{request.Scope}/providers/Microsoft.Authorization/roleAssignmentSchedules/{Guid.NewGuid():D}",
            RevokeId = Guid.NewGuid(), RevokeReason = "Security ends access", Status = "RevokeIssuing" };
        using var handler = new Handler("{}");
        using var transport = new AzurePimTransport(handler, new Credential());
        Assert.Equal("RevokeOutcomeUnknown", (await transport.RevokeAsync(Guid.NewGuid(), request, default)).Status);
        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        var properties = body.RootElement.GetProperty("properties");
        Assert.Equal("AdminRemove", properties.GetProperty("requestType").GetString());
        Assert.Equal(request.ScheduleId.Split('/').Last(), properties.GetProperty("targetRoleAssignmentScheduleId").GetString());
        Assert.Equal(request.RequestedBy, properties.GetProperty("principalId").GetGuid());
        Assert.Equal(request.RoleDefinitionId, properties.GetProperty("roleDefinitionId").GetString());
        Assert.Equal(HttpMethod.Put, Assert.Single(handler.Methods));
        await Assert.ThrowsAsync<ArgumentException>(() => transport.RevokeAsync(Guid.NewGuid(),
            request with { ScheduleId = "/subscriptions/other/providers/Microsoft.Authorization/roleAssignmentSchedules/" + Guid.NewGuid() }, default));
        Assert.Single(handler.Methods);
    }

    private static PrivilegedAccessRequest Request()
    {
        var subscription = Guid.NewGuid().ToString("D");
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), Guid.NewGuid(), "target", subscription, $"/subscriptions/{subscription}",
            $"/subscriptions/{subscription}/providers/Microsoft.Authorization/roleDefinitions/{Guid.NewGuid():D}",
            60, false, "Temporary investigation", now, now.AddMinutes(30), "Issuing", 3, [Guid.NewGuid(), Guid.NewGuid()], now, now.AddMinutes(60));
    }
    private static string RequestId(PrivilegedAccessRequest request)
        => $"{request.Scope}/providers/Microsoft.Authorization/roleAssignmentScheduleRequests/{request.RequestId:D}";
    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Assert.Equal("https://management.azure.com/.default", Assert.Single(requestContext.Scopes));
            return new("test-token", DateTimeOffset.UtcNow.AddHours(1));
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
    private sealed class Handler(params string[] responses) : HttpMessageHandler
    {
        public List<HttpMethod> Methods { get; } = [];
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("management.azure.com", request.RequestUri!.Host);
            Methods.Add(request.Method);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return new(HttpStatusCode.OK) { Content = new StringContent(responses[Methods.Count - 1]) };
        }
    }
}
