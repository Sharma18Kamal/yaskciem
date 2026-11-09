using System.IO;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.LicenseServer.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed class SubscriptionTests
{
    [PostgreSqlFact]
    public async Task Trial_IsThirtyDaysDurableTenantScopedAndNeverRestarted()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var source = NpgsqlDataSource.Create(database.ConnectionString);
        await InitializeAsync(source);
        var service = new SubscriptionService(source);
        var tenant = Guid.NewGuid();
        Assert.Equal("NotRegistered", (await service.GetAsync(tenant, false, default)).Status);
        var trials = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.GetAsync(tenant, true, default)));
        Assert.All(trials, trial =>
        {
            Assert.True(trial.CanUseLicensedFeatures);
            Assert.Equal(TimeSpan.FromDays(30), trial.TrialExpiresAtUtc - trial.TrialStartedAtUtc);
            Assert.Equal(trials[0].TrialStartedAtUtc, trial.TrialStartedAtUtc);
        });
        Assert.Equal("NotRegistered", (await service.GetAsync(Guid.NewGuid(), false, default)).Status);
        var reloaded = await new SubscriptionService(source).GetAsync(tenant, true, default);
        Assert.Equal(trials[0].TrialExpiresAtUtc, reloaded.TrialExpiresAtUtc);
        await using (var expire = source.CreateCommand("""
            UPDATE CiemSubscriptions SET TrialStartedAtUtc=statement_timestamp()-interval '31 days',
                TrialExpiresAtUtc=statement_timestamp()-interval '1 day' WHERE TenantId=@tenant
            """))
        {
            expire.Parameters.AddWithValue("tenant", tenant);
            await expire.ExecuteNonQueryAsync();
        }
        var expired = await service.GetAsync(tenant, true, default);
        Assert.Equal("Expired", expired.Status);
        Assert.False(expired.CanUseLicensedFeatures);
        await using (var license = source.CreateCommand("UPDATE CiemSubscriptions SET LicensedUntilUtc=statement_timestamp()+interval '1 year' WHERE TenantId=@tenant"))
        {
            license.Parameters.AddWithValue("tenant", tenant);
            await license.ExecuteNonQueryAsync();
        }
        Assert.Equal("Licensed", (await service.GetAsync(tenant, false, default)).Status);
    }

    [Fact]
    public async Task Api_RequiresIdentityBlocksWritesAndPreservesReadsAndRevocation()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, AuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("CiemApiAccess", policy => policy.RequireAuthenticatedUser()));
        var entitlement = new TestSubscription { Active = false };
        builder.Services.AddSingleton<ISubscriptionService>(entitlement);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseMiddleware<SubscriptionMiddleware>();
        app.MapSubscriptionEndpoints();
        app.MapGet("/api/v1/findings", () => Results.Ok()).RequireAuthorization("CiemApiAccess");
        app.MapPost("/api/v1/scans", () => Results.Ok()).RequireAuthorization("CiemApiAccess");
        app.MapPost("/api/v1/privileged-access/revoke", () => Results.Ok()).RequireAuthorization("CiemApiAccess");
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/subscription")).StatusCode);
        client.DefaultRequestHeaders.Add("test-identity", "yes");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/findings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/v1/scans", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/v1/privileged-access/revoke", null)).StatusCode);
        var registered = await client.PostAsync("/api/v1/subscription/trial", null);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        Assert.Equal("Expired", (await registered.Content.ReadFromJsonAsync<SubscriptionStatus>())!.Status);
        entitlement.Active = true;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/v1/scans", null)).StatusCode);
        client.DefaultRequestHeaders.Remove("test-identity");
        client.DefaultRequestHeaders.Add("test-identity", "invalid");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/v1/subscription/trial", null)).StatusCode);
    }

    [PostgreSqlFact]
    public async Task RuntimeRole_CanRegisterButCannotExtendTrialOrProvisionPaidAccess()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var admin = NpgsqlDataSource.Create(database.ConnectionString);
        await InitializeAsync(admin);
        await using (var grant = admin.CreateCommand($"GRANT USAGE ON SCHEMA \"{database.Schema}\" TO yask_ciem_runtime;"))
            await grant.ExecuteNonQueryAsync();
        var builder = new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(database.ConnectionString) { Pooling = false }.ConnectionString);
        builder.UsePhysicalConnectionInitializer(
            connection =>
            {
                using var role = new NpgsqlCommand("SET ROLE yask_ciem_runtime", connection);
                role.ExecuteNonQuery();
            },
            async connection =>
            {
                await using var role = new NpgsqlCommand("SET ROLE yask_ciem_runtime", connection);
                await role.ExecuteNonQueryAsync();
            });
        await using var runtime = builder.Build();
        var tenant = Guid.NewGuid();
        var service = new SubscriptionService(runtime);
        Assert.True((await service.GetAsync(tenant, true, default)).CanUseLicensedFeatures);
        Assert.Equal("Trial", (await service.GetAsync(tenant, true, default)).Status);
        foreach (var statement in new[]
        {
            "UPDATE CiemSubscriptions SET LicensedUntilUtc=statement_timestamp()+interval '1 year'",
            "DELETE FROM CiemSubscriptions",
            "TRUNCATE CiemSubscriptions",
            "INSERT INTO CiemSubscriptions VALUES (gen_random_uuid(),statement_timestamp(),statement_timestamp()+interval '720 hours',statement_timestamp()+interval '1 year')"
        })
        {
            await using var command = runtime.CreateCommand(statement);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
    }

    private static async Task InitializeAsync(NpgsqlDataSource source)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        await using var create = source.CreateCommand(await File.ReadAllTextAsync(Path.Combine(root, "infra", "postgres", "017-subscriptions.sql")));
        await create.ExecuteNonQueryAsync();
    }
    private sealed class AuthenticationHandler(Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
        : Microsoft.AspNetCore.Authentication.AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(Request.Headers.ContainsKey("test-identity")
                ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("tid", "11111111-1111-1111-1111-111111111111"),
                        new Claim("oid", Request.Headers["test-identity"] == "invalid" ? "invalid" : "22222222-2222-2222-2222-222222222222")], Scheme.Name)), Scheme.Name))
                : AuthenticateResult.NoResult());
    }
}
