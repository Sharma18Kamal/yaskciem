using Npgsql;

namespace CloudEntitle.LicenseServer.Services;

public sealed class SubscriptionMiddleware(RequestDelegate next, ILogger<SubscriptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, ISubscriptionService subscriptions)
    {
        var path = context.Request.Path.Value ?? "";
        if (context.User.Identity?.IsAuthenticated != true
            || HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)
            || HttpMethods.IsOptions(context.Request.Method)
            || !path.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase)
            || IsRecoveryOperation(path))
        {
            await next(context);
            return;
        }
        if (!SubscriptionEndpoints.TryTenant(context.User, out var tenant))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        bool allowed;
        try
        {
            allowed = (await subscriptions.GetAsync(tenant, false, context.RequestAborted)).CanUseLicensedFeatures;
        }
        catch (NpgsqlException)
        {
            logger.LogError("Subscription storage is unavailable; new licensed operations are blocked.");
            await Results.Problem("License validation is unavailable. Existing data remains readable.",
                statusCode: StatusCodes.Status503ServiceUnavailable).ExecuteAsync(context);
            return;
        }
        if (allowed)
        {
            await next(context);
            return;
        }
        await Results.Problem("Start the tenant's 30-day trial or obtain a license. Existing data remains readable; revocation and cancellation remain available.",
            statusCode: StatusCodes.Status403Forbidden, title: "Active trial or license required").ExecuteAsync(context);
    }

    internal static bool IsRecoveryOperation(string path)
        => path.Equals("/api/v1/subscription/trial", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/v1/audit/events", StringComparison.OrdinalIgnoreCase)
            || new[] { "/revoke", "/reject", "/cancel", "/pause", "/disconnect" }
                .Any(suffix => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
}
