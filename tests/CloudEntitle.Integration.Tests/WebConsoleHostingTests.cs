using System.Net;
using System.Text.Json;
using CloudEntitle.LicenseServer.Services;
using CloudEntitle.Security.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed class WebConsoleHostingTests
{
    private static Dictionary<string, string?> Settings() => new()
    {
        ["WebConsole:Enabled"] = "true",
        ["WebConsole:ClientId"] = Guid.NewGuid().ToString(),
        ["AzureAd:TenantId"] = Guid.NewGuid().ToString(),
        ["WebConsole:ApiScope"] = $"api://{Guid.NewGuid()}/access_as_user",
        ["WebConsole:ApiToken"] = "must-never-be-exposed"
    };

    [Fact]
    public void ConsoleConfiguration_ShouldBeDisabledByDefaultAndDefaultTo72Hours()
    {
        Assert.Null(WebConsoleHosting.GetConfiguration(new ConfigurationBuilder().Build()));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(Settings()).Build();
        Assert.Equal(72, WebConsoleHosting.GetConfiguration(configuration)!.StaleAfterHours);
    }

    [Theory]
    [InlineData("WebConsole:ClientId", "not-a-guid")]
    [InlineData("AzureAd:TenantId", "common")]
    [InlineData("WebConsole:ApiScope", "api://test/.default")]
    [InlineData("WebConsole:ApiScope", "api://test/access_as_user extra")]
    [InlineData("WebConsole:StaleAfterHours", "0")]
    [InlineData("WebConsole:StaleAfterHours", "8761")]
    public void ConsoleConfiguration_ShouldRejectInvalidSettings(string key, string value)
    {
        var settings = Settings();
        settings[key] = value;
        Assert.Throws<InvalidOperationException>(() =>
            WebConsoleHosting.GetConfiguration(new ConfigurationBuilder().AddInMemoryCollection(settings).Build()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConsoleHosting_ShouldServeOnlyEnabledPublicAssetsWithScopedHeaders(bool enabled)
    {
        var root = Path.Combine(Path.GetTempPath(), $"yask-console-test-{Guid.NewGuid():N}");
        var console = Path.Combine(root, "wwwroot", "console");
        Directory.CreateDirectory(console);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(console, "index.html"), "<!doctype html><title>Console</title>");
            await File.WriteAllTextAsync(Path.Combine(console, "auth.html"), "<!doctype html><title>Auth</title>");
            await File.WriteAllTextAsync(Path.Combine(console, "app.js"), "console.log('fixture');");
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root, WebRootPath = "wwwroot" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var settings = Settings();
            settings["WebConsole:Enabled"] = enabled.ToString();
            builder.Configuration.AddInMemoryCollection(settings);
            await using var app = builder.Build();
            app.Use(async (context, next) =>
            {
                SecurityHeaderPolicy.Apply(context.Response, false);
                await next();
            });
            app.UseOperationsConsole();
            app.MapGet("/api-header-check", () => "API");
            await app.StartAsync();
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!.Addresses.Single())
            };
            foreach (var path in new[] { "/console/", "/console/auth.html", "/console/app.js", "/console/config" })
            {
                using var response = await client.GetAsync(path);
                Assert.Equal(enabled ? HttpStatusCode.OK : HttpStatusCode.NotFound, response.StatusCode);
                if (!enabled) continue;
                Assert.True(response.Headers.CacheControl!.NoStore);
                Assert.Contains("script-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
                Assert.Contains("https://login.microsoftonline.com", response.Headers.GetValues("Content-Security-Policy").Single());
                Assert.Equal("same-origin-allow-popups", response.Headers.GetValues("Cross-Origin-Opener-Policy").Single());
                if (path.EndsWith("/config", StringComparison.Ordinal))
                {
                    var content = await response.Content.ReadAsStringAsync();
                    Assert.DoesNotContain("must-never-be-exposed", content);
                    using var document = JsonDocument.Parse(content);
                    Assert.Equal(4, document.RootElement.EnumerateObject().Count());
                    Assert.Equal(72, document.RootElement.GetProperty("staleAfterHours").GetInt32());
                }
            }
            using var api = await client.GetAsync("/api-header-check");
            Assert.Equal("default-src 'none'; frame-ancestors 'none'", api.Headers.GetValues("Content-Security-Policy").Single());
            using var entry = await client.GetAsync("/console");
            Assert.Equal(enabled ? HttpStatusCode.Redirect : HttpStatusCode.NotFound, entry.StatusCode);
            if (enabled) Assert.Equal("/console/", entry.Headers.Location!.OriginalString);
            using var write = await client.PostAsync("/console/config", null);
            Assert.Equal(enabled ? HttpStatusCode.MethodNotAllowed : HttpStatusCode.NotFound, write.StatusCode);
            using var unknown = await client.GetAsync("/console/not-an-endpoint");
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            await app.StopAsync();
        }
        finally { Directory.Delete(root, true); }
    }
}
