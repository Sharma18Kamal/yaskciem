using System.Security.Claims;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.OrganizationManagement.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace CloudEntitle.LicenseServer.Services;

public static partial class SharedWorkspaceEndpoints
{
    private static void MapCentralMonitoringEndpoints(this WebApplication app, SharedWorkspaceStore store)
    {
        const string prefix = "/api/v1/organizations/{organizationId:guid}";
        app.MapGet(prefix + "/notification-rules", (ClaimsPrincipal principal, Guid organizationId, CancellationToken token) =>
            WithMonitoringAccess(principal, organizationId, store, false, async tenant =>
                Results.Ok(Envelope(await store.GetNotificationRulesAsync(tenant, organizationId, token)))))
            .RequireAuthorization("CiemApiAccess")
            .WithName("GetNotificationRules").WithSummary("List shared organization notification rules.")
            .Produces<ApiEnvelope<IReadOnlyList<SharedNotificationRuleDto>>>();
        app.MapPut(prefix + "/notification-rules/{ruleId:guid}", (ClaimsPrincipal principal, Guid organizationId,
            Guid ruleId, SharedNotificationRuleDto rule, CancellationToken token) =>
            WithMonitoringAccess(principal, organizationId, store, true, async tenant =>
            {
                if (rule is null || rule.RuleId != ruleId) return BadRequest("invalid_notification_rule", "The route and rule identifiers must match.");
                await store.SaveNotificationRuleAsync(tenant, organizationId, rule, token);
                return Results.Ok(Envelope(rule));
            })).RequireAuthorization("CiemApiAccess")
            .WithName("SaveNotificationRule").WithSummary("Create or replace a notification rule using a server channel alias.")
            .Produces<ApiEnvelope<SharedNotificationRuleDto>>()
            .Produces<ApiError>(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status403Forbidden);
        app.MapGet(prefix + "/drift-events", (ClaimsPrincipal principal, Guid organizationId, int? limit, CancellationToken token) =>
            WithMonitoringAccess(principal, organizationId, store, false, async tenant =>
                Results.Ok(Envelope(await store.GetDriftEventsAsync(tenant, organizationId, limit ?? 100, token)))))
            .RequireAuthorization("CiemApiAccess")
            .WithName("GetCentralDriftEvents").WithSummary("List finding and effective-access changes across comparable scans.")
            .Produces<ApiEnvelope<IReadOnlyList<SharedDriftEventDto>>>();
        app.MapGet(prefix + "/notification-deliveries", (ClaimsPrincipal principal, Guid organizationId, int? limit, CancellationToken token) =>
            WithMonitoringAccess(principal, organizationId, store, false, async tenant =>
                Results.Ok(Envelope(await store.GetNotificationDeliveriesAsync(tenant, organizationId, limit ?? 100, token)))))
            .RequireAuthorization("CiemApiAccess")
            .WithName("GetNotificationDeliveries").WithSummary("List shared delivery and retry states.")
            .Produces<ApiEnvelope<IReadOnlyList<SharedDeliveryDto>>>();
        app.MapGet(prefix + "/notification-deliveries/{deliveryId:guid}", (ClaimsPrincipal principal, Guid organizationId,
            Guid deliveryId, CancellationToken token) =>
            WithMonitoringAccess(principal, organizationId, store, false, async tenant =>
            {
                var status = await store.GetNotificationDeliveryStatusAsync(tenant, organizationId, deliveryId, token);
                return status is null ? Results.NotFound() : Results.Ok(Envelope(status));
            })).RequireAuthorization("CiemApiAccess")
            .WithName("GetNotificationDeliveryStatus").WithSummary("Inspect a delivery and its safe attempt outcomes.")
            .Produces<ApiEnvelope<SharedDeliveryStatusDto>>().Produces(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> WithMonitoringAccess(ClaimsPrincipal principal, Guid organizationId,
        SharedWorkspaceStore store, bool write, Func<Guid, Task<IResult>> operation)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        try
        {
            var service = CreateService(store, identity);
            if (write
                ? !service.Authorize(organizationId, identity, OrganizationAction.ManagePolicies).Allowed
                : !service.Authorize(organizationId, identity, OrganizationAction.ViewFindings).Allowed
                    || !service.Authorize(organizationId, identity, OrganizationAction.ViewInventory).Allowed)
                return Results.Forbid();
            return await operation(Guid.Parse(identity.TenantId));
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentException exception) { return BadRequest("invalid_monitoring_request", exception.Message); }
    }
}
