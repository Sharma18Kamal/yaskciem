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
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed class SharedWorkspaceEndpointTests
{
    [Fact]
    public async Task OrganizationEndpoints_ShouldDeriveTenantFromAuthenticatedClaimsAndIsolateDirectories()
    {
        var directory = Path.Combine(Path.GetTempPath(), "YaskCiemSharedEndpointTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var store = new SharedWorkspaceStore($"Data Source={Path.Combine(directory, "shared.db")};Pooling=False");
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

            var now = DateTime.UtcNow;
            using var scanUpload = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/organizations/{created.Id:D}/scan-summaries")
            {
                Content = JsonContent.Create(new UploadSharedScanSummaryRequest(Guid.NewGuid(), now.AddMinutes(-1), now, "AWS",
                    new[] { new SharedScanAccountDto(CloudProvider.Aws, "123456789012", "Production AWS") },
                    5, 10, 2, 12, 7, 3, 1, 2, Array.Empty<SharedScanCoverageDto>()))
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

            var reviewId = Guid.NewGuid();
            var reviewItem = new AccessReviewItem(Guid.NewGuid(), reviewId, Guid.NewGuid(), Guid.NewGuid(), null, CloudProvider.Aws,
                "Role/Admin at account", ReviewDecision.Rejected, null, "must be reset by server", now)
            {
                EvidenceSnapshot = "Provider=AWS; Role=Admin; Owner=platform@example.invalid",
                OwnerSnapshot = "platform@example.invalid",
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
            using var decisionSave = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/organizations/{created.Id:D}/access-reviews/{reviewId:D}")
            {
                Content = JsonContent.Create(decisionCampaign)
            };
            SetIdentityHeaders(decisionSave, tenantId, userId, "owner@example.invalid");
            using var decisionResponse = await client.SendAsync(decisionSave);
            Assert.Equal(HttpStatusCode.OK, decisionResponse.StatusCode);
            var attestedCampaign = (await decisionResponse.Content.ReadFromJsonAsync<ApiEnvelope<AccessReviewCampaign>>())!.Data;
            var attestedItem = Assert.Single(attestedCampaign.Items);
            Assert.Equal("owner@example.invalid", attestedItem.Reviewer);
            Assert.Equal(1, attestedItem.ReminderCount);
            var serverAttestation = Assert.Single(attestedItem.Attestations);
            Assert.Equal("owner@example.invalid", serverAttestation.Reviewer);
            Assert.Equal("owner@example.invalid", serverAttestation.Actor);
            Assert.Equal(attestedItem.EvidenceSnapshot, serverAttestation.EvidenceSnapshot);
            Assert.NotEqual("forged-hash", serverAttestation.EvidenceSha256);
            Assert.Contains(attestedItem.AuditTrail, audit => audit.Action == "AttestationRecorded");

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
