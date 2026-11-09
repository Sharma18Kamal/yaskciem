using System.Security.Claims;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.OrganizationManagement.Models;
using Npgsql;

namespace CloudEntitle.LicenseServer.Services;

public static partial class SharedWorkspaceEndpoints
{
    private static void MapPrivilegedAccessEndpoints(this WebApplication app, SharedWorkspaceStore store)
    {
        app.MapJitRevocation(store);
        const string prefix = "/api/v1/organizations/{organizationId:guid}/privileged-access";
        app.MapGet(prefix + "/{requestId:guid}", (ClaimsPrincipal principal, Guid organizationId, Guid requestId, CancellationToken token) =>
            WithGovernanceAccess(principal, organizationId, store, false, async (tenant, _) =>
            {
                var request = await store.GetPrivilegedAccessAsync(tenant, organizationId, requestId, token);
                return request is null ? Results.NotFound() : Results.Ok(Envelope(request));
            })).RequireAuthorization("CiemApiAccess").WithName("GetPrivilegedAccessRequest")
            .WithSummary("Read a scoped saved JIT request without contacting Azure.").Produces<ApiEnvelope<PrivilegedAccessRequest>>().Produces(404);
        app.MapGet(prefix, (ClaimsPrincipal principal, Guid organizationId, CancellationToken token) =>
            WithGovernanceAccess(principal, organizationId, store, false, async (tenant, _) =>
                Results.Ok(Envelope(await store.GetPrivilegedAccessAsync(tenant, organizationId, token)))))
            .RequireAuthorization("CiemApiAccess").WithName("ListPrivilegedAccess")
            .WithSummary("List the latest 100 organization JIT requests; no cloud calls.").Produces<ApiEnvelope<IReadOnlyList<PrivilegedAccessRequest>>>();
        app.MapGet(prefix + "/{requestId:guid}/audit", (ClaimsPrincipal principal, Guid organizationId, Guid requestId, CancellationToken token) =>
            WithGovernanceAccess(principal, organizationId, store, false, async (tenant, _) =>
                Results.Ok(Envelope(await store.GetPrivilegedAccessAuditAsync(tenant, organizationId, requestId, token)))))
            .RequireAuthorization("CiemApiAccess").WithName("GetPrivilegedAccessAudit")
            .WithSummary("Read append-only JIT audit events, up to 500, without contacting Azure.").Produces<ApiEnvelope<IReadOnlyList<PrivilegedAccessAudit>>>();
        app.MapPost(prefix, (ClaimsPrincipal principal, Guid organizationId, CreatePrivilegedAccessRequest request,
            IConfiguration configuration, CancellationToken token) =>
            WithGovernanceAccess(principal, organizationId, store, false, async (tenant, actor) =>
            {
                if (!(principal.FindFirstValue("scp") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("access_as_user", StringComparer.Ordinal))
                    return Results.Forbid();
                if (request is null) throw new ArgumentException("A JIT request is required.");
                var service = CreateService(store, actor);
                if (!PrivilegedAccessPolicy.Authorized(service, organizationId, actor, false)) return Results.Forbid();
                PrivilegedAccessPolicy.ValidateReason(request.Reason);
                var target = PrivilegedAccessPolicy.Target(configuration, tenant, organizationId, request.Target);
                var approvers = PrivilegedAccessPolicy.Approvers(configuration, tenant, organizationId);
                var requiredApprovals = PrivilegedAccessPolicy.RequiredApprovals(configuration, tenant, organizationId);
                if (approvers.Count < requiredApprovals)
                    throw new ArgumentException("The configured approver list is smaller than RequiredApprovals.");
                if (approvers.Contains(actor.UserId)) throw new ArgumentException("Requesters cannot approve their own access.");
                if (!target.Connected) throw new InvalidOperationException("The client must configure and explicitly connect this JIT target first.");
                if (!service.GetTenantBindings(organizationId, actor).Any(binding => binding.Provider == target.Provider
                    && string.Equals(binding.ExternalTenantId, target.SubscriptionId, StringComparison.OrdinalIgnoreCase))) return Results.Forbid();
                if (request.DurationMinutes < 1 || request.DurationMinutes > (request.Emergency ? target.EmergencyMaximumMinutes : target.MaximumMinutes))
                    throw new ArgumentException("The requested duration exceeds the configured standard/emergency maximum.");
                var id = Guid.NewGuid();
                var now = DateTimeOffset.UtcNow;
                var human = PrivilegedAccessPolicy.Human(configuration, tenant, organizationId, target, actor.UserId);
                var risk = PrivilegedAccessPolicy.AssessRisk(target, request.DurationMinutes, request.Emergency, now);
                PrivilegedAccessRequest created;
                try
                {
                    created = await store.UpdatePrivilegedAccessAsync(tenant, organizationId, id, actor.UserId, "Requested", request.Reason,
                        _ => new(id, actor.UserId, target.Alias, target.SubscriptionId, target.Scope, target.RoleDefinitionId,
                            request.DurationMinutes, request.Emergency, request.Reason.Trim(), now, now.AddMinutes(30), "PendingApproval", 0, [],
                            Provider: target.Provider, HumanPrincipal: human.Principal, BrokerEndpoint: target.BrokerEndpoint,
                            AuthSecretKey: string.IsNullOrEmpty(human.SecretKey) ? null : human.SecretKey,
                            RevocationAuthSecretKey: target.RevocationAuthSecretKey)
                            { RequiredApprovals = requiredApprovals, EligibleApprovers = approvers, RiskAssessment = risk }, token);
                }
                catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation
                    && string.Equals(exception.ConstraintName, "UX_SharedPrivilegedAccessRequests_LiveGrant",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Conflict(Error("jit_request_already_active",
                        "A matching JIT request or grant is already active. Refresh the request list before creating another."));
                }
                return Results.Created($"/api/v1/organizations/{organizationId:D}/privileged-access/{id:D}", Envelope(created));
            })).RequireAuthorization("CiemApiAccess").WithName("RequestPrivilegedAccess")
            .WithSummary("Request caller-owned Azure PIM, AWS broker human sessions or GCP PAM from a connected allowlist; configured distinct non-requester approvals are required.")
            .Produces<ApiEnvelope<PrivilegedAccessRequest>>(201).Produces(400).Produces(403).Produces(409);
        foreach (var action in new[] { "approve", "reject", "activate" })
        {
            var decision = action;
            app.MapPost(prefix + "/{requestId:guid}/" + decision, (ClaimsPrincipal principal, Guid organizationId, Guid requestId,
                PrivilegedAccessDecision request, IConfiguration configuration, CancellationToken token) =>
                WithGovernanceAccess(principal, organizationId, store, decision != "activate", async (tenant, actor) =>
                {
                    if (request is null) throw new ArgumentException("A versioned decision is required.");
                    if (!(principal.FindFirstValue("scp") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("access_as_user", StringComparer.Ordinal))
                        return Results.Forbid();
                    var service = CreateService(store, actor);
                    if (!PrivilegedAccessPolicy.Authorized(service, organizationId, actor, decision != "activate")) return Results.Forbid();
                    PrivilegedAccessPolicy.ValidateReason(request.Reason);
                    if (decision != "activate" && !PrivilegedAccessPolicy.IsApprover(configuration, tenant, organizationId, actor.UserId)) return Results.Forbid();
                    var updated = await store.UpdatePrivilegedAccessAsync(tenant, organizationId, requestId, actor.UserId, decision, request.Reason, previous =>
                    {
                        var current = previous ?? throw new KeyNotFoundException();
                        if (current.Version != request.ExpectedVersion) throw new InvalidOperationException("The JIT request changed. Refresh before deciding.");
                        if (current.ApprovalDeadlineUtc <= DateTimeOffset.UtcNow) throw new InvalidOperationException("The approval/activation window expired. Create a new request.");
                        if (decision == "activate")
                        {
                            if (current.RequestedBy != actor.UserId) throw new UnauthorizedAccessException();
                            if (current.Status != "Approved" || !request.LiveConsent) throw new InvalidOperationException("The configured approval threshold and explicit live activation consent are required.");
                            ValidateJitTarget(configuration, tenant, organizationId, current);
                            return current with { Status = "Queued", Version = current.Version + 1 };
                        }

                        if (current.RequestedBy == actor.UserId) throw new UnauthorizedAccessException();
                        if (current.Status is not ("PendingApproval" or "Approved")) throw new InvalidOperationException("Only unactivated requests may be decided.");
                        if (decision == "reject") return current with { Status = "Rejected", Version = current.Version + 1 };
                        var eligibleApprovers = current.EligibleApprovers.Count == 0
                            ? PrivilegedAccessPolicy.Approvers(configuration, tenant, organizationId)
                            : current.EligibleApprovers;
                        if (!eligibleApprovers.Contains(actor.UserId))
                            throw new UnauthorizedAccessException("This identity was not an approver for this request.");
                        if (current.ApprovedBy.Contains(actor.UserId)) throw new InvalidOperationException("This approver already decided; duplicate approvals cannot satisfy dual control.");
                        var approvals = current.ApprovedBy.Append(actor.UserId).ToArray();
                        return current with { ApprovedBy = approvals, EligibleApprovers = eligibleApprovers,
                            Status = approvals.Length >= current.RequiredApprovals ? "Approved" : "PendingApproval", Version = current.Version + 1 };
                    }, token);
                    return Results.Ok(Envelope(updated));
                })).RequireAuthorization("CiemApiAccess").WithName("PrivilegedAccess" + decision)
                .WithSummary("Apply a versioned JIT decision; activation queues an explicitly consented native PIM operation.")
                .Produces<ApiEnvelope<PrivilegedAccessRequest>>().Produces(400).Produces(403).Produces(404).Produces(409);
        }
    }

    private static void MapJitRevocation(this WebApplication app, SharedWorkspaceStore store)
    {
        app.MapPost("/api/v1/organizations/{organizationId:guid}/privileged-access/{requestId:guid}/revoke",
            (ClaimsPrincipal principal, Guid organizationId, Guid requestId, PrivilegedAccessDecision request,
                IConfiguration configuration, CancellationToken token) =>
                WithGovernanceAccess(principal, organizationId, store, false, async (tenant, actor) =>
                {
                    if (request is null) throw new ArgumentException("A versioned decision is required.");
                    if (!(principal.FindFirstValue("scp") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Contains("access_as_user", StringComparer.Ordinal)) return Results.Forbid();
                    var service = CreateService(store, actor);
                    var approver = PrivilegedAccessPolicy.IsApprover(configuration, tenant, organizationId, actor.UserId)
                        && PrivilegedAccessPolicy.Authorized(service, organizationId, actor, true);
                    await using var lease = await store.LockPrivilegedAccessAsync(tenant, organizationId, requestId, token);
                    var result = await store.UpdatePrivilegedAccessAsync(tenant, organizationId, requestId, actor.UserId,
                        "revoke", request.Reason, previous =>
                            PrivilegedAccessPolicy.Revoke(previous ?? throw new KeyNotFoundException(), actor.UserId, request, approver), token);
                    return Results.Ok(Envelope(result));
                })).RequireAuthorization("CiemApiAccess").WithName("RevokePrivilegedAccess")
            .WithSummary("Requester ends own grant or one current authorized configured approver revokes it, with consent/reason/version. Unissued requests cancel without cloud calls.")
            .Produces<ApiEnvelope<PrivilegedAccessRequest>>().Produces(400).Produces(403).Produces(404).Produces(409);
    }

    internal static void ValidateJitTarget(IConfiguration configuration, Guid tenant, Guid organization, PrivilegedAccessRequest request)
    {
        var target = PrivilegedAccessPolicy.Target(configuration, tenant, organization, request.Target);
        var human = PrivilegedAccessPolicy.Human(configuration, tenant, organization, target, request.RequestedBy);
        var requiredApprovals = PrivilegedAccessPolicy.RequiredApprovals(configuration, tenant, organization);
        var approvers = PrivilegedAccessPolicy.Approvers(configuration, tenant, organization);
        var eligibleApprovers = request.EligibleApprovers.Count == 0 ? approvers : request.EligibleApprovers;
        if (!target.Connected || target.SubscriptionId != request.SubscriptionId || target.Scope != request.Scope
            || target.Provider != request.Provider || target.BrokerEndpoint != request.BrokerEndpoint
            || (request.Provider != CloudProvider.Azure && (human.Principal != request.HumanPrincipal
                || human.SecretKey != request.AuthSecretKey || target.RevocationAuthSecretKey != request.RevocationAuthSecretKey))
            || target.RoleDefinitionId != request.RoleDefinitionId || request.DurationMinutes > (request.Emergency ? target.EmergencyMaximumMinutes : target.MaximumMinutes)
            || request.RequiredApprovals != requiredApprovals || approvers.Count < requiredApprovals
            || !eligibleApprovers.Order().SequenceEqual(approvers.Order())
            || request.ApprovedBy.Count < requiredApprovals || request.ApprovedBy.Distinct().Count() != request.ApprovedBy.Count
            || request.ApprovedBy.Any(id => id == request.RequestedBy || !eligibleApprovers.Contains(id)))
            throw new InvalidOperationException("The configured target or approval policy changed; create a new request.");
    }
}
