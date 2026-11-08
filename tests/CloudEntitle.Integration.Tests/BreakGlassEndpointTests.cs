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
using Npgsql;
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed partial class SharedWorkspaceEndpointTests
{
    [PostgreSqlFact]
    public async Task BreakGlassReports_RequireBoundScopedIndependentDelegatedReviewAndNeverProveRevocation()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        await store.InitializeAsync();
        Assert.True(await store.IsReadyAsync());
        var tenant = Guid.NewGuid();
        var reporter = new OrganizationIdentity(Guid.NewGuid(), "reporter", tenant.ToString("D"), "reporter@example.invalid");
        var security = new OrganizationIdentity(Guid.NewGuid(), "security", tenant.ToString("D"), "security@example.invalid");
        var viewer = new OrganizationIdentity(Guid.NewGuid(), "viewer", tenant.ToString("D"), "viewer@example.invalid");
        var service = new OrganizationManagementService(store.CreateOrganizationStore(tenant));
        var organization = service.CreateOrganization("Emergency", "emergency", tenant.ToString("D"), reporter);
        service.AddMember(organization.Id, reporter, security.Email, OrganizationMemberRole.OrganizationAdmin);
        service.AcceptInvitation(organization.Id, security);
        service.AddMember(organization.Id, reporter, viewer.Email, OrganizationMemberRole.Viewer);
        service.AcceptInvitation(organization.Id, viewer);
        const string account = "123456789012";
        service.BindCloudAccount(organization.Id, reporter, CloudProvider.Aws, Guid.NewGuid(), account, "Client AWS");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var configurationRoot = $"EmergencyAccessReview:Organizations:{tenant:D}:{organization.Id:D}:ReviewerIds";
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        { [$"{configurationRoot}:0"] = security.UserId.ToString("D"), [$"{configurationRoot}:1"] = reporter.UserId.ToString("D") });
        builder.Services.AddAuthentication(TestAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("CiemApiAccess", policy => policy.RequireAuthenticatedUser()));
        builder.Services.AddSingleton(store);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapSharedWorkspaceEndpoints(store);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var prefix = $"/api/v1/organizations/{organization.Id:D}/break-glass-reports";
        async Task<HttpResponseMessage> Send(HttpMethod method, string path, OrganizationIdentity actor, object? body = null, bool delegated = true)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = JsonContent.Create(body);
            SetIdentityHeaders(request, Guid.Parse(actor.TenantId), actor.UserId, actor.Email);
            if (!delegated) request.Headers.Add("X-Test-Scope", "unrelated_scope");
            return await client.SendAsync(request);
        }
        var now = DateTimeOffset.UtcNow;
        var input = new CreateBreakGlassReportRequest(Guid.NewGuid(), CloudProvider.Aws, account,
            "arn:aws:iam::123456789012:user/client-emergency", now.AddHours(-2), now.AddHours(-1), "Client SSO outage",
            "INC-12345");
        using var denied = await Send(HttpMethod.Post, prefix, viewer, input);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var noDelegation = await Send(HttpMethod.Post, prefix, reporter, input, false);
        Assert.Equal(HttpStatusCode.Forbidden, noDelegation.StatusCode);
        using var unbound = await Send(HttpMethod.Post, prefix, reporter, input with { AccountId = "999999999999" });
        Assert.Equal(HttpStatusCode.Forbidden, unbound.StatusCode);
        using var future = await Send(HttpMethod.Post, prefix, reporter, input with { EndedAtUtc = now.AddHours(1) });
        Assert.Equal(HttpStatusCode.BadRequest, future.StatusCode);
        using var reversed = await Send(HttpMethod.Post, prefix, reporter, input with { StartedAtUtc = input.EndedAtUtc });
        Assert.Equal(HttpStatusCode.BadRequest, reversed.StatusCode);
        using var signedUrl = await Send(HttpMethod.Post, prefix, reporter, input with { ClientIncidentReference = "https://secret.invalid/?token=credential" });
        Assert.Equal(HttpStatusCode.BadRequest, signedUrl.StatusCode);
        using var missingEvidence = await Send(HttpMethod.Post, prefix, reporter, input with { ActivitySnapshotId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, missingEvidence.StatusCode);
        using var saved = await Send(HttpMethod.Post, prefix, reporter, input);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        Assert.EndsWith(input.ReportId.ToString("D"), saved.Headers.Location!.ToString());
        var report = (await saved.Content.ReadFromJsonAsync<ApiEnvelope<BreakGlassReport>>())!.Data;
        Assert.Equal("ClientAttestationOnly", report.EvidenceStatus);
        Assert.Null(report.Review);
        var eventCount = (await store.GetDriftEventsAsync(tenant, organization.Id)).Count;
        using var replay = await Send(HttpMethod.Post, prefix, reporter, input);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(report.ReportedAtUtc, (await replay.Content.ReadFromJsonAsync<ApiEnvelope<BreakGlassReport>>())!.Data.ReportedAtUtc);
        Assert.Equal(eventCount, (await store.GetDriftEventsAsync(tenant, organization.Id)).Count);
        using var conflict = await Send(HttpMethod.Post, prefix, reporter, input with { Reason = "Changed" });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var otherReporter = await Send(HttpMethod.Post, prefix, security, input);
        Assert.Equal(HttpStatusCode.Conflict, otherReporter.StatusCode);
        var path = prefix + $"/{report.ReportId:D}";
        using var foreign = await Send(HttpMethod.Get, path, reporter with { TenantId = Guid.NewGuid().ToString("D") });
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        var review = new ReviewBreakGlassReportRequest("Client rotated credentials and reviewed session cleanup", "CHANGE-54321", true);
        using var selfReview = await Send(HttpMethod.Post, path + "/review", reporter, review);
        Assert.Equal(HttpStatusCode.Forbidden, selfReview.StatusCode);
        using var noDelegatedReview = await Send(HttpMethod.Post, path + "/review", security, review, false);
        Assert.Equal(HttpStatusCode.Forbidden, noDelegatedReview.StatusCode);
        using var reviewed = await Send(HttpMethod.Post, path + "/review", security, review);
        Assert.Equal(HttpStatusCode.OK, reviewed.StatusCode);
        var result = (await reviewed.Content.ReadFromJsonAsync<ApiEnvelope<BreakGlassReport>>())!.Data;
        Assert.True(result.Review!.ClientVerifiedClosure);
        Assert.Equal("ClientAttestationOnly", result.EvidenceStatus);
        using var duplicateReview = await Send(HttpMethod.Post, path + "/review", security, review);
        Assert.Equal(HttpStatusCode.Conflict, duplicateReview.StatusCode);
        using var replayAfterReview = await Send(HttpMethod.Post, prefix, reporter, input);
        Assert.Equal(HttpStatusCode.Created, replayAfterReview.StatusCode);
        Assert.Equal(security.UserId, (await replayAfterReview.Content.ReadFromJsonAsync<ApiEnvelope<BreakGlassReport>>())!.Data.Review!.ReviewerId);
        var snapshot = new CloudActivitySnapshot(Guid.NewGuid(), CloudProvider.Aws, account, now.AddDays(-90), now, "Client audit", "Partial", false,
            [new("success", input.ExternalPrincipal, "iam.amazonaws.com/GetUser", null, null, now.AddMinutes(-90), true),
             new("success", input.ExternalPrincipal, "iam.amazonaws.com/GetUser", null, null, now.AddMinutes(-90), true),
             new("failed", input.ExternalPrincipal, "iam.amazonaws.com/DeleteUser", null, null, now.AddMinutes(-80), false),
             new("other-principal", "other", "iam.amazonaws.com/GetUser", null, null, now.AddMinutes(-90), true)]);
        await store.SaveCloudActivityAsync(tenant, organization.Id, reporter.UserId, snapshot);
        using var observed = await Send(HttpMethod.Post, prefix, reporter, input with { ReportId = Guid.NewGuid(), ActivitySnapshotId = snapshot.SnapshotId });
        Assert.Equal(HttpStatusCode.Created, observed.StatusCode);
        var partial = (await observed.Content.ReadFromJsonAsync<ApiEnvelope<BreakGlassReport>>())!.Data;
        Assert.Equal("PartialActivityObservedNotRevocationProof", partial.EvidenceStatus);
        Assert.Equal("success", Assert.Single(partial.MatchingEventIds));
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var grant = new NpgsqlCommand($"GRANT USAGE ON SCHEMA \"{database.Schema}\" TO yask_ciem_runtime;", connection))
            await grant.ExecuteNonQueryAsync();
        var runtimeBuilder = new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(database.ConnectionString) { Pooling = false }.ConnectionString);
        runtimeBuilder.UsePhysicalConnectionInitializer(
            runtimeConnection =>
            {
                using var role = new NpgsqlCommand("SET ROLE yask_ciem_runtime;", runtimeConnection);
                role.ExecuteNonQuery();
            },
            async runtimeConnection =>
            {
                await using var role = new NpgsqlCommand("SET ROLE yask_ciem_runtime;", runtimeConnection);
                await role.ExecuteNonQueryAsync();
            });
        await using var runtimeSource = runtimeBuilder.Build();
        using var runtimeStore = new SharedWorkspaceStore(runtimeSource);
        var runtimeInput = input with { ReportId = Guid.NewGuid() };
        var runtimeReport = report with { ReportId = runtimeInput.ReportId };
        await runtimeStore.SaveBreakGlassReportAsync(tenant, organization.Id, runtimeInput, runtimeReport);
        var runtimeReview = await runtimeStore.ReviewBreakGlassReportAsync(tenant, organization.Id, runtimeInput.ReportId, security.UserId, review);
        Assert.Equal(security.UserId, runtimeReview.Review!.ReviewerId);
        Assert.NotEmpty(await runtimeStore.GetBreakGlassReportsAsync(tenant, organization.Id));
        foreach (var table in new[] { "SharedBreakGlassReports", "SharedBreakGlassReviews" })
        {
            await using var delete = new NpgsqlCommand($"DELETE FROM {table};", connection);
            await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
            await using var truncate = new NpgsqlCommand($"TRUNCATE {table} CASCADE;", connection);
            await Assert.ThrowsAsync<PostgresException>(() => truncate.ExecuteNonQueryAsync());
        }
    }
}
