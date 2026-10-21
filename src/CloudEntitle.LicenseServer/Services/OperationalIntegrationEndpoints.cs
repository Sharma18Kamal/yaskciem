using System.Security.Claims;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.OrganizationManagement.Models;

namespace CloudEntitle.LicenseServer.Services;

public static partial class SharedWorkspaceEndpoints
{
    private static void MapOperationalIntegrationEndpoints(this WebApplication app, SharedWorkspaceStore store)
    {
        const string prefix = "/api/v1/organizations/{organizationId:guid}";
        app.MapGet(prefix + "/connectors/health", (ClaimsPrincipal principal, Guid organizationId,
            IConfiguration configuration, CancellationToken token) =>
            WithMonitoringAccess(principal, organizationId, store, false, async tenant =>
                Results.Ok(Envelope(await store.GetConnectorHealthAsync(tenant, organizationId, configuration, token)))))
            .RequireAuthorization("CiemApiAccess").WithName("GetConnectorHealth")
            .WithSummary("Read passive connector configuration health, delivery latency, retry and dead-letter totals without exposing credentials.")
            .Produces<ApiEnvelope<IReadOnlyList<ConnectorHealthDto>>>().Produces(403);
        app.MapGet(prefix + "/operational-events", (ClaimsPrincipal principal, Guid organizationId, int? limit, CancellationToken token) =>
            WithMonitoringAccess(principal, organizationId, store, false, async tenant =>
                Results.Ok(Envelope(await store.GetDriftEventsAsync(tenant, organizationId, limit ?? 100, token)))))
            .RequireAuthorization("CiemApiAccess").WithName("GetOperationalEvents")
            .WithSummary("List standardized finding, access, review-overdue and terminal scan-failure events.")
            .Produces<ApiEnvelope<IReadOnlyList<SharedDriftEventDto>>>();
        app.MapPost(prefix + "/operational-events/{eventId:guid}/ticket", async (ClaimsPrincipal principal, Guid organizationId,
            Guid eventId, CreateOperationalTicketRequest request, IConfiguration configuration, CancellationToken token) =>
        {
            if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
            try
            {
                var service = CreateService(store, identity);
                if (!service.Authorize(organizationId, identity, OrganizationAction.ManageRemediations).Allowed
                    || !service.Authorize(organizationId, identity, OrganizationAction.ViewInventory).Allowed
                    || !service.Authorize(organizationId, identity, OrganizationAction.ViewFindings).Allowed) return Results.Forbid();
                if (request is null || string.IsNullOrEmpty(request.Channel) || request.Channel.Length > 64
                    || request.Channel.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
                    return BadRequest("invalid_connector", "A configured Jira channel alias is required.");
                var tenant = Guid.Parse(identity.TenantId);
                var channel = configuration.GetSection($"CentralNotifications:Organizations:{tenant:D}:{organizationId:D}:Channels:{request.Channel}");
                if (ConnectorConfiguration.Status(channel, "Jira") != "Configured")
                    return BadRequest("invalid_connector", "The organization Jira channel is not configured.");
                var status = await store.RequestOperationalTicketAsync(tenant, organizationId, eventId, request.Channel, identity.UserId, token);
                return Results.Accepted($"/api/v1/organizations/{organizationId:D}/notification-deliveries/{status.Delivery.DeliveryId:D}", Envelope(status));
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (ArgumentException exception) { return BadRequest("invalid_ticket_request", exception.Message); }
            catch (InvalidOperationException exception) { return Results.Conflict(Error("ticket_conflict", exception.Message)); }
        }).RequireAuthorization("CiemApiAccess").WithName("RequestOperationalTicket")
            .WithSummary("Explicitly queue a deduplicated Jira ticket for an existing event; never create tickets inside HTTP handlers.")
            .Produces<ApiEnvelope<SharedDeliveryStatusDto>>(202).Produces<ApiError>(400).Produces(403).Produces(404).Produces<ApiError>(409);
    }
}
