using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.FileProviders;

namespace CloudEntitle.LicenseServer.Services;

/// <summary>Public browser sign-in settings; credentials and API data are never included.</summary>
public sealed record WebConsoleConfiguration(string ClientId, string TenantId, string Scope, int StaleAfterHours);

public static class WebConsoleHosting
{
    public static WebConsoleConfiguration? GetConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("WebConsole");
        if (!section.GetValue("Enabled", false)) return null;
        var client = section["ClientId"];
        var tenant = configuration["AzureAd:TenantId"];
        var scope = section["ApiScope"];
        var hours = section.GetValue("StaleAfterHours", 72);
        if (!Guid.TryParse(client, out var clientId) || clientId == Guid.Empty
            || !Guid.TryParse(tenant, out var tenantId) || tenantId == Guid.Empty
            || string.IsNullOrWhiteSpace(scope) || scope.Any(char.IsWhiteSpace)
            || !scope.EndsWith("/access_as_user", StringComparison.Ordinal)
            || !(scope.StartsWith("api://", StringComparison.Ordinal) || scope.StartsWith("https://", StringComparison.Ordinal))
            || hours is < 1 or > 8760)
            throw new InvalidOperationException("WebConsole requires a SPA ClientId, AzureAd:TenantId, delegated ApiScope ending /access_as_user and StaleAfterHours between 1 and 8760.");
        return new(clientId.ToString(), tenantId.ToString(), scope, hours);
    }

    public static void UseOperationsConsole(this WebApplication app)
    {
        var configuration = GetConfiguration(app.Configuration);
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/console"))
            {
                if (configuration is null)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["Content-Security-Policy"] =
                    "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; " +
                    "connect-src 'self' https://login.microsoftonline.com; frame-src 'self' https://login.microsoftonline.com; " +
                    "base-uri 'none'; object-src 'none'; frame-ancestors 'none'; form-action 'none'";
                context.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin-allow-popups";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
            }
            await next();
        });
        if (configuration is null) return;

        var root = app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
        if (!File.Exists(Path.Combine(root, "console", "index.html")))
            throw new InvalidOperationException("Build CloudEntitle.WebConsole before enabling WebConsole.");
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(Path.Combine(root, "console")),
            RequestPath = "/console"
        });
        app.MapGet("/console/", Results<PhysicalFileHttpResult, RedirectHttpResult> (HttpContext context) =>
            context.Request.Path.Value?.EndsWith('/') == true
                ? TypedResults.PhysicalFile(Path.Combine(root, "console", "index.html"), "text/html")
                : TypedResults.Redirect("/console/"))
            .AllowAnonymous();
        app.MapGet("/console/config", Results<Ok<WebConsoleConfiguration>, NotFound> () =>
            configuration is not null ? TypedResults.Ok(configuration) : TypedResults.NotFound())
            .AllowAnonymous().WithName("GetWebConsoleConfiguration")
            .WithSummary("Public, non-secret Entra SPA configuration for the read-only operations console.")
            .Produces<WebConsoleConfiguration>().Produces(StatusCodes.Status404NotFound);
    }
}
