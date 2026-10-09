using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using CloudEntitle.Domain;
using CloudEntitle.IdentityGovernance.Models;
using CloudEntitle.IdentityGovernance.Services;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.OrganizationManagement.Models;
using CloudEntitle.OrganizationManagement.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace CloudEntitle.LicenseServer.Services;

public static class SharedWorkspaceEndpoints
{
    public static void MapSharedWorkspaceEndpoints(this WebApplication app, SharedWorkspaceStore store)
    {
        app.MapGet("/api/v1/organizations", (ClaimsPrincipal principal) => GetOrganizations(principal, store))
            .RequireAuthorization("CiemApiAccess");
        app.MapPost("/api/v1/organizations", (ClaimsPrincipal principal, CreateOrganizationRequest request) => CreateOrganization(principal, request, store))
            .RequireAuthorization("CiemApiAccess");
        app.MapGet("/api/v1/organizations/{organizationId:guid}/workspace", (ClaimsPrincipal principal, Guid organizationId) => GetWorkspace(principal, organizationId, store))
            .RequireAuthorization("CiemApiAccess");
        app.MapPost("/api/v1/organizations/{organizationId:guid}/members", (ClaimsPrincipal principal, Guid organizationId, InviteOrganizationMemberRequest request) => InviteMember(principal, organizationId, request, store))
            .RequireAuthorization("CiemApiAccess");
        app.MapPut("/api/v1/organizations/{organizationId:guid}/members/{memberId:guid}/role", (ClaimsPrincipal principal, Guid organizationId, Guid memberId, UpdateOrganizationMemberRoleRequest request) => UpdateMemberRole(principal, organizationId, memberId, request, store))
            .RequireAuthorization("CiemApiAccess");
        app.MapPut("/api/v1/organizations/{organizationId:guid}/members/{memberId:guid}/status", (ClaimsPrincipal principal, Guid organizationId, Guid memberId, SetOrganizationMemberStatusRequest request) => SetMemberStatus(principal, organizationId, memberId, request, store))
            .RequireAuthorization("CiemApiAccess");
        app.MapPost("/api/v1/organizations/{organizationId:guid}/bindings", (ClaimsPrincipal principal, Guid organizationId, BindOrganizationAccountRequest request) => BindAccount(principal, organizationId, request, store))
            .RequireAuthorization("CiemApiAccess");
        app.MapPost("/api/v1/organizations/{organizationId:guid}/custom-roles", (ClaimsPrincipal principal, Guid organizationId, SaveOrganizationCustomRoleRequest request) => SaveCustomRole(principal, organizationId, null, request, store))
            .RequireAuthorization("CiemApiAccess");
        app.MapPut("/api/v1/organizations/{organizationId:guid}/custom-roles/{roleId:guid}", (ClaimsPrincipal principal, Guid organizationId, Guid roleId, SaveOrganizationCustomRoleRequest request) => SaveCustomRole(principal, organizationId, roleId, request, store))
            .RequireAuthorization("CiemApiAccess");
        app.MapDelete("/api/v1/organizations/{organizationId:guid}/custom-roles/{roleId:guid}", (ClaimsPrincipal principal, Guid organizationId, Guid roleId) => DeleteCustomRole(principal, organizationId, roleId, store))
            .RequireAuthorization("CiemApiAccess");
        app.MapGet("/api/v1/organizations/{organizationId:guid}/audit-history", (ClaimsPrincipal principal, Guid organizationId) => GetOrganizationAudit(principal, organizationId, store))
            .RequireAuthorization("CiemApiAccess");
        app.MapPost("/api/v1/organizations/{organizationId:guid}/scan-summaries", (ClaimsPrincipal principal, Guid organizationId, UploadSharedScanSummaryRequest request, SharedWorkspaceStore workspaceStore, CancellationToken cancellationToken) => UploadScanSummary(principal, organizationId, request, workspaceStore, cancellationToken))
            .RequireAuthorization("CiemApiAccess");
        app.MapGet("/api/v1/organizations/{organizationId:guid}/scan-summaries", (ClaimsPrincipal principal, Guid organizationId, int limit, SharedWorkspaceStore workspaceStore, CancellationToken cancellationToken) => GetScanSummaries(principal, organizationId, limit, workspaceStore, cancellationToken))
            .RequireAuthorization("CiemApiAccess");
        app.MapGet("/api/v1/organizations/{organizationId:guid}/access-reviews", (ClaimsPrincipal principal, Guid organizationId, SharedWorkspaceStore workspaceStore, CancellationToken cancellationToken) => GetAccessReviews(principal, organizationId, workspaceStore, cancellationToken))
            .RequireAuthorization("CiemApiAccess");
        app.MapPut("/api/v1/organizations/{organizationId:guid}/access-reviews/{campaignId:guid}", (ClaimsPrincipal principal, Guid organizationId, Guid campaignId, AccessReviewCampaign campaign, SharedWorkspaceStore workspaceStore, CancellationToken cancellationToken) => SaveAccessReview(principal, organizationId, campaignId, campaign, workspaceStore, cancellationToken))
            .RequireAuthorization("CiemApiAccess");
    }

    private static IResult GetOrganizations(ClaimsPrincipal principal, SharedWorkspaceStore store)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        var service = CreateService(store, identity);
        foreach (var organization in service.GetOrganizations().Where(item => item.EntraTenantId.Equals(identity.TenantId, StringComparison.OrdinalIgnoreCase)))
            service.AcceptInvitation(organization.Id, identity);
        return Results.Ok(Envelope(service.GetOrganizationsForIdentity(identity)));
    }

    private static IResult CreateOrganization(ClaimsPrincipal principal, CreateOrganizationRequest request, SharedWorkspaceStore store)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        try
        {
            var organization = CreateService(store, identity).CreateOrganization(request.Name, request.TenantKey, identity.TenantId, identity);
            return Results.Created($"/api/v1/organizations/{organization.Id:D}", Envelope(organization));
        }
        catch (ArgumentException exception) { return BadRequest("invalid_organization", exception.Message); }
        catch (InvalidOperationException exception) { return Results.Conflict(Error("organization_conflict", exception.Message)); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
    }

    private static IResult GetWorkspace(ClaimsPrincipal principal, Guid organizationId, SharedWorkspaceStore store)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        try
        {
            var service = CreateService(store, identity);
            service.AcceptInvitation(organizationId, identity);
            var organization = service.GetOrganizationsForIdentity(identity).FirstOrDefault(item => item.Id == organizationId);
            var member = service.GetCurrentMember(organizationId, identity);
            if (organization is null || member is null) return Results.NotFound();
            var allowed = Enum.GetValues<OrganizationAction>().Where(action => service.Authorize(organizationId, identity, action).Allowed).ToArray();
            var canManageMembers = allowed.Contains(OrganizationAction.ManageMembers);
            var canViewInventory = allowed.Contains(OrganizationAction.ViewInventory);
            return Results.Ok(Envelope(new OrganizationWorkspaceDto(organization, member, allowed,
                canManageMembers ? service.GetMembers(organizationId, identity) : Array.Empty<OrganizationMember>(),
                canViewInventory ? service.GetTenantBindings(organizationId, identity) : Array.Empty<TenantBinding>(),
                canManageMembers ? service.GetCustomRoles(organizationId, identity) : Array.Empty<OrganizationCustomRole>(),
                canManageMembers ? service.GetAuditTrail(organizationId, identity) : Array.Empty<OrganizationAuditEntry>())));
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static IResult InviteMember(ClaimsPrincipal principal, Guid organizationId, InviteOrganizationMemberRequest request, SharedWorkspaceStore store)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        try
        {
            var member = CreateService(store, identity).AddMember(organizationId, identity, request.Email, request.Role);
            return Results.Created($"/api/v1/organizations/{organizationId:D}/members/{member.Id:D}", Envelope(member));
        }
        catch (ArgumentException exception) { return BadRequest("invalid_member", exception.Message); }
        catch (InvalidOperationException exception) { return Results.Conflict(Error("member_conflict", exception.Message)); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static IResult UpdateMemberRole(ClaimsPrincipal principal, Guid organizationId, Guid memberId, UpdateOrganizationMemberRoleRequest request, SharedWorkspaceStore store)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        try { return Results.Ok(Envelope(CreateService(store, identity).UpdateMemberRole(organizationId, identity, memberId, request.Role))); }
        catch (ArgumentException exception) { return BadRequest("invalid_role", exception.Message); }
        catch (InvalidOperationException exception) { return Results.Conflict(Error("role_conflict", exception.Message)); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static IResult SetMemberStatus(ClaimsPrincipal principal, Guid organizationId, Guid memberId, SetOrganizationMemberStatusRequest request, SharedWorkspaceStore store)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        try { return Results.Ok(Envelope(CreateService(store, identity).SetMemberActive(organizationId, identity, memberId, request.IsActive))); }
        catch (InvalidOperationException exception) { return Results.Conflict(Error("member_status_conflict", exception.Message)); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static async Task<IResult> BindAccount(ClaimsPrincipal principal, Guid organizationId, BindOrganizationAccountRequest request, SharedWorkspaceStore store)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        OrganizationManagementService service;
        try
        {
            service = CreateService(store, identity);
            if (!service.Authorize(organizationId, identity, OrganizationAction.ManageConnections).Allowed) return Results.Forbid();
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        if (request is null || string.IsNullOrWhiteSpace(request.ExternalAccountId) || request.CloudAccountId == Guid.Empty)
            return BadRequest("invalid_binding", "Cloud account and external account ID are required.");
        if (await store.IsAccountBoundAsync(request.Provider, request.ExternalAccountId))
            return Results.Conflict(Error("account_already_bound", "This provider account is already bound to an organization."));
        try { return Results.Created($"/api/v1/organizations/{organizationId:D}/bindings", Envelope(service.BindCloudAccount(organizationId, identity, request.Provider, request.CloudAccountId, request.ExternalAccountId, request.DisplayName))); }
        catch (ArgumentException exception) { return BadRequest("invalid_binding", exception.Message); }
        catch (InvalidOperationException exception) { return Results.Conflict(Error("binding_conflict", exception.Message)); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static IResult SaveCustomRole(ClaimsPrincipal principal, Guid organizationId, Guid? roleId, SaveOrganizationCustomRoleRequest request, SharedWorkspaceStore store)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        try
        {
            var service = CreateService(store, identity);
            var role = roleId.HasValue
                ? service.UpdateCustomRole(organizationId, identity, roleId.Value, request.Name, request.Permissions)
                : service.CreateCustomRole(organizationId, identity, request.Name, request.Permissions);
            return Results.Ok(Envelope(role));
        }
        catch (ArgumentException exception) { return BadRequest("invalid_custom_role", exception.Message); }
        catch (InvalidOperationException exception) { return Results.Conflict(Error("custom_role_conflict", exception.Message)); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static IResult DeleteCustomRole(ClaimsPrincipal principal, Guid organizationId, Guid roleId, SharedWorkspaceStore store)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        try
        {
            CreateService(store, identity).DeleteCustomRole(organizationId, identity, roleId);
            return Results.NoContent();
        }
        catch (InvalidOperationException exception) { return Results.Conflict(Error("custom_role_conflict", exception.Message)); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static IResult GetOrganizationAudit(ClaimsPrincipal principal, Guid organizationId, SharedWorkspaceStore store)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        try { return Results.Ok(Envelope(CreateService(store, identity).GetAuditTrail(organizationId, identity))); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static async Task<IResult> UploadScanSummary(ClaimsPrincipal principal, Guid organizationId, UploadSharedScanSummaryRequest request, SharedWorkspaceStore store, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        if (request is null || request.Accounts is null || request.Coverage is null)
            return BadRequest("invalid_scan_summary", "Scan summary account and coverage lists are required.");
        OrganizationManagementService service;
        try
        {
            service = CreateService(store, identity);
            if (!service.Authorize(organizationId, identity, OrganizationAction.RunScan).Allowed) return Results.Forbid();
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        if (request.ScanId == Guid.Empty || request.CompletedAtUtc < request.StartedAtUtc || request.Accounts.Count > 200
            || request.Coverage.Count > 10000 || new[] { request.Identities, request.Resources, request.Roles, request.Permissions, request.EffectiveAccess, request.Findings, request.CriticalFindings, request.HighFindings }.Any(value => value < 0))
            return BadRequest("invalid_scan_summary", "Scan summary identifiers, times, counts, or collection sizes are invalid.");
        IReadOnlyList<TenantBinding> bindings;
        try { bindings = service.GetTenantBindings(organizationId, identity); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        if (request.Accounts.Any(account => account is null || string.IsNullOrWhiteSpace(account.ExternalAccountId)
                || !bindings.Any(binding => binding.Provider == account.Provider
                    && binding.ExternalTenantId.Equals(account.ExternalAccountId, StringComparison.OrdinalIgnoreCase))))
            return Results.Forbid();
        var providerLabel = string.Join(", ", request.Accounts.Select(account => account.Provider.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(provider => provider, StringComparer.OrdinalIgnoreCase));
        var summary = new SharedScanSummaryDto(request.ScanId, organizationId, Guid.Parse(identity.TenantId), identity.UserId,
            request.StartedAtUtc.ToUniversalTime(), request.CompletedAtUtc.ToUniversalTime(), providerLabel,
            request.Accounts, request.Identities, request.Resources, request.Roles, request.Permissions, request.EffectiveAccess,
            request.Findings, request.CriticalFindings, request.HighFindings, request.Coverage);
        await store.SaveScanSummaryAsync(summary, cancellationToken);
        return Results.Created($"/api/v1/organizations/{organizationId:D}/scan-summaries/{summary.ScanId:D}", Envelope(summary));
    }

    private static async Task<IResult> GetScanSummaries(ClaimsPrincipal principal, Guid organizationId, int limit, SharedWorkspaceStore store, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        try
        {
            if (!CreateService(store, identity).Authorize(organizationId, identity, OrganizationAction.ViewInventory).Allowed) return Results.Forbid();
            return Results.Ok(Envelope(await store.GetScanSummariesAsync(Guid.Parse(identity.TenantId), organizationId, limit, cancellationToken)));
        }
        catch (ArgumentOutOfRangeException exception) { return BadRequest("invalid_scan_summary_query", exception.Message); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static async Task<IResult> GetAccessReviews(ClaimsPrincipal principal, Guid organizationId, SharedWorkspaceStore store, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        try
        {
            var service = CreateService(store, identity);
            if (!service.Authorize(organizationId, identity, OrganizationAction.ViewFindings).Allowed
                && !service.Authorize(organizationId, identity, OrganizationAction.ManageRemediations).Allowed) return Results.Forbid();
            var campaigns = await store.GetAccessReviewsAsync(Guid.Parse(identity.TenantId), organizationId, cancellationToken);
            var governance = new IdentityGovernanceService();
            var reevaluated = campaigns.Select(campaign => governance.ReevaluateCampaign(campaign)).ToArray();
            for (var index = 0; index < campaigns.Count; index++)
            {
                if (HasReviewLifecycleChanges(campaigns[index], reevaluated[index]))
                    await store.SaveAccessReviewAsync(Guid.Parse(identity.TenantId), organizationId, reevaluated[index], cancellationToken);
            }
            return Results.Ok(Envelope(reevaluated));
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static async Task<IResult> SaveAccessReview(ClaimsPrincipal principal, Guid organizationId, Guid campaignId, AccessReviewCampaign campaign, SharedWorkspaceStore store, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        OrganizationManagementService service;
        try
        {
            service = CreateService(store, identity);
            if (!service.Authorize(organizationId, identity, OrganizationAction.ManageRemediations).Allowed) return Results.Forbid();
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        if (campaign is null || campaign.Items is null || campaign.Id != campaignId || campaignId == Guid.Empty || campaign.Items.Count is 0 or > 10000
            || campaign.Items.Any(item => item is null || item.Id == Guid.Empty || item.CampaignId != campaignId || !Enum.IsDefined(item.Decision)))
            return BadRequest("invalid_access_review", "Access review IDs or items are invalid.");
        var tenantId = Guid.Parse(identity.TenantId);
        var existing = (await store.GetAccessReviewsAsync(tenantId, organizationId, cancellationToken)).FirstOrDefault(item => item.Id == campaignId);
        try
        {
            var sanitized = existing is null ? SanitizeNewReview(campaign, identity) : SanitizeReviewUpdate(existing, campaign, identity);
            sanitized = new IdentityGovernanceService().ReevaluateCampaign(sanitized);
            await store.SaveAccessReviewAsync(tenantId, organizationId, sanitized, cancellationToken);
            return Results.Ok(Envelope(sanitized));
        }
        catch (ArgumentException exception) { return BadRequest("invalid_access_review", exception.Message); }
        catch (InvalidOperationException exception) { return Results.Conflict(Error("access_review_conflict", exception.Message)); }
    }

    private static AccessReviewCampaign SanitizeNewReview(AccessReviewCampaign campaign, OrganizationIdentity identity)
    {
        var now = DateTime.UtcNow;
        if (campaign.DueAt <= now) throw new ArgumentException("A shared review campaign requires a future due date.");
        var items = campaign.Items.Select(item => item with
        {
            AssignedReviewer = item.AssignedReviewer?.Trim(),
            OwnerSnapshot = item.OwnerSnapshot?.Trim(),
            Decision = ReviewDecision.Pending,
            Reviewer = null,
            ReviewComment = null,
            DecidedAt = null,
            ReminderCount = 0,
            LastReminderAt = null,
            Attestations = Array.Empty<AccessReviewAttestation>(),
            ExceptionExpiresAt = null,
            ExceptionExpiredAt = null,
            EscalatedAt = null,
            EscalationReason = null,
            AuditTrail = new[] { new AccessReviewAuditEntry(now, identity.Email, "ItemCreated", "Review item created by the shared service.") }
        }).ToArray();
        return campaign with
        {
            Status = CampaignStatus.Open,
            CreatedAt = now,
            CreatedBy = identity.Email,
            ReminderLeadDays = Math.Clamp(campaign.ReminderLeadDays, 1, 30),
            ReminderIntervalDays = Math.Clamp(campaign.ReminderIntervalDays, 1, 30),
            Items = items,
            AuditTrail = new[] { new AccessReviewAuditEntry(now, identity.Email, "CampaignCreated", $"{items.Length} review item(s) created.") }
        };
    }

    private static AccessReviewCampaign SanitizeReviewUpdate(AccessReviewCampaign existing, AccessReviewCampaign incoming, OrganizationIdentity identity)
    {
        var now = DateTime.UtcNow;
        if (!existing.Items.Select(item => item.Id).OrderBy(id => id).SequenceEqual(incoming.Items.Select(item => item.Id).OrderBy(id => id)))
            throw new InvalidOperationException("A shared review update cannot add or remove review items.");
        var audit = existing.AuditTrail.ToList();
        var updatedItems = new List<AccessReviewItem>(existing.Items.Count);
        foreach (var previous in existing.Items)
        {
            var submitted = incoming.Items.Single(item => item.Id == previous.Id);
            if (submitted.PrincipalId != previous.PrincipalId || submitted.EffectiveAccessId != previous.EffectiveAccessId
                || submitted.AccessPathId != previous.AccessPathId || submitted.Provider != previous.Provider
                || submitted.AccessSummary != previous.AccessSummary || submitted.EvidenceSnapshot != previous.EvidenceSnapshot
                || submitted.OwnerSnapshot != previous.OwnerSnapshot)
                throw new InvalidOperationException("A shared review update cannot alter its source evidence.");

            var itemAudit = previous.AuditTrail.ToList();
            var assignedReviewer = submitted.AssignedReviewer?.Trim();
            var itemDueAt = submitted.DueAt?.ToUniversalTime();
            if (!string.Equals(previous.AssignedReviewer, assignedReviewer, StringComparison.OrdinalIgnoreCase) || previous.DueAt != itemDueAt)
            {
                itemAudit.Add(new AccessReviewAuditEntry(now, identity.Email, "ReviewerAssigned", $"Assigned reviewer: {assignedReviewer ?? "unassigned"}; due: {itemDueAt:O}."));
                audit.Add(new AccessReviewAuditEntry(now, identity.Email, "ReviewerAssigned", $"Item {previous.Id:D}; reviewer {assignedReviewer ?? "unassigned"}."));
            }

            var decision = previous.Decision;
            var reviewer = previous.Reviewer;
            var comment = previous.ReviewComment;
            var decidedAt = previous.DecidedAt;
            var exceptionExpiresAt = previous.ExceptionExpiresAt;
            var attestations = previous.Attestations;
            if (submitted.Decision != previous.Decision)
            {
                if (submitted.Decision == ReviewDecision.Pending) throw new InvalidOperationException("A recorded access-review decision cannot be reset to Pending.");
                if (submitted.Decision != ReviewDecision.Certified && string.IsNullOrWhiteSpace(submitted.ReviewComment))
                    throw new ArgumentException("A comment is required for investigation, rejection, and exception decisions.");
                if (submitted.Decision == ReviewDecision.ExceptionGranted
                    && (!submitted.ExceptionExpiresAt.HasValue || submitted.ExceptionExpiresAt.Value.ToUniversalTime() <= now))
                    throw new ArgumentException("An exception requires a future expiration time.");
                decision = submitted.Decision;
                reviewer = identity.Email;
                comment = submitted.ReviewComment is null ? null : CloudEntitle.Security.Services.SensitiveDataRedactor.RedactText(submitted.ReviewComment.Trim());
                decidedAt = now;
                exceptionExpiresAt = decision == ReviewDecision.ExceptionGranted ? submitted.ExceptionExpiresAt?.ToUniversalTime() : null;
                var evidenceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(previous.EvidenceSnapshot))).ToLowerInvariant();
                var attestation = new AccessReviewAttestation(identity.Email, identity.Email, decision, comment ?? string.Empty,
                    now, evidenceHash, previous.EvidenceSnapshot);
                attestations = attestations.Append(attestation).ToArray();
                itemAudit.Add(new AccessReviewAuditEntry(now, identity.Email, "AttestationRecorded", $"{decision} attested against evidence SHA-256 {evidenceHash}."));
                audit.Add(new AccessReviewAuditEntry(now, identity.Email, "AttestationRecorded", $"Item {previous.Id:D}; evidence SHA-256 {evidenceHash}."));
                var action = $"DecisionRecorded:{decision}";
                itemAudit.Add(new AccessReviewAuditEntry(now, identity.Email, action, comment ?? "No comment supplied."));
                audit.Add(new AccessReviewAuditEntry(now, identity.Email, action, $"Item {previous.Id:D}; {comment ?? "No comment supplied."}"));
            }

            updatedItems.Add(previous with
            {
                AssignedReviewer = assignedReviewer,
                DueAt = itemDueAt,
                Decision = decision,
                Reviewer = reviewer,
                ReviewComment = comment,
                DecidedAt = decidedAt,
                Attestations = attestations,
                ExceptionExpiresAt = exceptionExpiresAt,
                ExceptionExpiredAt = previous.ExceptionExpiredAt,
                EscalatedAt = previous.EscalatedAt,
                EscalationReason = previous.EscalationReason,
                AuditTrail = itemAudit
            });
        }

        var name = string.IsNullOrWhiteSpace(incoming.Name) ? existing.Name : incoming.Name.Trim();
        var dueAt = incoming.DueAt > now ? incoming.DueAt.ToUniversalTime() : existing.DueAt;
        if (name != existing.Name || dueAt != existing.DueAt)
            audit.Add(new AccessReviewAuditEntry(now, identity.Email, "CampaignUpdated", $"Name={name}; DueAt={dueAt:O}."));
        var status = updatedItems.All(item => item.Decision != ReviewDecision.Pending) ? CampaignStatus.Completed : CampaignStatus.Open;
        return existing with { Name = name, DueAt = dueAt, Status = status, Items = updatedItems, AuditTrail = audit };
    }

    private static OrganizationManagementService CreateService(SharedWorkspaceStore store, OrganizationIdentity identity)
        => new(store.CreateOrganizationStore(Guid.Parse(identity.TenantId)));

    private static bool HasReviewLifecycleChanges(AccessReviewCampaign before, AccessReviewCampaign after)
        => before.Status != after.Status || before.AuditTrail.Count != after.AuditTrail.Count
            || before.Items.Zip(after.Items).Any(pair => pair.First.ExceptionExpiredAt != pair.Second.ExceptionExpiredAt
                || pair.First.EscalatedAt != pair.Second.EscalatedAt
                || pair.First.AuditTrail.Count != pair.Second.AuditTrail.Count);

    private static bool TryGetIdentity(ClaimsPrincipal principal, out OrganizationIdentity identity)
    {
        identity = null!;
        if (!Guid.TryParse(principal.FindFirstValue("tid"), out var tenantId) || tenantId == Guid.Empty
            || !Guid.TryParse(principal.FindFirstValue("oid"), out var userId) || userId == Guid.Empty) return false;
        var subject = principal.FindFirstValue("sub") ?? userId.ToString("D");
        var email = principal.FindFirstValue(ClaimTypes.Email) ?? principal.FindFirstValue("email")
            ?? principal.FindFirstValue("preferred_username") ?? principal.FindFirstValue("upn");
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(email)) return false;
        identity = new OrganizationIdentity(userId, subject, tenantId.ToString("D"), email.Trim());
        return true;
    }

    private static ApiEnvelope<T> Envelope<T>(T data) => new(data, Guid.NewGuid().ToString("N"), ApiContractVersion.Current);
    private static IResult BadRequest(string code, string message) => Results.BadRequest(Error(code, message));
    private static ApiError Error(string code, string message) => new(code, message, Guid.NewGuid().ToString("N"));
}
