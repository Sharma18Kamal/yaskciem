using System.Net;
using System.Net.Http.Json;
using CloudEntitle.Domain;
using CloudEntitle.IdentityGovernance.Models;
using CloudEntitle.IdentityGovernance.Services;
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
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed partial class SharedWorkspaceEndpointTests
{
    [PostgreSqlFact]
    public async Task Governance_ShouldAuditReassignmentRejectStaleWritesAndAuthorizeExceptionRenewals()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        var tenant = Guid.NewGuid();
        OrganizationIdentity Identity(string email) => new(Guid.NewGuid(), email, tenant.ToString(), email);
        var owner = Identity("manager@example.invalid");
        var reviewer = Identity("reviewer@example.invalid");
        var replacement = Identity("replacement@example.invalid");
        var viewer = Identity("viewer@example.invalid");
        var service = new OrganizationManagementService(store.CreateOrganizationStore(tenant));
        var organization = service.CreateOrganization("Governance", "governance", tenant.ToString(), owner);
        service.BindCloudAccount(organization.Id, owner, CloudProvider.Aws, Guid.NewGuid(), "123456789012", "Production");
        foreach (var member in new[] { reviewer, replacement, viewer })
        {
            service.AddMember(organization.Id, owner, member.Email,
                member == viewer ? OrganizationMemberRole.Viewer : OrganizationMemberRole.SecurityAnalyst);
            service.AcceptInvitation(organization.Id, member);
        }
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
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
        var prefix = $"/api/v1/organizations/{organization.Id}";
        async Task<HttpResponseMessage> Send(HttpMethod method, string path, OrganizationIdentity actor, object? payload = null)
        {
            using var message = new HttpRequestMessage(method, path);
            if (payload is not null) message.Content = JsonContent.Create(payload);
            SetIdentityHeaders(message, Guid.Parse(actor.TenantId), actor.UserId, actor.Email);
            return await client.SendAsync(message);
        }
        var campaign = new IdentityGovernanceService().CreateCampaign("Production review",
            new[] { new AccessReviewCandidate(Guid.NewGuid(), Guid.NewGuid(), null, CloudProvider.Aws, "Admin", "Captured evidence", "original-owner")
                { ExternalAccountId = "123456789012", Team = "Platform" } }, TimeSpan.FromDays(14), assignedReviewer: reviewer.Email);
        var path = $"{prefix}/access-reviews/{campaign.Id}";
        using var create = await Send(HttpMethod.Put, path, owner, campaign);
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var saved = (await create.Content.ReadFromJsonAsync<ApiEnvelope<AccessReviewCampaign>>())!.Data;
        var item = Assert.Single(saved.Items);
        var assignmentPath = path + $"/items/{item.Id}/assignment";
        var assignment = new ReassignReviewItemRequest(item.AssignmentVersion, viewer.Email, replacement.Email, "Security",
            "123456789012", DateTime.UtcNow.AddDays(7), "Quarterly owner rotation");
        using var denied = await Send(HttpMethod.Patch, assignmentPath, viewer, assignment);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var selfReview = await Send(HttpMethod.Patch, assignmentPath, owner, assignment with { Reviewer = owner.Email });
        Assert.Equal(HttpStatusCode.Conflict, selfReview.StatusCode);
        using var invalidOwner = await Send(HttpMethod.Patch, assignmentPath, owner, assignment with { Owner = "outsider@example.invalid" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidOwner.StatusCode);
        using var invalidAccount = await Send(HttpMethod.Patch, assignmentPath, owner, assignment with { ExternalAccountId = "other-account" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidAccount.StatusCode);
        using var reassigned = await Send(HttpMethod.Patch, assignmentPath, owner, assignment);
        Assert.Equal(HttpStatusCode.OK, reassigned.StatusCode);
        var current = (await reassigned.Content.ReadFromJsonAsync<ApiEnvelope<AccessReviewCampaign>>())!.Data;
        var assigned = Assert.Single(current.Items);
        Assert.Equal(1, assigned.AssignmentVersion);
        Assert.Equal(viewer.Email, assigned.AssignedOwner);
        Assert.Equal(replacement.Email, assigned.AssignedReviewer);
        Assert.Equal("original-owner", assigned.OwnerSnapshot);
        Assert.Equal(item.EvidenceSnapshot, assigned.EvidenceSnapshot);
        var audit = Assert.Single(assigned.AuditTrail, entry => entry.Action == "AssignmentChanged");
        Assert.Equal(owner.UserId, audit.ActorUserId);
        Assert.Contains("Before", audit.Details);
        Assert.Contains("After", audit.Details);
        Assert.Contains(assignment.Reason, audit.Details);
        Assert.NotEqual(Guid.Empty, audit.EventId);
        using var stale = await Send(HttpMethod.Patch, assignmentPath, owner, assignment);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var staleFull = await Send(HttpMethod.Put, path, owner, saved);
        Assert.Equal(HttpStatusCode.Conflict, staleFull.StatusCode);
        var racingAssignment = assignment with { ExpectedAssignmentVersion = assigned.AssignmentVersion, DueAtUtc = DateTime.UtcNow.AddDays(8) };
        var races = await Task.WhenAll(Send(HttpMethod.Patch, assignmentPath, owner, racingAssignment),
            Send(HttpMethod.Patch, assignmentPath, owner, racingAssignment));
        try
        {
            Assert.Single(races, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(races, response => response.StatusCode == HttpStatusCode.Conflict);
            current = (await races.Single(response => response.StatusCode == HttpStatusCode.OK)
                .Content.ReadFromJsonAsync<ApiEnvelope<AccessReviewCampaign>>())!.Data;
            assigned = Assert.Single(current.Items);
            Assert.Equal(2, assigned.AssignmentVersion);
        }
        finally { foreach (var response in races) response.Dispose(); }
        var exception = current with { Items = new[] { assigned with { Decision = ReviewDecision.ExceptionGranted,
            ReviewComment = "Temporary exception", ExceptionExpiresAt = DateTime.UtcNow.AddDays(2) } } };
        using var oldReviewer = await Send(HttpMethod.Put, path, reviewer, exception);
        Assert.Equal(HttpStatusCode.Forbidden, oldReviewer.StatusCode);
        using var decision = await Send(HttpMethod.Put, path, replacement, exception);
        Assert.Equal(HttpStatusCode.OK, decision.StatusCode);
        var decided = (await decision.Content.ReadFromJsonAsync<ApiEnvelope<AccessReviewCampaign>>())!.Data;
        Assert.Single(decided.Items[0].Attestations);
        // An expiry worker may run between reads. Renewal must clear current flags, not its audit history.
        await store.UpdateAccessReviewAsync(tenant, organization.Id, campaign.Id, value => value! with
        {
            Items = new[] { value!.Items[0] with { ExceptionExpiredAt = DateTime.UtcNow, EscalatedAt = DateTime.UtcNow,
                EscalationReason = "Expired exception" } }
        });
        var renewal = decided with { Items = new[] { decided.Items[0] with { ExceptionExpiresAt = DateTime.UtcNow.AddDays(5),
            ReviewComment = "Renewed with approval" } } };
        using var renewedResponse = await Send(HttpMethod.Put, path, replacement, renewal);
        Assert.Equal(HttpStatusCode.OK, renewedResponse.StatusCode);
        var renewed = (await renewedResponse.Content.ReadFromJsonAsync<ApiEnvelope<AccessReviewCampaign>>())!.Data;
        Assert.Equal(2, renewed.Items[0].Attestations.Count);
        Assert.Null(renewed.Items[0].ExceptionExpiredAt);
        Assert.Null(renewed.Items[0].EscalatedAt);
        using var replay = await Send(HttpMethod.Put, path, replacement, renewal);
        Assert.Equal(2, (await replay.Content.ReadFromJsonAsync<ApiEnvelope<AccessReviewCampaign>>())!.Data.Items[0].Attestations.Count);
        using var reportResponse = await Send(HttpMethod.Get, prefix + "/governance/report", viewer);
        Assert.Equal(HttpStatusCode.OK, reportResponse.StatusCode);
        var report = (await reportResponse.Content.ReadFromJsonAsync<ApiEnvelope<GovernanceReportDto>>())!.Data;
        Assert.Equal("Security", Assert.Single(report.Groups).Team);
        Assert.Contains(report.History, entry => entry.Event.EventId == audit.EventId);
        using var assigneesResponse = await Send(HttpMethod.Get, prefix + "/governance/assignees", viewer);
        var assignees = (await assigneesResponse.Content.ReadFromJsonAsync<ApiEnvelope<GovernanceAssigneesDto>>())!.Data;
        Assert.Contains(viewer.Email, assignees.Owners);
        Assert.Contains(replacement.Email, assignees.Reviewers);
        foreach (var route in new[] { "/governance/report", "/governance/assignees" })
        {
            using var crossTenant = await Send(HttpMethod.Get, prefix + route, owner with { TenantId = Guid.NewGuid().ToString() });
            Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);
            using var outsider = await Send(HttpMethod.Get, prefix + route, Identity("outsider@example.invalid"));
            Assert.Equal(HttpStatusCode.Forbidden, outsider.StatusCode);
        }
        await app.StopAsync();
    }
}
