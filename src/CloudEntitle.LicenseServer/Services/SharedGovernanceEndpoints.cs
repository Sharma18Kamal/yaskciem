using System.Security.Claims;
using CloudEntitle.IdentityGovernance.Models;
using CloudEntitle.IdentityGovernance.Services;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.OrganizationManagement.Models;

namespace CloudEntitle.LicenseServer.Services;

public static partial class SharedWorkspaceEndpoints
{
    private static void MapGovernanceEndpoints(this WebApplication app, SharedWorkspaceStore store)
    {
        const string prefix = "/api/v1/organizations/{organizationId:guid}";
        app.MapGet(prefix + "/governance/report", (ClaimsPrincipal principal, Guid organizationId, CancellationToken token) =>
            WithGovernanceAccess(principal, organizationId, store, false, async (tenant, _) =>
                Results.Ok(Envelope(await store.GetGovernanceReportAsync(tenant, organizationId, cancellationToken: token)))))
            .RequireAuthorization("CiemApiAccess").WithName("GetGovernanceReport")
            .WithSummary("Report review completion, overdue work, exception aging and the latest 500 audit events by account and team.")
            .Produces<ApiEnvelope<GovernanceReportDto>>().Produces(StatusCodes.Status403Forbidden);
        app.MapGet(prefix + "/governance/assignees", (ClaimsPrincipal principal, Guid organizationId, CancellationToken token) =>
            WithGovernanceAccess(principal, organizationId, store, false, (_, identity) =>
            {
                token.ThrowIfCancellationRequested();
                var service = CreateService(store, identity);
                var callerAction = service.Authorize(organizationId, identity, OrganizationAction.ViewFindings).Allowed
                    ? OrganizationAction.ViewFindings : OrganizationAction.ManageRemediations;
                var owners = service.GetMembersAuthorizedForAction(organizationId, identity, callerAction, OrganizationAction.ViewInventory);
                var reviewers = service.GetMembersAuthorizedForAction(organizationId, identity, callerAction, OrganizationAction.ManageRemediations)
                    .Where(member => member.UserId != identity.UserId);
                return Task.FromResult<IResult>(Results.Ok(Envelope(new GovernanceAssigneesDto(
                    owners.Select(member => member.Email).ToArray(), reviewers.Select(member => member.Email).ToArray()))));
            })).RequireAuthorization("CiemApiAccess").WithName("GetGovernanceAssignees")
            .WithSummary("List active organization owners and authorized reviewers; creators cannot review their own items.")
            .Produces<ApiEnvelope<GovernanceAssigneesDto>>().Produces(StatusCodes.Status403Forbidden);
        app.MapPatch(prefix + "/access-reviews/{campaignId:guid}/items/{itemId:guid}/assignment",
            (ClaimsPrincipal principal, Guid organizationId, Guid campaignId, Guid itemId, ReassignReviewItemRequest request,
                HttpContext context, CancellationToken token) =>
            WithGovernanceAccess(principal, organizationId, store, true, async (tenant, identity) =>
            {
                if (request is null || request.ExpectedAssignmentVersion < 0 || string.IsNullOrWhiteSpace(request.Reviewer)
                    || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 2000 || request.DueAtUtc.ToUniversalTime() <= DateTime.UtcNow)
                    return BadRequest("invalid_governance_assignment", "A reviewer, future deadline, assignment version and reason (up to 2,000 characters) are required.");
                var correlation = Guid.TryParse(context.Request.Headers["X-Correlation-ID"].FirstOrDefault(), out var supplied)
                    && supplied != Guid.Empty ? supplied : Guid.NewGuid();
                var campaign = await store.UpdateAccessReviewAsync(tenant, organizationId, campaignId, current =>
                {
                    if (current is null) throw new KeyNotFoundException();
                    if (current.Status is CampaignStatus.Cancelled or CampaignStatus.Draft)
                        throw new InvalidOperationException("Only active review campaigns can be reassigned.");
                    var previous = current.Items.SingleOrDefault(item => item.Id == itemId) ?? throw new KeyNotFoundException();
                    if (previous.AssignmentVersion != request.ExpectedAssignmentVersion)
                        throw new InvalidOperationException("The assignment changed. Refresh before reassigning this item.");
                    var submitted = previous with { AssignedOwner = request.Owner, AssignedReviewer = request.Reviewer,
                        Team = request.Team, ExternalAccountId = request.ExternalAccountId, DueAt = request.DueAtUtc.ToUniversalTime() };
                    var incoming = current with { Items = current.Items.Select(item => item.Id == itemId ? submitted : item).ToArray() };
                    var updated = SanitizeReviewUpdate(current, incoming, identity, CreateService(store, identity),
                        organizationId, correlation, request.Reason);
                    return EnsureAuditMetadata(new IdentityGovernanceService().ReevaluateCampaign(updated), correlation);
                }, token);
                return Results.Ok(Envelope(campaign, correlation));
            })).RequireAuthorization("CiemApiAccess").WithName("ReassignGovernanceItem")
            .WithSummary("Audit owner/reviewer/team/account/deadline reassignment using optimistic concurrency; never change cloud access.")
            .Produces<ApiEnvelope<AccessReviewCampaign>>().Produces<ApiError>(400).Produces(403).Produces(404).Produces<ApiError>(409);
    }

    private static async Task<IResult> WithGovernanceAccess(ClaimsPrincipal principal, Guid organizationId,
        SharedWorkspaceStore store, bool write, Func<Guid, OrganizationIdentity, Task<IResult>> operation)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        try
        {
            var service = CreateService(store, identity);
            if (write ? !service.Authorize(organizationId, identity, OrganizationAction.ManageRemediations).Allowed
                : !service.Authorize(organizationId, identity, OrganizationAction.ViewFindings).Allowed
                    && !service.Authorize(organizationId, identity, OrganizationAction.ManageRemediations).Allowed)
                return Results.Forbid();
            return await operation(Guid.Parse(identity.TenantId), identity);
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentException exception) { return BadRequest("invalid_governance_request", exception.Message); }
        catch (InvalidOperationException exception) { return Results.Conflict(Error("governance_conflict", exception.Message)); }
    }
}
