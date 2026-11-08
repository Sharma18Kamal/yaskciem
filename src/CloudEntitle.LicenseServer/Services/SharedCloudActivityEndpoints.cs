using System.Security.Claims;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.OrganizationManagement.Models;

namespace CloudEntitle.LicenseServer.Services;

public static partial class SharedWorkspaceEndpoints
{
    private static void MapCloudActivityEndpoints(this WebApplication app, SharedWorkspaceStore store)
    {
        const string prefix = "/api/v1/organizations/{organizationId:guid}/cloud-activity";
        app.MapPost(prefix, async (ClaimsPrincipal principal, Guid organizationId, CloudActivitySnapshot request, CancellationToken token) =>
        {
            if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
            try
            {
                var service = CreateService(store, identity);
                if (!service.Authorize(organizationId, identity, OrganizationAction.RunScan).Allowed
                    || !service.Authorize(organizationId, identity, OrganizationAction.ViewInventory).Allowed
                    || !service.Authorize(organizationId, identity, OrganizationAction.ViewFindings).Allowed)
                    return Results.Forbid();
                SharedWorkspaceStore.ValidateCloudActivity(request);
                if (!service.GetTenantBindings(organizationId, identity).Any(binding => binding.Provider == request.Provider
                    && binding.ExternalTenantId == request.AccountId)) return Results.Forbid();
                await store.SaveCloudActivityAsync(Guid.Parse(identity.TenantId), organizationId, identity.UserId, request, token);
                return Results.Ok(Envelope(request));
            }
            catch (ArgumentException) { return BadRequest("invalid_activity", "Invalid bounded 90-day activity evidence."); }
            catch (InvalidOperationException) { return Results.Conflict(Error("activity_conflict", "Activity evidence could not be stored under that snapshot ID.")); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        }).RequireAuthorization("CiemApiAccess").WithName("UploadCloudActivity")
            .WithSummary("Idempotently upload a read-only, partial-coverage audit snapshot for a bound account; does not execute cloud calls.")
            .Produces<ApiEnvelope<CloudActivitySnapshot>>().Produces(400).Produces(403).Produces(404).Produces(409);
        app.MapGet(prefix, (ClaimsPrincipal principal, Guid organizationId, CloudProvider provider, string accountId, CancellationToken token) =>
            WithMonitoringAccess(principal, organizationId, store, false, async tenant =>
            {
                if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
                var service = CreateService(store, identity);
                if (!service.GetTenantBindings(organizationId, identity).Any(binding => binding.Provider == provider
                    && binding.ExternalTenantId == accountId)) return Results.Forbid();
                var result = await store.GetCloudActivityAsync(tenant, organizationId, provider, accountId, token);
                return result is null ? Results.NotFound() : Results.Ok(Envelope(result));
            })).RequireAuthorization("CiemApiAccess").WithName("GetCloudActivity")
            .WithSummary("Read the latest scoped snapshot, without initiating any live endpoint call. All sources have partial coverage.")
            .Produces<ApiEnvelope<CloudActivitySnapshot>>().Produces(403).Produces(404);
    }
}
