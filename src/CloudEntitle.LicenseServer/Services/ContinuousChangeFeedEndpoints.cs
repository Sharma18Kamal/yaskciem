using System.Security.Claims;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.OrganizationManagement.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace CloudEntitle.LicenseServer.Services;

public static partial class SharedWorkspaceEndpoints
{
    public static void MapContinuousChangeFeedEndpoints(this WebApplication app, SharedWorkspaceStore store)
    {
        const string prefix = "/api/v1/organizations/{organizationId:guid}/continuous-change-feeds";
        app.MapGet(prefix, (ClaimsPrincipal principal, Guid organizationId, [FromServices] IContinuousChangeFeedService service, CancellationToken token) =>
            WithContinuousFeedAccess(principal, async (tenant, identity) =>
                TypedResults.Ok(Envelope(await service.GetStatusAsync(tenant, organizationId, identity, token)))))
            .RequireAuthorization("CiemApiAccess").WithName("GetContinuousChangeFeeds")
            .WithSummary("Read server bridge status and explicit per-category unverified source coverage; never contacts a provider.")
            .Produces<ApiEnvelope<IReadOnlyList<ContinuousChangeFeedStatus>>>().Produces(403).Produces(404);

        app.MapPut(prefix + "/{alias}", (ClaimsPrincipal principal, Guid organizationId, string alias,
            ConfigureContinuousChangeFeedRequest request, [FromServices] IContinuousChangeFeedService service, CancellationToken token) =>
            WithContinuousFeedAccess(principal, async (tenant, identity) =>
                TypedResults.Ok(Envelope(await service.ConfigureAsync(tenant, organizationId, identity, alias, request, token)))))
            .RequireAuthorization("CiemApiAccess").WithName("ConfigureContinuousChangeFeed")
            .WithSummary("Explicitly enable/connect or reset a server-configured authenticated normalized client bridge; no cloud resources are created.")
            .Produces<ApiEnvelope<ContinuousChangeFeedStatus>>().Produces(400).Produces(403).Produces(404).Produces(409);

        app.MapPost(prefix + "/{alias}/pause", (ClaimsPrincipal principal, Guid organizationId, string alias,
            [FromServices] IContinuousChangeFeedService service, CancellationToken token) =>
            WithContinuousFeedAccess(principal, async (tenant, identity) =>
            {
                await service.PauseAsync(tenant, organizationId, identity, alias, token);
                return TypedResults.NoContent();
            })).RequireAuthorization("CiemApiAccess").WithName("PauseContinuousChangeFeed")
            .WithSummary("Pause persistent bridge polling and fence in-flight attempts; immutable changes remain saved.")
            .Produces(204).Produces(403).Produces(404);

        app.MapGet(prefix + "/{alias}/changes", (ClaimsPrincipal principal, Guid organizationId, string alias, int? limit,
            [FromServices] IContinuousChangeFeedService service, CancellationToken token) =>
            WithContinuousFeedAccess(principal, async (tenant, identity) =>
                TypedResults.Ok(Envelope(await service.GetChangesAsync(tenant, organizationId, identity, alias, limit ?? 100, token)))))
            .RequireAuthorization("CiemApiAccess").WithName("GetContinuousCloudChanges")
            .WithSummary("Read bounded saved immutable bridge revisions across source epochs; metadata only, no live poll.")
            .Produces<ApiEnvelope<IReadOnlyList<StoredContinuousChange>>>().Produces(400).Produces(403).Produces(404);
    }

    private static async Task<IResult> WithContinuousFeedAccess(ClaimsPrincipal principal,
        Func<Guid, OrganizationIdentity, Task<IResult>> operation)
    {
        if (!TryGetIdentity(principal, out var identity) || !Guid.TryParse(identity.TenantId, out var tenant)) return TypedResults.Forbid();
        try { return await operation(tenant, identity); }
        catch (ArgumentException) { return TypedResults.Problem(statusCode: 400, title: "Invalid bounded change feed configuration"); }
        catch (UnauthorizedAccessException) { return TypedResults.Forbid(); }
        catch (KeyNotFoundException) { return TypedResults.NotFound(); }
        catch (InvalidOperationException) { return TypedResults.Problem(statusCode: 409, title: "Source reset or feed state conflict"); }
    }
}
