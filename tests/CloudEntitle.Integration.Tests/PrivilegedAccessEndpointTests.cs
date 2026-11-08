using System.Net;
using System.Net.Http.Json;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.LicenseServer.Services;
using CloudEntitle.OrganizationManagement.Models;
using CloudEntitle.OrganizationManagement.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed partial class SharedWorkspaceEndpointTests
{
    [PostgreSqlFact]
    public async Task PrivilegedAccess_RequiresTwoDistinctApproversConsentAndFreshAuthorizationWithDurableAudit()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        await store.InitializeAsync();
        Assert.True(await store.IsReadyAsync());
        var tenant = Guid.NewGuid();
        var requester = new OrganizationIdentity(Guid.NewGuid(), "requester", tenant.ToString("D"), "requester@example.invalid");
        var manager = new OrganizationIdentity(Guid.NewGuid(), "manager", tenant.ToString("D"), "manager@example.invalid");
        var security = new OrganizationIdentity(Guid.NewGuid(), "security", tenant.ToString("D"), "security@example.invalid");
        var service = new OrganizationManagementService(store.CreateOrganizationStore(tenant));
        var org = service.CreateOrganization("JIT", "jit", tenant.ToString("D"), manager);
        service.AddMember(org.Id, manager, security.Email, OrganizationMemberRole.OrganizationAdmin);
        service.AcceptInvitation(org.Id, security);
        service.AddMember(org.Id, manager, requester.Email, OrganizationMemberRole.Viewer);
        service.AcceptInvitation(org.Id, requester);
        var subscription = Guid.NewGuid().ToString("D");
        service.BindCloudAccount(org.Id, manager, CloudProvider.Azure, Guid.NewGuid(), subscription, "Client Azure");
        var root = $"PrivilegedAccess:Organizations:{tenant:D}:{org.Id:D}";
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{root}:TenantId"] = tenant.ToString("D"), [$"{root}:Enabled"] = "true",
            [$"{root}:ApproverIds:0"] = manager.UserId.ToString("D"), [$"{root}:ApproverIds:1"] = security.UserId.ToString("D"),
            [$"{root}:Targets:investigation:SubscriptionId"] = subscription,
            [$"{root}:Targets:investigation:Scope"] = $"/subscriptions/{subscription}",
            [$"{root}:Targets:investigation:RoleDefinitionId"] = $"/subscriptions/{subscription}/providers/Microsoft.Authorization/roleDefinitions/{Guid.NewGuid():D}",
            [$"{root}:Targets:investigation:Connected"] = "true"
        });
        builder.Services.AddAuthentication(TestAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("CiemApiAccess", policy => policy.RequireAuthenticatedUser()));
        builder.Services.AddSingleton(store);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapSharedWorkspaceEndpoints(store);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var prefix = $"/api/v1/organizations/{org.Id:D}/privileged-access";
        async Task<HttpResponseMessage> Send(string path, OrganizationIdentity actor, object body, bool delegated = true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            SetIdentityHeaders(request, tenant, actor.UserId, actor.Email);
            if (!delegated) request.Headers.Add("X-Test-Scope", "unrelated_scope");
            return await client.SendAsync(request);
        }
        using var noDelegation = await Send(prefix, requester, new CreatePrivilegedAccessRequest("investigation", 60, false, "No delegation"), false);
        Assert.Equal(HttpStatusCode.Forbidden, noDelegation.StatusCode);
        using var emergencyInvalid = await Send(prefix, requester, new CreatePrivilegedAccessRequest("investigation", 60, true, "Emergency"));
        Assert.Equal(HttpStatusCode.BadRequest, emergencyInvalid.StatusCode);
        using var approverSelfRequest = await Send(prefix, manager, new CreatePrivilegedAccessRequest("investigation", 60, false, "Self"));
        Assert.Equal(HttpStatusCode.BadRequest, approverSelfRequest.StatusCode);
        using var created = await Send(prefix, requester, new CreatePrivilegedAccessRequest("investigation", 60, false, "Investigation"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var current = (await created.Content.ReadFromJsonAsync<ApiEnvelope<PrivilegedAccessRequest>>())!.Data;
        Assert.Equal(requester.UserId, current.RequestedBy);
        var path = prefix + $"/{current.RequestId:D}";
        using var self = await Send(path + "/approve", requester, new PrivilegedAccessDecision(0, "Self"));
        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
        using var early = await Send(path + "/activate", requester, new PrivilegedAccessDecision(0, "Too early", true));
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        using var noDelegatedApproval = await Send(path + "/approve", manager, new PrivilegedAccessDecision(0, "No delegation"), false);
        Assert.Equal(HttpStatusCode.Forbidden, noDelegatedApproval.StatusCode);
        using var first = await Send(path + "/approve", manager, new PrivilegedAccessDecision(0, "Manager approval"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var duplicate = await Send(path + "/approve", manager, new PrivilegedAccessDecision(1, "Duplicate"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var stale = await Send(path + "/approve", security, new PrivilegedAccessDecision(0, "Stale"));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var second = await Send(path + "/approve", security, new PrivilegedAccessDecision(1, "Security approval"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var noConsent = await Send(path + "/activate", requester, new PrivilegedAccessDecision(2, "No consent"));
        Assert.Equal(HttpStatusCode.Conflict, noConsent.StatusCode);
        using var otherActivator = await Send(path + "/activate", security, new PrivilegedAccessDecision(2, "Other actor", true));
        Assert.Equal(HttpStatusCode.Forbidden, otherActivator.StatusCode);
        using var noDelegatedActivation = await Send(path + "/activate", requester, new PrivilegedAccessDecision(2, "No delegation", true), false);
        Assert.Equal(HttpStatusCode.Forbidden, noDelegatedActivation.StatusCode);
        using var activated = await Send(path + "/activate", requester, new PrivilegedAccessDecision(2, "Explicit consent", true));
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        var transport = new TestPimTransport();
        var worker = new PrivilegedAccessWorker(store, builder.Configuration, transport, NullLogger<PrivilegedAccessWorker>.Instance);
        await worker.ProcessOnceAsync(default);
        Assert.Equal(1, transport.Submissions);
        current = (await store.GetPrivilegedAccessAsync(tenant, org.Id, current.RequestId))!;
        Assert.Equal("Submitted", current.Status);
        Assert.Equal(TimeSpan.FromMinutes(60), current.ExpiresAtUtc - current.StartsAtUtc);
        await worker.ProcessOnceAsync(default);
        Assert.Equal(1, transport.Submissions);
        Assert.Equal(1, transport.Reads);
        Assert.Equal("OutcomeUnknown", (await store.GetPrivilegedAccessAsync(tenant, org.Id, current.RequestId))!.Status);
        var audit = await store.GetPrivilegedAccessAuditAsync(tenant, org.Id, current.RequestId);
        Assert.Contains(audit, item => item.ActorId == manager.UserId && item.Action == "approve");
        Assert.Contains(audit, item => item.ActorId == security.UserId && item.Action == "approve");
        Assert.Empty(await store.GetPrivilegedAccessAsync(Guid.NewGuid(), org.Id));
        using var emergency = await Send(prefix, requester, new CreatePrivilegedAccessRequest("investigation", 10, true, "Emergency access"));
        var emergencyRequest = (await emergency.Content.ReadFromJsonAsync<ApiEnvelope<PrivilegedAccessRequest>>())!.Data;
        var emergencyPath = prefix + $"/{emergencyRequest.RequestId:D}";
        using var emergencyManager = await Send(emergencyPath + "/approve", manager, new PrivilegedAccessDecision(0, "Manager emergency approval"));
        Assert.Equal(HttpStatusCode.OK, emergencyManager.StatusCode);
        using var emergencyEarly = await Send(emergencyPath + "/activate", requester, new PrivilegedAccessDecision(1, "Only one approval", true));
        Assert.Equal(HttpStatusCode.Conflict, emergencyEarly.StatusCode);
        using var emergencySecurity = await Send(emergencyPath + "/approve", security, new PrivilegedAccessDecision(1, "Security emergency approval"));
        Assert.Equal(HttpStatusCode.OK, emergencySecurity.StatusCode);
        using var emergencyActivate = await Send(emergencyPath + "/activate", requester, new PrivilegedAccessDecision(2, "Emergency consent", true));
        Assert.Equal(HttpStatusCode.OK, emergencyActivate.StatusCode);
        builder.Configuration[$"{root}:Targets:investigation:Connected"] = "false";
        await worker.ProcessOnceAsync(default);
        Assert.Equal(1, transport.Submissions);
        Assert.Equal("Failed", (await store.GetPrivilegedAccessAsync(tenant, org.Id, emergencyRequest.RequestId))!.Status);
        Assert.Equal("authorization_or_configuration_changed", (await store.GetPrivilegedAccessAsync(tenant, org.Id, emergencyRequest.RequestId))!.FailureCode);
        builder.Configuration[$"{root}:Targets:investigation:Connected"] = "true";
        using var malformedCreated = await Send(prefix, requester, new CreatePrivilegedAccessRequest("investigation", 10, true, "Configuration-change test"));
        var malformed = (await malformedCreated.Content.ReadFromJsonAsync<ApiEnvelope<PrivilegedAccessRequest>>())!.Data;
        var malformedPath = prefix + $"/{malformed.RequestId:D}";
        using var malformedManager = await Send(malformedPath + "/approve", manager, new PrivilegedAccessDecision(0, "Manager approval"));
        Assert.Equal(HttpStatusCode.OK, malformedManager.StatusCode);
        using var malformedSecurity = await Send(malformedPath + "/approve", security, new PrivilegedAccessDecision(1, "Security approval"));
        Assert.Equal(HttpStatusCode.OK, malformedSecurity.StatusCode);
        using var malformedActivation = await Send(malformedPath + "/activate", requester, new PrivilegedAccessDecision(2, "Consent", true));
        Assert.Equal(HttpStatusCode.OK, malformedActivation.StatusCode);
        builder.Configuration[$"{root}:ApproverIds:1"] = "invalid-object-id";
        await worker.ProcessOnceAsync(default);
        Assert.Equal(1, transport.Submissions);
        Assert.Equal("Failed", (await store.GetPrivilegedAccessAsync(tenant, org.Id, malformed.RequestId))!.Status);
        builder.Configuration[$"{root}:ApproverIds:1"] = security.UserId.ToString("D");
        using var cancellableResponse = await Send(prefix, requester, new CreatePrivilegedAccessRequest("investigation", 10, true, "Cancel before activation"));
        var cancellable = (await cancellableResponse.Content.ReadFromJsonAsync<ApiEnvelope<PrivilegedAccessRequest>>())!.Data;
        var cancelPath = prefix + $"/{cancellable.RequestId:D}/revoke";
        using var cancelNoConsent = await Send(cancelPath, requester, new PrivilegedAccessDecision(0, "Cancel"));
        Assert.Equal(HttpStatusCode.Conflict, cancelNoConsent.StatusCode);
        using var cancelNoDelegation = await Send(cancelPath, requester, new PrivilegedAccessDecision(0, "Cancel", true), false);
        Assert.Equal(HttpStatusCode.Forbidden, cancelNoDelegation.StatusCode);
        using var cancelledResponse = await Send(cancelPath, requester, new PrivilegedAccessDecision(0, "Cancel own request", true));
        Assert.Equal(HttpStatusCode.OK, cancelledResponse.StatusCode);
        var cancelled = (await cancelledResponse.Content.ReadFromJsonAsync<ApiEnvelope<PrivilegedAccessRequest>>())!.Data;
        Assert.Equal("Cancelled", cancelled.Status);
        Assert.NotNull(cancelled.RevokeId);
        var submissionsBeforeCancelWorker = transport.Submissions;
        await worker.ProcessOnceAsync(default);
        Assert.Equal(submissionsBeforeCancelWorker, transport.Submissions);
        current = (await store.GetPrivilegedAccessAsync(tenant, org.Id, current.RequestId))!;
        var schedule = $"{current.Scope}/providers/Microsoft.Authorization/roleAssignmentSchedules/{Guid.NewGuid():D}";
        current = await store.UpdatePrivilegedAccessAsync(tenant, org.Id, current.RequestId, Guid.Empty, "test-native-active",
            "Inject native evidence fixture", previous => previous! with { Status = "Active", ScheduleId = schedule, Version = previous!.Version + 1 });
        builder.Configuration[$"{root}:Enabled"] = "false";
        builder.Configuration[$"{root}:Targets:investigation:Connected"] = "false";
        service.SetMemberActive(org.Id, manager, service.GetMembers(org.Id, manager).Single(member => member.UserId == requester.UserId).Id, false);
        using var staleRevoke = await Send(path + "/revoke", security, new PrivilegedAccessDecision(current.Version - 1, "Stale", true));
        Assert.Equal(HttpStatusCode.Conflict, staleRevoke.StatusCode);
        using var noReason = await Send(path + "/revoke", security, new PrivilegedAccessDecision(current.Version, "", true));
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        using var revokedResponse = await Send(path + "/revoke", security, new PrivilegedAccessDecision(current.Version, "Security revoke despite disconnect", true));
        Assert.Equal(HttpStatusCode.OK, revokedResponse.StatusCode);
        var queuedRevoke = (await revokedResponse.Content.ReadFromJsonAsync<ApiEnvelope<PrivilegedAccessRequest>>())!.Data;
        Assert.Equal("RevokeQueued", queuedRevoke.Status);
        Assert.Equal(schedule, queuedRevoke.ScheduleId);
        Assert.NotEqual(queuedRevoke.RequestId, queuedRevoke.RevokeId);
        transport.Observation = new("Active", schedule);
        await Task.WhenAll(worker.ProcessOnceAsync(default), worker.ProcessOnceAsync(default));
        Assert.Equal(1, transport.Revocations);
        Assert.Equal(submissionsBeforeCancelWorker, transport.Submissions);
        current = (await store.GetPrivilegedAccessAsync(tenant, org.Id, current.RequestId))!;
        Assert.Equal("RevokeOutcomeUnknown", current.Status);
        await worker.ProcessOnceAsync(default);
        Assert.Equal(1, transport.Revocations);
        Assert.Equal("RevokeOutcomeUnknown", (await store.GetPrivilegedAccessAsync(tenant, org.Id, current.RequestId))!.Status);
        transport.Observation = new("Revoked", schedule);
        await worker.ProcessOnceAsync(default);
        Assert.Equal("Revoked", (await store.GetPrivilegedAccessAsync(tenant, org.Id, current.RequestId))!.Status);
        Assert.Contains(await store.GetPrivilegedAccessAuditAsync(tenant, org.Id, current.RequestId),
            item => item.ActorId == security.UserId && item.Action == "revoke");
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var delete = new NpgsqlCommand("DELETE FROM SharedPrivilegedAccessAudit;", connection);
        await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
    }
    private sealed class TestPimTransport : IAzurePimTransport
    {
        public int Submissions { get; private set; }
        public int Reads { get; private set; }
        public int Revocations { get; private set; }
        public PimObservation Observation { get; set; } = new("OutcomeUnknown", FailureCode: "observation_unavailable");
        public Task<PimObservation> SubmitAsync(Guid tenant, PrivilegedAccessRequest request, CancellationToken token)
        {
            Submissions++;
            Assert.Equal(2, request.ApprovedBy.Distinct().Count());
            Assert.DoesNotContain(request.RequestedBy, request.ApprovedBy);
            return Task.FromResult(new PimObservation("Submitted"));
        }
        public Task<PimObservation> ObserveAsync(Guid tenant, PrivilegedAccessRequest request, CancellationToken token)
        {
            Reads++;
            return Task.FromResult(Observation);
        }
        public Task<PimObservation> RevokeAsync(Guid tenant, PrivilegedAccessRequest request, CancellationToken token)
        {
            Revocations++;
            Assert.Equal("RevokeIssuing", request.Status);
            Assert.NotNull(request.RevokeId);
            Assert.NotNull(request.ScheduleId);
            return Task.FromResult(new PimObservation("RevokeOutcomeUnknown", request.ScheduleId, "accepted_unverified"));
        }
    }
}
