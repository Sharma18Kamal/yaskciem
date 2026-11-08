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
        service.BindCloudAccount(organization.Id, owner, CloudProvider.Aws, Guid.NewGuid(), "123456789012", "Production");
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
        using var catalogRead = await Send(HttpMethod.Get, prefix + "/connectors/catalog", viewer);
        Assert.Equal(HttpStatusCode.OK, catalogRead.StatusCode);
        var catalog = (await catalogRead.Content.ReadFromJsonAsync<ApiEnvelope<IReadOnlyList<SecurityConnectorCapability>>>())!.Data;
        Assert.Contains(catalog, item => item.Kind == SecurityConnectorCatalog.Splunk && item.ExportEvents);
        Assert.Contains(catalog, item => item.Kind == SecurityConnectorCatalog.QRadar && item.ExportEvents);
        Assert.Contains(catalog, item => item.Kind == SecurityConnectorCatalog.Sentinel && item.ExportEvents);
        Assert.All(catalog, item => Assert.False(item.ExecuteResponseActions));
        var activityTime = DateTimeOffset.UtcNow;
        var activity = new CloudActivitySnapshot(Guid.NewGuid(), CloudProvider.Aws, "123456789012",
            activityTime.AddDays(-90), activityTime, "CloudTrail regional event history", "Partial management only", false,
            new[] { new CloudActivityEvent("event", "principal", "iam.amazonaws.com/CreateUser", null, null, activityTime.AddDays(-1), true) });
        using var activityDenied = await Send(HttpMethod.Post, prefix + "/cloud-activity", viewer, activity);
        Assert.Equal(HttpStatusCode.Forbidden, activityDenied.StatusCode);
        using var activityUnbound = await Send(HttpMethod.Post, prefix + "/cloud-activity", owner, activity with { AccountId = "999999999999" });
        Assert.Equal(HttpStatusCode.Forbidden, activityUnbound.StatusCode);
        using var activitySaved = await Send(HttpMethod.Post, prefix + "/cloud-activity", owner, activity);
        Assert.Equal(HttpStatusCode.OK, activitySaved.StatusCode);
        var activityPath = prefix + "/cloud-activity?provider=Aws&accountId=123456789012";
        using var activityRead = await Send(HttpMethod.Get, activityPath, viewer);
        Assert.Equal(HttpStatusCode.OK, activityRead.StatusCode);
        Assert.Equal(activity.SnapshotId, (await activityRead.Content.ReadFromJsonAsync<ApiEnvelope<CloudActivitySnapshot>>())!.Data.SnapshotId);
        using var activityConflict = await Send(HttpMethod.Post, prefix + "/cloud-activity", owner, activity with { Source = "Changed" });
        Assert.Equal(HttpStatusCode.Conflict, activityConflict.StatusCode);
        var historyPath = prefix + "/cloud-activity/history?provider=Aws&accountId=123456789012";
        using var activityHistory = await Send(HttpMethod.Get, historyPath, viewer);
        Assert.Equal(HttpStatusCode.OK, activityHistory.StatusCode);
        var history = (await activityHistory.Content.ReadFromJsonAsync<ApiEnvelope<CloudActivityHistoryPage>>())!.Data;
        Assert.Equal(activity.SnapshotId, Assert.Single(history.Snapshots).SnapshotId);
        Assert.Equal(owner.UserId, history.Snapshots[0].UploadedBy);
        Assert.Equal(1, history.Snapshots[0].EventCount);
        var snapshotPath = prefix + $"/cloud-activity/history/{activity.SnapshotId:D}?provider=Aws&accountId=123456789012";
        using var historicalRead = await Send(HttpMethod.Get, snapshotPath, viewer);
        Assert.Equal(HttpStatusCode.OK, historicalRead.StatusCode);
        Assert.Equal(activity.SnapshotId, (await historicalRead.Content.ReadFromJsonAsync<ApiEnvelope<CloudActivitySnapshot>>())!.Data.SnapshotId);
        using var missingCursor = await Send(HttpMethod.Get, historyPath + $"&beforeSnapshotId={Guid.NewGuid():D}", viewer);
        Assert.Equal(HttpStatusCode.BadRequest, missingCursor.StatusCode);
        using var invalidHistoryLimit = await Send(HttpMethod.Get, historyPath + "&limit=51", viewer);
        Assert.Equal(HttpStatusCode.BadRequest, invalidHistoryLimit.StatusCode);
        using var unboundHistory = await Send(HttpMethod.Get, prefix + "/cloud-activity/history?provider=Aws&accountId=999999999999", viewer);
        Assert.Equal(HttpStatusCode.Forbidden, unboundHistory.StatusCode);
        using var missingHistorical = await Send(HttpMethod.Get, prefix + $"/cloud-activity/history/{Guid.NewGuid():D}?provider=Aws&accountId=123456789012", viewer);
        Assert.Equal(HttpStatusCode.NotFound, missingHistorical.StatusCode);
        var foreignActor = new OrganizationIdentity(viewer.UserId, "foreign", Guid.NewGuid().ToString(), viewer.Email);
        using var foreignHistory = await Send(HttpMethod.Get, historyPath, foreignActor);
        Assert.NotEqual(HttpStatusCode.OK, foreignHistory.StatusCode);
        var permissions = new EntitlementEvidenceSnapshot(Guid.NewGuid(), CloudProvider.Aws, "123456789012",
            activityTime.AddMinutes(-1), activityTime, [new("arn:aws:iam::123456789012:user/reader",
                "arn:aws:iam::123456789012:user/reader", "user/reader", "policy", "v1", new string('A', 64),
                "Read", "AWS account", "s3:GetObject", "arn:aws:s3:::source/object", "Allow", false, false, false)], ["Partial"]);
        using var permissionDenied = await Send(HttpMethod.Post, prefix + "/entitlement-evidence", viewer, permissions);
        Assert.Equal(HttpStatusCode.Forbidden, permissionDenied.StatusCode);
        using var permissionUnbound = await Send(HttpMethod.Post, prefix + "/entitlement-evidence", owner, permissions with { AccountId = "999999999999" });
        Assert.Equal(HttpStatusCode.Forbidden, permissionUnbound.StatusCode);
        using var permissionSaved = await Send(HttpMethod.Post, prefix + "/entitlement-evidence", owner, permissions);
        Assert.Equal(HttpStatusCode.Created, permissionSaved.StatusCode);
        Assert.NotNull(permissionSaved.Headers.Location);
        using var permissionRead = await Send(HttpMethod.Get, permissionSaved.Headers.Location!.OriginalString, viewer);
        Assert.Equal(HttpStatusCode.OK, permissionRead.StatusCode);
        Assert.Equal(permissions.Grants[0], Assert.Single((await permissionRead.Content.ReadFromJsonAsync<ApiEnvelope<EntitlementEvidenceSnapshot>>())!.Data.Grants));
        using var permissionSummary = await Send(HttpMethod.Get, prefix + "/entitlement-evidence?provider=Aws&accountId=123456789012", viewer);
        Assert.Equal(owner.UserId, Assert.Single((await permissionSummary.Content.ReadFromJsonAsync<ApiEnvelope<IReadOnlyList<EntitlementEvidenceHistoryEntry>>>())!.Data).UploadedBy);
        using var permissionConflict = await Send(HttpMethod.Post, prefix + "/entitlement-evidence", owner, permissions with { Grants = [] });
        Assert.Equal(HttpStatusCode.Conflict, permissionConflict.StatusCode);
        using var permissionForeign = await Send(HttpMethod.Get, permissionSaved.Headers.Location.OriginalString, foreignActor);
        Assert.NotEqual(HttpStatusCode.OK, permissionForeign.StatusCode);
        using var permissionInvalid = await Send(HttpMethod.Post, prefix + "/entitlement-evidence", owner,
            permissions with { SnapshotId = Guid.NewGuid(), Grants = [permissions.Grants[0] with { IsExclusion = true }] });
        Assert.Equal(HttpStatusCode.BadRequest, permissionInvalid.StatusCode);
        foreach (var (body, expectedStatus) in new[] { ("{invalid", HttpStatusCode.BadRequest), (new string(' ', 8 * 1024 * 1024 + 1), HttpStatusCode.RequestEntityTooLarge) })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, prefix + "/entitlement-evidence")
                { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
            SetIdentityHeaders(request, tenant, owner.UserId, owner.Email);
            using var response = await client.SendAsync(request);
            Assert.Equal(expectedStatus, response.StatusCode);
            Assert.DoesNotContain("JsonException", await response.Content.ReadAsStringAsync());
        }
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
        foreach (var route in new[] { "/connectors/health", "/connectors/catalog", "/operational-events", "/cloud-activity?provider=Aws&accountId=123456789012" })
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
        var analysisRoot = $"BehaviorAnalysis:Organizations:{tenant:D}:{organization.Id:D}:Aws";
        builder.Configuration[$"{analysisRoot}:Enabled"] = "true";
        builder.Configuration[$"{analysisRoot}:TimeZoneId"] = "UTC";
        builder.Configuration[$"{analysisRoot}:PrivilegedActions:0"] = "iam.amazonaws.com/CreateUser";
        var sunday = new DateTimeOffset(activityTime.UtcDateTime.Date.AddDays(-(int)activityTime.DayOfWeek), TimeSpan.Zero);
        var reviewActivity = activity with { SnapshotId = Guid.NewGuid(), Events = [activity.Events[0] with { EventId = "weekend-call", Timestamp = sunday }] };
        await store.SaveCloudActivityAsync(tenant, organization.Id, owner.UserId, reviewActivity);
        var newTime = DateTimeOffset.UtcNow;
        var changedDocuments = permissions with { SnapshotId = Guid.NewGuid(), StartedAtUtc = newTime, CollectedAtUtc = newTime,
            Grants = [permissions.Grants[0] with { SourceSha256 = new string('B', 64), Action = "s3:PutBucketPolicy" }] };
        await store.SaveEntitlementEvidenceAsync(tenant, organization.Id, owner.UserId, changedDocuments);
        var analysisRequest = new SecurityAnalysisRequest(CloudProvider.Aws, "123456789012", reviewActivity.SnapshotId, changedDocuments.SnapshotId);
        using var analysisDenied = await Send(HttpMethod.Post, prefix + "/security-analysis", viewer, analysisRequest);
        Assert.Equal(HttpStatusCode.Forbidden, analysisDenied.StatusCode);
        using var analysis = await Send(HttpMethod.Post, prefix + "/security-analysis", owner, analysisRequest);
        Assert.True(analysis.StatusCode == HttpStatusCode.OK, await analysis.Content.ReadAsStringAsync());
        var report = (await analysis.Content.ReadFromJsonAsync<ApiEnvelope<SecurityAnalysisReport>>())!.Data;
        Assert.Contains(report.Anomalies, item => item.Kind == "PrivilegedActionOutsideBusinessHours");
        Assert.Equal("ObservedPolicyOrAssignmentChange", Assert.Single(report.Changes).Kind);
        var eventCount = (await store.GetDriftEventsAsync(tenant, organization.Id)).Count;
        using var analysisReplay = await Send(HttpMethod.Post, prefix + "/security-analysis", owner, analysisRequest);
        Assert.Equal(HttpStatusCode.OK, analysisReplay.StatusCode);
        Assert.Equal(eventCount, (await store.GetDriftEventsAsync(tenant, organization.Id)).Count);
        var snowRoot = $"CentralNotifications:Organizations:{tenant:D}:{organization.Id:D}:Channels:snow";
        builder.Configuration[$"{snowRoot}:Kind"] = "ServiceNow";
        builder.Configuration[$"{snowRoot}:Url"] = "https://client.service-now.com";
        builder.Configuration[$"{snowRoot}:UserName"] = "bot";
        builder.Configuration[$"{snowRoot}:ApiToken"] = "fixture-secret";
        var reviewEvent = (await store.GetDriftEventsAsync(tenant, organization.Id)).First(item => item.EventType == "behavior.anomaly");
        using var snow = await Send(HttpMethod.Post, prefix + $"/operational-events/{reviewEvent.EventId:D}/ticket", owner, new CreateOperationalTicketRequest("snow"));
        Assert.Equal(HttpStatusCode.Accepted, snow.StatusCode);
        Assert.Equal("ServiceNow", (await snow.Content.ReadFromJsonAsync<ApiEnvelope<SharedDeliveryStatusDto>>())!.Data.Delivery.ConnectorKind);
        await app.StopAsync();
    }
}
