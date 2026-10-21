using System.IO;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.IdentityGovernance.Models;
using CloudEntitle.LicenseServer.Services;
using CloudEntitle.OrganizationManagement.Models;
using CloudEntitle.OrganizationManagement.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed partial class SharedWorkspaceEndpointTests
{
    [PostgreSqlFact]
    public async Task CentralMonitoringEndpoints_ShouldEnforcePermissionsAndExposeSharedDeliveryAttempts()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        var tenant = Guid.NewGuid();
        var owner = new OrganizationIdentity(Guid.NewGuid(), "owner", tenant.ToString(), "owner@example.invalid");
        var viewer = new OrganizationIdentity(Guid.NewGuid(), "viewer", tenant.ToString(), "viewer@example.invalid");
        var service = new OrganizationManagementService(store.CreateOrganizationStore(tenant));
        var organization = service.CreateOrganization("Monitoring", "monitoring", tenant.ToString(), owner);
        service.BindCloudAccount(organization.Id, owner, CloudProvider.Aws, Guid.NewGuid(), "123456789012", "AWS");
        service.AddMember(organization.Id, owner, viewer.Email, OrganizationMemberRole.Viewer);
        service.AcceptInvitation(organization.Id, viewer);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication(TestAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("CiemApiAccess", policy => policy
            .RequireAuthenticatedUser().RequireClaim("tid").RequireClaim("oid")
            .RequireAssertion(context => context.User.FindFirst("scp")?.Value.Split(' ').Contains("access_as_user") == true)));
        builder.Services.AddSingleton(store);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapSharedWorkspaceEndpoints(store);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var prefix = $"/api/v1/organizations/{organization.Id:D}";
        async Task<HttpResponseMessage> Send(HttpMethod method, string path, OrganizationIdentity identity, object? body = null)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = JsonContent.Create(body);
            SetIdentityHeaders(request, Guid.Parse(identity.TenantId), identity.UserId, identity.Email);
            return await client.SendAsync(request);
        }
        var rule = new SharedNotificationRuleDto(Guid.NewGuid(), "High access changes", true, SharedAlertSeverity.High,
            true, true, true, true, true, "security");
        using var saveRule = await Send(HttpMethod.Put, $"{prefix}/notification-rules/{rule.RuleId}", owner, rule);
        Assert.Equal(HttpStatusCode.OK, saveRule.StatusCode);
        using var deniedWrite = await Send(HttpMethod.Put, $"{prefix}/notification-rules/{rule.RuleId}", viewer, rule);
        Assert.Equal(HttpStatusCode.Forbidden, deniedWrite.StatusCode);
        using var invalidRule = await Send(HttpMethod.Put, $"{prefix}/notification-rules/{rule.RuleId}", owner,
            rule with { Channel = "https://secret.invalid" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidRule.StatusCode);
        using var readRule = await Send(HttpMethod.Get, $"{prefix}/notification-rules", viewer);
        var readBack = Assert.Single((await readRule.Content.ReadFromJsonAsync<ApiEnvelope<IReadOnlyList<SharedNotificationRuleDto>>>())!.Data);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(rule), System.Text.Json.JsonSerializer.Serialize(readBack));

        var now = DateTime.UtcNow.AddMinutes(-2);
        var scan = new UploadSharedScanSummaryRequest(Guid.NewGuid(), now.AddSeconds(-1), now, "Aws",
            new[] { new SharedScanAccountDto(CloudProvider.Aws, "123456789012", "AWS") }, 0, 0, 0, 0, 0, 0, 0, 0,
            new[] { new SharedScanCoverageDto("Aws", "Effective access", "NoData", 0, "") })
        {
            MonitoringScopes = new[] { new SharedMonitoringScopeDto(CloudProvider.Aws, "123456789012", "effective-access", "effective-access-v1") }
        };
        using var baseline = await Send(HttpMethod.Post, $"{prefix}/scan-summaries", owner, scan);
        Assert.Equal(HttpStatusCode.Created, baseline.StatusCode);
        var next = scan with
        {
            ScanId = Guid.NewGuid(), StartedAtUtc = now, CompletedAtUtc = now.AddMinutes(1), EffectiveAccess = 1,
            Coverage = new[] { new SharedScanCoverageDto("Aws", "Effective access", "Collected", 1, "") },
            Records = new[] { new SharedCiemRecordDto(CloudProvider.Aws, "effective-access", "one", "123456789012",
                """{"principal":"admin","role":"Administrator","scope":"*","source":"IAM","isPrivileged":true}""") }
        };
        using var upload = await Send(HttpMethod.Post, $"{prefix}/scan-summaries", owner, next);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        using var replay = await Send(HttpMethod.Post, $"{prefix}/scan-summaries", owner, next);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var readDrift = await Send(HttpMethod.Get, $"{prefix}/drift-events", viewer);
        var drift = Assert.Single((await readDrift.Content.ReadFromJsonAsync<ApiEnvelope<IReadOnlyList<SharedDriftEventDto>>>())!.Data);
        using var readDeliveries = await Send(HttpMethod.Get, $"{prefix}/notification-deliveries", viewer);
        var delivery = Assert.Single((await readDeliveries.Content.ReadFromJsonAsync<ApiEnvelope<IReadOnlyList<SharedDeliveryDto>>>())!.Data);
        Assert.Equal(drift.EventId, delivery.EventId);
        var lease = Assert.IsType<NotificationDeliveryLease>(await store.ClaimNotificationDeliveryAsync());
        await store.CompleteNotificationDeliveryAsync(lease, new(false, true, 503, "http_rejected"));
        using var readStatus = await Send(HttpMethod.Get, $"{prefix}/notification-deliveries/{delivery.DeliveryId}", viewer);
        var status = (await readStatus.Content.ReadFromJsonAsync<ApiEnvelope<SharedDeliveryStatusDto>>())!.Data;
        Assert.Equal("Pending", status.Delivery.Status);
        Assert.Equal(503, Assert.Single(status.Attempts).HttpStatus);
        Assert.NotNull(status.Delivery.NextAttemptAtUtc);
        using var invalidLimit = await Send(HttpMethod.Get, $"{prefix}/drift-events?limit=501", owner);
        Assert.Equal(HttpStatusCode.BadRequest, invalidLimit.StatusCode);
        foreach (var route in new[] { "/drift-events", "/notification-rules", "/notification-deliveries", $"/notification-deliveries/{delivery.DeliveryId}" })
        {
            using var crossTenant = await Send(HttpMethod.Get, prefix + route, owner with { TenantId = Guid.NewGuid().ToString() });
            Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);
            using var nonMember = await Send(HttpMethod.Get, prefix + route, owner with { UserId = Guid.NewGuid() });
            Assert.Equal(HttpStatusCode.Forbidden, nonMember.StatusCode);
        }
        await app.StopAsync();
    }

    [PostgreSqlFact]
    public async Task OrganizationEndpoints_ShouldDeriveTenantFromAuthenticatedClaimsAndIsolateDirectories()
    {
        var directory = Path.Combine(Path.GetTempPath(), "YaskCiemSharedEndpointTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication(TestAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("CiemApiAccess", policy => policy
            .RequireAuthenticatedUser().RequireClaim("tid").RequireClaim("oid")
            .RequireAssertion(context => context.User.FindFirst("scp")?.Value.Split(' ').Contains("access_as_user") == true)));
        builder.Services.AddSingleton(store);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapSharedWorkspaceEndpoints(store);

        try
        {
            await app.StartAsync();
            var server = app.Services.GetRequiredService<IServer>();
            var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            var tenantId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            using var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/organizations")
            {
                Content = JsonContent.Create(new CreateOrganizationRequest("Shared Org", "shared-org"))
            };
            SetIdentityHeaders(create, tenantId, userId, "owner@example.invalid");
            using var createResponse = await client.SendAsync(create);
            Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
            var created = (await createResponse.Content.ReadFromJsonAsync<ApiEnvelope<OrganizationProfile>>())!.Data;
            Assert.Equal(tenantId.ToString("D"), created.EntraTenantId);

            using var sameTenantRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/organizations");
            SetIdentityHeaders(sameTenantRequest, tenantId, userId, "owner@example.invalid");
            using var sameTenantResponse = await client.SendAsync(sameTenantRequest);
            var sameTenantData = (await sameTenantResponse.Content.ReadFromJsonAsync<ApiEnvelope<IReadOnlyList<OrganizationProfile>>>())!.Data;
            Assert.Equal(created.Id, Assert.Single(sameTenantData).Id);

            var boundAccountId = Guid.NewGuid();
            using var bindRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/organizations/{created.Id:D}/bindings")
            {
                Content = JsonContent.Create(new BindOrganizationAccountRequest(CloudProvider.Aws, boundAccountId, "123456789012", "Production AWS"))
            };
            SetIdentityHeaders(bindRequest, tenantId, userId, "owner@example.invalid");
            using var bindResponse = await client.SendAsync(bindRequest);
            Assert.Equal(HttpStatusCode.Created, bindResponse.StatusCode);
            var boundBinding = (await bindResponse.Content.ReadFromJsonAsync<ApiEnvelope<TenantBinding>>())!.Data;

            var scheduleRequest = new CreateScanScheduleRequest(CloudProvider.Aws, boundBinding.ExternalTenantId, boundBinding.DisplayName, 60, 1);
            using var scheduleCreate = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/organizations/{created.Id:D}/scan-schedules")
            {
                Content = JsonContent.Create(scheduleRequest)
            };
            SetIdentityHeaders(scheduleCreate, tenantId, userId, "owner@example.invalid");
            using var scheduleResponse = await client.SendAsync(scheduleCreate);
            Assert.Equal(HttpStatusCode.Created, scheduleResponse.StatusCode);
            var schedule = (await scheduleResponse.Content.ReadFromJsonAsync<ApiEnvelope<ScanScheduleDto>>())!.Data;

            using var scheduleDuplicate = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/organizations/{created.Id:D}/scan-schedules")
            {
                Content = JsonContent.Create(scheduleRequest)
            };
            SetIdentityHeaders(scheduleDuplicate, tenantId, userId, "owner@example.invalid");
            using var scheduleDuplicateResponse = await client.SendAsync(scheduleDuplicate);
            Assert.Equal(HttpStatusCode.Conflict, scheduleDuplicateResponse.StatusCode);

            using var runNow = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/organizations/{created.Id:D}/scan-schedules/{schedule.ScheduleId:D}/run-now");
            SetIdentityHeaders(runNow, tenantId, userId, "owner@example.invalid");
            using var runNowResponse = await client.SendAsync(runNow);
            Assert.Equal(HttpStatusCode.Accepted, runNowResponse.StatusCode);
            var queuedJob = (await runNowResponse.Content.ReadFromJsonAsync<ApiEnvelope<ScanJobDto>>())!.Data;
            Assert.Equal("Queued", queuedJob.Status);
            using var jobStatusRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/organizations/{created.Id:D}/scan-jobs/{queuedJob.JobId:D}");
            SetIdentityHeaders(jobStatusRequest, tenantId, userId, "owner@example.invalid");
            using var jobStatusResponse = await client.SendAsync(jobStatusRequest);
            Assert.Equal(HttpStatusCode.OK, jobStatusResponse.StatusCode);
            var jobStatus = (await jobStatusResponse.Content.ReadFromJsonAsync<ApiEnvelope<ScanJobStatusDto>>())!.Data;
            Assert.Empty(jobStatus.Attempts);
            using var cancelJob = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/organizations/{created.Id:D}/scan-jobs/{queuedJob.JobId:D}/cancel");
            SetIdentityHeaders(cancelJob, tenantId, userId, "owner@example.invalid");
            using var cancelJobResponse = await client.SendAsync(cancelJob);
            Assert.Equal(HttpStatusCode.Accepted, cancelJobResponse.StatusCode);
            Assert.Equal("Cancelled", (await store.GetScanJobAsync(tenantId, created.Id, queuedJob.JobId))!.Status);

            var now = DateTime.UtcNow;
            var scanId = Guid.NewGuid();
            var scanRequest = new UploadSharedScanSummaryRequest(scanId, now.AddMinutes(-1), now, "AWS",
                new[] { new SharedScanAccountDto(CloudProvider.Aws, "123456789012", "Production AWS") },
                5, 10, 2, 12, 7, 3, 1, 2, Array.Empty<SharedScanCoverageDto>())
            {
                Records = new[]
                {
                    new SharedCiemRecordDto(CloudProvider.Aws, "finding", Guid.NewGuid().ToString("D"), "123456789012",
                        """{"title":"Unreviewed admin role","severity":"High"}"""),
                    new SharedCiemRecordDto(CloudProvider.Aws, "identity", Guid.NewGuid().ToString("D"), "123456789012",
                        """{"displayName":"deployment identity"}""")
                }
            };
            using var scanUpload = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/organizations/{created.Id:D}/scan-summaries")
            {
                Content = JsonContent.Create(scanRequest)
            };
            SetIdentityHeaders(scanUpload, tenantId, userId, "owner@example.invalid");
            using var scanResponse = await client.SendAsync(scanUpload);
            Assert.Equal(HttpStatusCode.Created, scanResponse.StatusCode);
            using var scanGet = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/organizations/{created.Id:D}/scan-summaries?limit=20");
            SetIdentityHeaders(scanGet, tenantId, userId, "owner@example.invalid");
            using var scanHistoryResponse = await client.SendAsync(scanGet);
            var sharedScan = Assert.Single((await scanHistoryResponse.Content.ReadFromJsonAsync<ApiEnvelope<IReadOnlyList<SharedScanSummaryDto>>>())!.Data);
            Assert.Equal("Aws", sharedScan.Provider);
            Assert.Equal(5, sharedScan.Identities);
            Assert.Equal(boundAccountId, boundBinding.CloudAccountId);

            using var recordsGet = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/organizations/{created.Id:D}/scans/{scanId:D}/records?kind=identity&limit=20");
            SetIdentityHeaders(recordsGet, tenantId, userId, "owner@example.invalid");
            using var recordsResponse = await client.SendAsync(recordsGet);
            Assert.True(recordsResponse.IsSuccessStatusCode,
                $"Records endpoint returned {(int)recordsResponse.StatusCode} {recordsResponse.StatusCode}: {await recordsResponse.Content.ReadAsStringAsync()}");
            var sharedRecords = (await recordsResponse.Content.ReadFromJsonAsync<ApiEnvelope<IReadOnlyList<SharedCiemRecordDto>>>())!.Data;
            Assert.Equal("identity", Assert.Single(sharedRecords).Kind);
            using var findingsGet = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/organizations/{created.Id:D}/findings?scanId={scanId:D}&limit=20");
            SetIdentityHeaders(findingsGet, tenantId, userId, "owner@example.invalid");
            using var findingsResponse = await client.SendAsync(findingsGet);
            Assert.True(findingsResponse.IsSuccessStatusCode,
                $"Findings endpoint returned {(int)findingsResponse.StatusCode} {findingsResponse.StatusCode}: {await findingsResponse.Content.ReadAsStringAsync()}");
            var sharedFindings = (await findingsResponse.Content.ReadFromJsonAsync<ApiEnvelope<IReadOnlyList<SharedCiemRecordDto>>>())!.Data;
            Assert.Equal("finding", Assert.Single(sharedFindings).Kind);

            using var duplicateUpload = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/organizations/{created.Id:D}/scan-summaries")
            {
                Content = JsonContent.Create(scanRequest)
            };
            SetIdentityHeaders(duplicateUpload, tenantId, userId, "owner@example.invalid");
            using var duplicateResponse = await client.SendAsync(duplicateUpload);
            Assert.Equal(HttpStatusCode.OK, duplicateResponse.StatusCode);

            var conflictingRequest = scanRequest with { Records = scanRequest.Records.Select(record => record with { PayloadJson = """{"title":"changed"}""" }).ToArray() };
            using var conflictingUpload = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/organizations/{created.Id:D}/scan-summaries")
            {
                Content = JsonContent.Create(conflictingRequest)
            };
            SetIdentityHeaders(conflictingUpload, tenantId, userId, "owner@example.invalid");
            using var conflictResponse = await client.SendAsync(conflictingUpload);
            Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);

            var reviewerUserId = Guid.NewGuid();
            using var reviewerInvite = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/organizations/{created.Id:D}/members")
            {
                Content = JsonContent.Create(new InviteOrganizationMemberRequest("reviewer@example.invalid",
                    new OrganizationRoleOption(OrganizationMemberRole.SecurityAnalyst.ToString(), OrganizationMemberRole.SecurityAnalyst, null)))
            };
            SetIdentityHeaders(reviewerInvite, tenantId, userId, "owner@example.invalid");
            using var reviewerInviteResponse = await client.SendAsync(reviewerInvite);
            Assert.Equal(HttpStatusCode.Created, reviewerInviteResponse.StatusCode);
            using var reviewerAccept = new HttpRequestMessage(HttpMethod.Get, "/api/v1/organizations");
            SetIdentityHeaders(reviewerAccept, tenantId, reviewerUserId, "reviewer@example.invalid");
            using var reviewerAcceptResponse = await client.SendAsync(reviewerAccept);
            Assert.Single((await reviewerAcceptResponse.Content.ReadFromJsonAsync<ApiEnvelope<IReadOnlyList<OrganizationProfile>>>())!.Data);
            using var reviewerListRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/organizations/{created.Id:D}/access-reviews/reviewers");
            SetIdentityHeaders(reviewerListRequest, tenantId, userId, "owner@example.invalid");
            using var reviewerListResponse = await client.SendAsync(reviewerListRequest);
            var reviewerEmails = (await reviewerListResponse.Content.ReadFromJsonAsync<ApiEnvelope<IReadOnlyList<string>>>())!.Data;
            Assert.Contains("reviewer@example.invalid", reviewerEmails);
            Assert.DoesNotContain("owner@example.invalid", reviewerEmails);

            var reviewId = Guid.NewGuid();
            var reviewItem = new AccessReviewItem(Guid.NewGuid(), reviewId, Guid.NewGuid(), Guid.NewGuid(), null, CloudProvider.Aws,
                "Role/Admin at account", ReviewDecision.Rejected, null, "must be reset by server", now)
            {
                EvidenceSnapshot = "Provider=AWS; Role=Admin; Owner=platform@example.invalid",
                OwnerSnapshot = "platform@example.invalid",
                AssignedReviewer = "reviewer@example.invalid",
                DueAt = now.AddDays(2),
                ReminderCount = 4,
                LastReminderAt = now,
                Attestations = new[] { new AccessReviewAttestation("spoofed", "spoofed", ReviewDecision.Rejected, "forged", now, "bad", "forged") }
            };
            var campaign = new AccessReviewCampaign(reviewId, "Quarterly review", CampaignStatus.Completed, now, now.AddDays(14),
                new[] { reviewItem.PrincipalId }, new[] { reviewItem })
            {
                DueAt = now.AddDays(2),
                ReminderLeadDays = 3,
                ReminderIntervalDays = 1
            };
            using var reviewSave = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/organizations/{created.Id:D}/access-reviews/{reviewId:D}")
            {
                Content = JsonContent.Create(campaign)
            };
            SetIdentityHeaders(reviewSave, tenantId, userId, "owner@example.invalid");
            using var reviewSaveResponse = await client.SendAsync(reviewSave);
            Assert.Equal(HttpStatusCode.OK, reviewSaveResponse.StatusCode);
            var savedCampaign = (await reviewSaveResponse.Content.ReadFromJsonAsync<ApiEnvelope<AccessReviewCampaign>>())!.Data;
            var pendingReviewItem = Assert.Single(savedCampaign.Items);
            Assert.Equal(ReviewDecision.Pending, pendingReviewItem.Decision);
            Assert.Empty(pendingReviewItem.Attestations);
            Assert.Equal("platform@example.invalid", pendingReviewItem.OwnerSnapshot);
            Assert.Equal(1, pendingReviewItem.ReminderCount);
            Assert.Contains(pendingReviewItem.AuditTrail, audit => audit.Action == "ReviewerReminderDue");
            Assert.NotEmpty(savedCampaign.AuditTrail);

            using var duplicateCampaignSave = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/organizations/{created.Id:D}/access-reviews/{reviewId:D}")
            {
                Content = JsonContent.Create(savedCampaign)
            };
            SetIdentityHeaders(duplicateCampaignSave, tenantId, userId, "owner@example.invalid");
            using var duplicateCampaignResponse = await client.SendAsync(duplicateCampaignSave);
            Assert.Equal(HttpStatusCode.OK, duplicateCampaignResponse.StatusCode);
            var duplicateSavedCampaign = (await duplicateCampaignResponse.Content.ReadFromJsonAsync<ApiEnvelope<AccessReviewCampaign>>())!.Data;
            Assert.Single(duplicateSavedCampaign.Items);
            Assert.Single(duplicateSavedCampaign.AuditTrail, audit => audit.Action == "CampaignCreated");

            var submittedAttestation = new AccessReviewAttestation("forged-reviewer", "forged-actor", ReviewDecision.Certified,
                "spoofed evidence", now, "forged-hash", "spoofed snapshot");
            var decisionCampaign = savedCampaign with
            {
                Items = new[] { pendingReviewItem with
                {
                    Decision = ReviewDecision.Certified,
                    Reviewer = "forged-reviewer",
                    ReviewComment = "owner confirmed",
                    DecidedAt = now,
                    Attestations = new[] { submittedAttestation }
                } }
            };
            using var creatorDecisionRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/organizations/{created.Id:D}/access-reviews/{reviewId:D}")
            {
                Content = JsonContent.Create(decisionCampaign)
            };
            SetIdentityHeaders(creatorDecisionRequest, tenantId, userId, "owner@example.invalid");
            using var creatorDecisionResponse = await client.SendAsync(creatorDecisionRequest);
            Assert.Equal(HttpStatusCode.Forbidden, creatorDecisionResponse.StatusCode);

            using var decisionSave = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/organizations/{created.Id:D}/access-reviews/{reviewId:D}")
            {
                Content = JsonContent.Create(decisionCampaign)
            };
            SetIdentityHeaders(decisionSave, tenantId, reviewerUserId, "reviewer@example.invalid");
            using var decisionResponse = await client.SendAsync(decisionSave);
            Assert.Equal(HttpStatusCode.OK, decisionResponse.StatusCode);
            var decisionEnvelope = (await decisionResponse.Content.ReadFromJsonAsync<ApiEnvelope<AccessReviewCampaign>>())!;
            var attestedCampaign = decisionEnvelope.Data;
            var attestedItem = Assert.Single(attestedCampaign.Items);
            Assert.Equal("reviewer@example.invalid", attestedItem.Reviewer);
            Assert.Equal(1, attestedItem.ReminderCount);
            var serverAttestation = Assert.Single(attestedItem.Attestations);
            Assert.Equal("reviewer@example.invalid", serverAttestation.Reviewer);
            Assert.Equal("reviewer@example.invalid", serverAttestation.Actor);
            Assert.Equal(reviewerUserId, serverAttestation.ActorUserId);
            Assert.Equal(attestedItem.EvidenceSnapshot, serverAttestation.EvidenceSnapshot);
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(attestedItem.EvidenceSnapshot))).ToLowerInvariant(),
                serverAttestation.EvidenceSha256);
            Assert.NotEqual("forged-hash", serverAttestation.EvidenceSha256);
            Assert.NotEqual(Guid.Empty, serverAttestation.CorrelationId);
            Assert.Equal(Guid.Parse(decisionEnvelope.CorrelationId), serverAttestation.CorrelationId);
            Assert.NotEqual(Guid.Empty, Assert.Single(attestedItem.AuditTrail, audit => audit.Action.StartsWith("DecisionRecorded:", StringComparison.Ordinal)).CorrelationId);
            Assert.Contains(attestedItem.AuditTrail, audit => audit.Action == "AttestationRecorded");

            using var duplicateReviewSave = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/organizations/{created.Id:D}/access-reviews/{reviewId:D}")
            {
                Content = JsonContent.Create(decisionCampaign)
            };
            SetIdentityHeaders(duplicateReviewSave, tenantId, reviewerUserId, "reviewer@example.invalid");
            using var duplicateReviewResponse = await client.SendAsync(duplicateReviewSave);
            Assert.Equal(HttpStatusCode.OK, duplicateReviewResponse.StatusCode);
            var duplicateCampaign = (await duplicateReviewResponse.Content.ReadFromJsonAsync<ApiEnvelope<AccessReviewCampaign>>())!.Data;
            var duplicateItem = Assert.Single(duplicateCampaign.Items);
            Assert.Single(duplicateItem.Attestations);
            Assert.Equal(attestedItem.Attestations, duplicateItem.Attestations);
            Assert.Equal(attestedItem.EvidenceSnapshot, duplicateItem.EvidenceSnapshot);

            var changedEvidenceCampaign = decisionCampaign with
            {
                Items = new[] { decisionCampaign.Items.Single() with { EvidenceSnapshot = "tampered after decision" } }
            };
            using var changedEvidenceRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/organizations/{created.Id:D}/access-reviews/{reviewId:D}")
            {
                Content = JsonContent.Create(changedEvidenceCampaign)
            };
            SetIdentityHeaders(changedEvidenceRequest, tenantId, reviewerUserId, "reviewer@example.invalid");
            using var changedEvidenceResponse = await client.SendAsync(changedEvidenceRequest);
            Assert.Equal(HttpStatusCode.Conflict, changedEvidenceResponse.StatusCode);

            await using (var connection = new NpgsqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync();
                await using var auditQuery = new NpgsqlCommand("""
                    SELECT COUNT(*)
                    FROM SharedAccessReviewAuditEvents
                    WHERE TenantId=@tenant AND OrganizationId=@organization AND CampaignId=@campaign
                      AND Decision='Certified' AND EvidenceSha256 IS NOT NULL AND CorrelationId IS NOT NULL AND ActorUserId IS NOT NULL
                      AND Rationale IS NOT NULL;
                    """, connection);
                auditQuery.Parameters.AddWithValue("tenant", tenantId);
                auditQuery.Parameters.AddWithValue("organization", created.Id);
                auditQuery.Parameters.AddWithValue("campaign", reviewId);
                Assert.True(Convert.ToInt32(await auditQuery.ExecuteScalarAsync()) >= 2);
                await using var tamperAudit = new NpgsqlCommand("""
                    UPDATE SharedAccessReviewAuditEvents SET Rationale='tampered'
                    WHERE TenantId=@tenant AND OrganizationId=@organization AND CampaignId=@campaign;
                    """, connection);
                tamperAudit.Parameters.AddWithValue("tenant", tenantId);
                tamperAudit.Parameters.AddWithValue("organization", created.Id);
                tamperAudit.Parameters.AddWithValue("campaign", reviewId);
                await Assert.ThrowsAsync<PostgresException>(() => tamperAudit.ExecuteNonQueryAsync());
            }

            using var reviewListRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/organizations/{created.Id:D}/access-reviews");
            SetIdentityHeaders(reviewListRequest, tenantId, userId, "owner@example.invalid");
            using var reviewListResponse = await client.SendAsync(reviewListRequest);
            Assert.Equal(campaign.Id, Assert.Single((await reviewListResponse.Content.ReadFromJsonAsync<ApiEnvelope<IReadOnlyList<AccessReviewCampaign>>>())!.Data).Id);

            using var workspaceRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/organizations/{created.Id:D}/workspace");
            SetIdentityHeaders(workspaceRequest, tenantId, userId, "owner@example.invalid");
            using var workspaceResponse = await client.SendAsync(workspaceRequest);
            var workspace = (await workspaceResponse.Content.ReadFromJsonAsync<ApiEnvelope<OrganizationWorkspaceDto>>())!.Data;
            Assert.Contains(workspace.AuditTrail, audit => audit.Action == "OrganizationCreated");
            Assert.Single(workspace.Bindings);

            using var inviteRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/organizations/{created.Id:D}/members")
            {
                Content = JsonContent.Create(new InviteOrganizationMemberRequest("viewer@example.invalid",
                    new OrganizationRoleOption(OrganizationMemberRole.Viewer.ToString(), OrganizationMemberRole.Viewer, null)))
            };
            SetIdentityHeaders(inviteRequest, tenantId, userId, "owner@example.invalid");
            using var inviteResponse = await client.SendAsync(inviteRequest);
            Assert.Equal(HttpStatusCode.Created, inviteResponse.StatusCode);

            var viewerId = Guid.NewGuid();
            using var viewerOrganizationsRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/organizations");
            SetIdentityHeaders(viewerOrganizationsRequest, tenantId, viewerId, "viewer@example.invalid");
            using var viewerOrganizationsResponse = await client.SendAsync(viewerOrganizationsRequest);
            Assert.Single((await viewerOrganizationsResponse.Content.ReadFromJsonAsync<ApiEnvelope<IReadOnlyList<OrganizationProfile>>>())!.Data);
            using var viewerWorkspaceRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/organizations/{created.Id:D}/workspace");
            SetIdentityHeaders(viewerWorkspaceRequest, tenantId, viewerId, "viewer@example.invalid");
            using var viewerWorkspaceResponse = await client.SendAsync(viewerWorkspaceRequest);
            var viewerWorkspace = (await viewerWorkspaceResponse.Content.ReadFromJsonAsync<ApiEnvelope<OrganizationWorkspaceDto>>())!.Data;
            Assert.Contains(OrganizationAction.ViewFindings, viewerWorkspace.AllowedActions);
            Assert.DoesNotContain(OrganizationAction.ManageMembers, viewerWorkspace.AllowedActions);
            Assert.Empty(viewerWorkspace.Members);

            var unauthorizedDecision = attestedCampaign with
            {
                Items = new[] { attestedItem with
                {
                    Decision = ReviewDecision.Rejected,
                    ReviewComment = "viewer should not decide"
                } }
            };
            using var unauthorizedReviewRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/organizations/{created.Id:D}/access-reviews/{reviewId:D}")
            {
                Content = JsonContent.Create(unauthorizedDecision)
            };
            SetIdentityHeaders(unauthorizedReviewRequest, tenantId, viewerId, "viewer@example.invalid");
            using var unauthorizedReviewResponse = await client.SendAsync(unauthorizedReviewRequest);
            Assert.Equal(HttpStatusCode.Forbidden, unauthorizedReviewResponse.StatusCode);

            using var viewerInviteRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/organizations/{created.Id:D}/members")
            {
                Content = JsonContent.Create(new InviteOrganizationMemberRequest("unauthorized@example.invalid",
                    new OrganizationRoleOption(OrganizationMemberRole.Viewer.ToString(), OrganizationMemberRole.Viewer, null)))
            };
            SetIdentityHeaders(viewerInviteRequest, tenantId, viewerId, "viewer@example.invalid");
            using var viewerInviteResponse = await client.SendAsync(viewerInviteRequest);
            Assert.Equal(HttpStatusCode.Forbidden, viewerInviteResponse.StatusCode);

            using var otherTenantRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/organizations");
            SetIdentityHeaders(otherTenantRequest, Guid.NewGuid(), Guid.NewGuid(), "other@example.invalid");
            using var otherTenantResponse = await client.SendAsync(otherTenantRequest);
            var otherTenantData = (await otherTenantResponse.Content.ReadFromJsonAsync<ApiEnvelope<IReadOnlyList<OrganizationProfile>>>())!.Data;
            Assert.Empty(otherTenantData);

            using var crossTenantWorkspaceRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/organizations/{created.Id:D}/workspace");
            SetIdentityHeaders(crossTenantWorkspaceRequest, Guid.NewGuid(), Guid.NewGuid(), "other@example.invalid");
            using var crossTenantWorkspaceResponse = await client.SendAsync(crossTenantWorkspaceRequest);
            Assert.Equal(HttpStatusCode.NotFound, crossTenantWorkspaceResponse.StatusCode);

            var separateOwnerUserId = Guid.NewGuid();
            using var secondOrganizationRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/organizations")
            {
                Content = JsonContent.Create(new CreateOrganizationRequest("Separate Org", "separate-org"))
            };
            SetIdentityHeaders(secondOrganizationRequest, tenantId, separateOwnerUserId, "separate-owner@example.invalid");
            using var secondOrganizationResponse = await client.SendAsync(secondOrganizationRequest);
            Assert.Equal(HttpStatusCode.Created, secondOrganizationResponse.StatusCode);
            using var crossOrganizationReviewRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/organizations/{created.Id:D}/access-reviews");
            SetIdentityHeaders(crossOrganizationReviewRequest, tenantId, separateOwnerUserId, "separate-owner@example.invalid");
            using var crossOrganizationReviewResponse = await client.SendAsync(crossOrganizationReviewRequest);
            Assert.Equal(HttpStatusCode.Forbidden, crossOrganizationReviewResponse.StatusCode);
        }
        finally
        {
            await app.StopAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void SetIdentityHeaders(HttpRequestMessage request, Guid tenantId, Guid userId, string email)
    {
        request.Headers.Add("X-Test-Tenant", tenantId.ToString("D"));
        request.Headers.Add("X-Test-User", userId.ToString("D"));
        request.Headers.Add("X-Test-Email", email);
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "SharedWorkspaceTest";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Guid.TryParse(Request.Headers["X-Test-Tenant"], out var tenantId)
                || !Guid.TryParse(Request.Headers["X-Test-User"], out var userId)) return Task.FromResult(AuthenticateResult.NoResult());
            var email = Request.Headers["X-Test-Email"].ToString();
            var claims = new[]
            {
                new Claim("tid", tenantId.ToString("D")),
                new Claim("oid", userId.ToString("D")),
                new Claim("sub", userId.ToString("N")),
                new Claim("preferred_username", email),
                new Claim("scp", "access_as_user")
            };
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
