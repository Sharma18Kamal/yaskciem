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
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed partial class SharedWorkspaceEndpointTests
{
    [PostgreSqlFact]
    public async Task OperationalEndpoints_ShouldAuthorizeExplicitTicketsIsolateHealthAndDeduplicateRequests()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        var tenant = Guid.NewGuid();
        var owner = new OrganizationIdentity(Guid.NewGuid(), "owner", tenant.ToString(), "owner@example.invalid");
        var viewer = new OrganizationIdentity(Guid.NewGuid(), "viewer", tenant.ToString(), "viewer@example.invalid");
        var service = new OrganizationManagementService(store.CreateOrganizationStore(tenant));
        var organization = service.CreateOrganization("Operations", "operations", tenant.ToString(), owner);
        service.AddMember(organization.Id, owner, viewer.Email, OrganizationMemberRole.Viewer);
        service.AcceptInvitation(organization.Id, viewer);
        var schedule = (await store.CreateScanScheduleAsync(tenant, organization.Id,
            new(CloudProvider.Aws, "123456789012", "Production", 5, 0)))!;
        await store.QueueScanNowAsync(tenant, organization.Id, schedule.ScheduleId);
        var scanLease = (await store.ClaimNextScanJobAsync())!;
        await store.CompleteScanJobAttemptAsync(scanLease, "Failed", "sensitive source error", null);
        var value = Assert.Single(await store.GetDriftEventsAsync(tenant, organization.Id));
        Assert.Equal("scan.failed", value.EventType);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var root = $"CentralNotifications:Organizations:{tenant:D}:{organization.Id:D}:Channels:jira";
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{root}:Kind"] = "Jira", [$"{root}:Url"] = "https://jira.invalid",
            [$"{root}:UserName"] = "bot@example.invalid", [$"{root}:ApiToken"] = "test-server-only-secret",
            [$"{root}:ProjectKey"] = "SEC", [$"{root}:IssueType"] = "Task"
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
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var prefix = $"/api/v1/organizations/{organization.Id:D}";
        async Task<HttpResponseMessage> Send(HttpMethod method, string path, OrganizationIdentity actor, object? body = null)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = JsonContent.Create(body);
            SetIdentityHeaders(request, Guid.Parse(actor.TenantId), actor.UserId, actor.Email);
            return await client.SendAsync(request);
        }
        using var healthRead = await Send(HttpMethod.Get, prefix + "/connectors/health", viewer);
        Assert.Equal(HttpStatusCode.OK, healthRead.StatusCode);
        var health = Assert.Single((await healthRead.Content.ReadFromJsonAsync<ApiEnvelope<IReadOnlyList<ConnectorHealthDto>>>())!.Data);
        Assert.Equal("Configured", health.ConfigurationStatus);
        Assert.Equal("Unknown", health.Health);
        Assert.DoesNotContain("test-server-only-secret", await healthRead.Content.ReadAsStringAsync());
        using var eventRead = await Send(HttpMethod.Get, prefix + "/operational-events", viewer);
        Assert.Equal(value.EventId, Assert.Single((await eventRead.Content.ReadFromJsonAsync<ApiEnvelope<IReadOnlyList<SharedDriftEventDto>>>())!.Data).EventId);
        var ticketPath = prefix + $"/operational-events/{value.EventId:D}/ticket";
        using var denied = await Send(HttpMethod.Post, ticketPath, viewer, new CreateOperationalTicketRequest("jira"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var badChannel = await Send(HttpMethod.Post, ticketPath, owner, new CreateOperationalTicketRequest("https://secret.invalid"));
        Assert.Equal(HttpStatusCode.BadRequest, badChannel.StatusCode);
        using var missingChannel = await Send(HttpMethod.Post, ticketPath, owner, new CreateOperationalTicketRequest("missing"));
        Assert.Equal(HttpStatusCode.BadRequest, missingChannel.StatusCode);
        using var requested = await Send(HttpMethod.Post, ticketPath, owner, new CreateOperationalTicketRequest("jira"));
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        var status = (await requested.Content.ReadFromJsonAsync<ApiEnvelope<SharedDeliveryStatusDto>>())!.Data;
        Assert.Equal(owner.UserId, status.Delivery.RequestedBy);
        Assert.Equal("Pending", status.Delivery.Status);
        Assert.EndsWith(status.Delivery.DeliveryId.ToString("D"), requested.Headers.Location!.ToString());
        using var duplicate = await Send(HttpMethod.Post, ticketPath, owner, new CreateOperationalTicketRequest("jira"));
        Assert.Equal(status.Delivery.DeliveryId, (await duplicate.Content.ReadFromJsonAsync<ApiEnvelope<SharedDeliveryStatusDto>>())!.Data.Delivery.DeliveryId);
        Assert.Single(await store.GetNotificationDeliveriesAsync(tenant, organization.Id));
        var lease = (await store.ClaimNotificationDeliveryAsync())!;
        Assert.True(await store.IsNotificationEnabledAsync(lease, default));
        await store.CompleteNotificationDeliveryAsync(lease, new(false, false, 503, "outcome_unknown"));
        using var dead = await Send(HttpMethod.Get, prefix + "/connectors/health", viewer);
        Assert.Equal(1, Assert.Single((await dead.Content.ReadFromJsonAsync<ApiEnvelope<IReadOnlyList<ConnectorHealthDto>>>())!.Data).DeadLettered);
        foreach (var route in new[] { "/connectors/health", "/operational-events" })
        {
            using var crossTenant = await Send(HttpMethod.Get, prefix + route, owner with { TenantId = Guid.NewGuid().ToString() });
            Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);
            using var outsider = await Send(HttpMethod.Get, prefix + route, owner with { UserId = Guid.NewGuid(), Email = "outsider@example.invalid" });
            Assert.Equal(HttpStatusCode.Forbidden, outsider.StatusCode);
        }
        using var crossTicket = await Send(HttpMethod.Post, ticketPath, owner with { TenantId = Guid.NewGuid().ToString() }, new CreateOperationalTicketRequest("jira"));
        Assert.Equal(HttpStatusCode.NotFound, crossTicket.StatusCode);
        using var missingEvent = await Send(HttpMethod.Post, prefix + $"/operational-events/{Guid.NewGuid()}/ticket", owner, new CreateOperationalTicketRequest("jira"));
        Assert.Equal(HttpStatusCode.NotFound, missingEvent.StatusCode);
        await app.StopAsync();
    }
}
