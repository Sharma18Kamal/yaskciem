using System.Security.Claims;
using CloudEntitle.Integration.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CloudEntitle.LicenseServer.Services;

public static class SubscriptionEndpoints
{
    public static void MapSubscriptionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/subscription", async Task<Results<Ok<SubscriptionStatus>, ForbidHttpResult>> (
            ClaimsPrincipal principal, ISubscriptionService service, CancellationToken token) =>
        {
            if (!TryTenant(principal, out var tenant)) return TypedResults.Forbid();
            return TypedResults.Ok(await service.GetAsync(tenant, false, token));
        }).RequireAuthorization("CiemApiAccess").WithName("GetSubscription")
            .WithSummary("Read the authenticated tenant's server-evaluated trial or license.");

        app.MapPost("/api/v1/subscription/trial", async Task<Results<Created<SubscriptionStatus>, ForbidHttpResult>> (
            ClaimsPrincipal principal, ISubscriptionService service, CancellationToken token) =>
        {
            if (!TryTenant(principal, out var tenant)) return TypedResults.Forbid();
            return TypedResults.Created("/api/v1/subscription", await service.GetAsync(tenant, true, token));
        }).RequireAuthorization("CiemApiAccess").WithName("RegisterTrial")
            .WithSummary("Register one non-renewable 30-day trial per authenticated tenant.");
    }

    internal static bool TryTenant(ClaimsPrincipal principal, out Guid tenant)
        => Guid.TryParse(principal.FindFirstValue("tid"), out tenant) && tenant != Guid.Empty
            && Guid.TryParse(principal.FindFirstValue("oid"), out var actor) && actor != Guid.Empty;
}
