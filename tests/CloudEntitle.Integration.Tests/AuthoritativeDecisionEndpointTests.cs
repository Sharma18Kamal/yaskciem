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
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed partial class SharedWorkspaceEndpointTests
{
    [PostgreSqlFact]
    public async Task AuthoritativeDecisionEndpointsCaptureSavedNativeDecisionsAndDeduplicateConfirmedAnalysis()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        var tenant = Guid.NewGuid();
        var owner = new OrganizationIdentity(Guid.NewGuid(), "owner", tenant.ToString(), "owner@example.invalid");
        var viewer = new OrganizationIdentity(Guid.NewGuid(), "viewer", tenant.ToString(), "viewer@example.invalid");
        var organizations = new OrganizationManagementService(store.CreateOrganizationStore(tenant));
        var organization = organizations.CreateOrganization("Native decisions", "native-decisions", tenant.ToString(), owner);
        var source = AuthoritativeDecisionTests.Connection(tenant, organization.Id);
        organizations.BindCloudAccount(organization.Id, owner, CloudProvider.Gcp, Guid.NewGuid(), source.AccountId, "GCP");
        organizations.AddMember(organization.Id, owner, viewer.Email, OrganizationMemberRole.Viewer);
        organizations.AcceptInvitation(organization.Id, viewer);
        var nativeHandler = new AuthoritativeDecisionTests.NativeHandler(source);
        using var nativeClient = new HttpClient(nativeHandler);
        var collector = new GcpAuthoritativeDecisionCollector(new AuthoritativeDecisionTests.SingleClientFactory(nativeClient), _ => "opaque-test-token");
        var service = new AuthoritativeDecisionService(store, collector, [source]);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IEffectiveDecisionService>(service);
        builder.Services.AddAuthentication(TestAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("CiemApiAccess", policy => policy
            .RequireAuthenticatedUser().RequireClaim("tid").RequireClaim("oid")
            .RequireAssertion(context => context.User.FindFirst("scp")?.Value.Split(' ').Contains("access_as_user") == true)));
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapAuthoritativeDecisionEndpoints(store);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var prefix = $"/api/v1/organizations/{organization.Id:D}/effective-decisions";
        async Task<HttpResponseMessage> Send(HttpMethod method, string path, OrganizationIdentity actor, object? body = null)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = JsonContent.Create(body);
            SetIdentityHeaders(request, tenant, actor.UserId, actor.Email);
            return await client.SendAsync(request);
        }
        using (var sources = await Send(HttpMethod.Get, prefix + "/sources", owner))
            Assert.Equal(HttpStatusCode.OK, sources.StatusCode);
        Assert.Empty(nativeHandler.Requests);
        using (var denied = await Send(HttpMethod.Post, prefix + "/capture", viewer, AuthoritativeDecisionTests.Request(source)))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var noConsent = await Send(HttpMethod.Post, prefix + "/capture", owner,
                   AuthoritativeDecisionTests.Request(source) with { ReadConsent = false }))
            Assert.Equal(HttpStatusCode.BadRequest, noConsent.StatusCode);
        using (var wrongTuple = await Send(HttpMethod.Post, prefix + "/capture", owner,
                   AuthoritativeDecisionTests.Request(source) with { FullResourceName = source.FullResourceName + "/invented" }))
            Assert.Equal(HttpStatusCode.BadRequest, wrongTuple.StatusCode);
        Assert.Empty(nativeHandler.Requests);

        async Task<EffectiveDecisionSnapshot> Capture(bool spoof = false)
        {
            object body = AuthoritativeDecisionTests.Request(source);
            if (spoof) body = new
            {
                sourceId = source.SourceId, principal = source.Principals[0], permission = source.PrivilegedPermissions[0],
                fullResourceName = source.FullResourceName, readConsent = true, authoritative = true,
                decision = "GRANTED", coverage = "CompleteVerified", nativeResponseJson = "{}"
            };
            using var response = await Send(HttpMethod.Post, prefix + "/capture", owner, body);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.NotNull(response.Headers.Location);
            return (await response.Content.ReadFromJsonAsync<ApiEnvelope<EffectiveDecisionSnapshot>>())!.Data;
        }
        var before = await Capture(true);
        var after = await Capture();
        Assert.Equal("NOT_GRANTED", before.Decision);
        Assert.Equal("GRANTED", after.Decision);
        Assert.Equal(2, nativeHandler.NativeReads);
        var pair = new AnalyzeDecisionEscalationRequest(source.SourceId, before.SnapshotId, after.SnapshotId);
        Guid? analysisId = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await Send(HttpMethod.Post, prefix + "/analyze", owner, pair);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var report = (await response.Content.ReadFromJsonAsync<ApiEnvelope<DecisionEscalationReport>>())!.Data;
            Assert.Equal("ConfirmedDecisionEscalation", report.Status);
            if (analysisId is not null) Assert.Equal(analysisId, report.AnalysisId);
            analysisId = report.AnalysisId;
        }
        Assert.Equal(2, nativeHandler.NativeReads);
        var drift = await store.GetDriftEventsAsync(tenant, organization.Id, 50);
        Assert.Single(drift, item => item.EventType == "privilege-escalation.confirmeddecision");
        using (var history = await Send(HttpMethod.Get, prefix + "?sourceId=native-gcp", viewer))
        {
            Assert.Equal(HttpStatusCode.OK, history.StatusCode);
            Assert.Equal(2, (await history.Content.ReadFromJsonAsync<ApiEnvelope<IReadOnlyList<EffectiveDecisionSnapshot>>>())!.Data.Count);
            Assert.DoesNotContain("nativeResponseJson", await history.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("opaque-test-token", await history.Content.ReadAsStringAsync());
        }
        using (var saved = await Send(HttpMethod.Get, prefix + $"/analyses/{analysisId:D}?sourceId=native-gcp", viewer))
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using (var deniedAnalysis = await Send(HttpMethod.Post, prefix + "/analyze", viewer, pair))
            Assert.Equal(HttpStatusCode.Forbidden, deniedAnalysis.StatusCode);
        using (var upload = await Send(HttpMethod.Post, prefix, owner, after))
            Assert.Equal(HttpStatusCode.MethodNotAllowed, upload.StatusCode);
        using (var missing = await Send(HttpMethod.Get, prefix + $"/{Guid.NewGuid():D}?sourceId=native-gcp", owner))
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using (var reversed = await Send(HttpMethod.Post, prefix + "/analyze", owner,
                   pair with { BeforeSnapshotId = after.SnapshotId, AfterSnapshotId = before.SnapshotId }))
        {
            Assert.Equal(HttpStatusCode.Created, reversed.StatusCode);
            var report = (await reversed.Content.ReadFromJsonAsync<ApiEnvelope<DecisionEscalationReport>>())!.Data;
            Assert.NotEqual("ConfirmedDecisionEscalation", report.Status);
        }
        Assert.Equal(2, nativeHandler.NativeReads);

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        foreach (var sql in new[]
                 {
                     "UPDATE SharedEffectiveDecisionSnapshots SET PayloadJson='{}'",
                     "DELETE FROM SharedEffectiveDecisionSnapshots",
                     "TRUNCATE SharedEffectiveDecisionSnapshots CASCADE",
                     "UPDATE SharedDecisionEscalationAnalyses SET PayloadJson='{}'",
                     "DELETE FROM SharedDecisionEscalationAnalyses",
                     "TRUNCATE SharedDecisionEscalationAnalyses"
                 })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        }
    }
}
