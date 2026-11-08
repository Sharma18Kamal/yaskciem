using System.Net;
using System.Net.Http.Json;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.LicenseServer.Services;
using CloudEntitle.OrganizationManagement.Models;
using CloudEntitle.OrganizationManagement.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed partial class SharedWorkspaceEndpointTests
{
    [PostgreSqlFact]
    public async Task ContinuousFeedEndpoints_IsolatedCustomRoleMissingPermissionMatrix()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        var scope = ContinuousChangeFeedTests.CreateScope(store);
        var organizations = new OrganizationManagementService(store.CreateOrganizationStore(scope.Tenant));
        var required = new[] { OrganizationAction.ViewInventory, OrganizationAction.ViewFindings,
            OrganizationAction.ManageConnections, OrganizationAction.ManagePolicies };
        var identities = new List<(OrganizationIdentity Identity, OrganizationAction? Missing)>();
        foreach (var missing in required.Cast<OrganizationAction?>().Append(null))
        {
            var role = organizations.CreateCustomRole(scope.Organization, scope.Owner, $"feed-{missing?.ToString() ?? "minimum"}",
                required.Where(permission => permission != missing));
            var actor = new OrganizationIdentity(Guid.NewGuid(), "custom", scope.Tenant.ToString(),
                $"feed-{missing?.ToString() ?? "minimum"}@example.invalid");
            organizations.AddMember(scope.Organization, scope.Owner, actor.Email, new OrganizationRoleOption(role.Name, null, role.Id));
            organizations.AcceptInvitation(scope.Organization, actor);
            identities.Add((actor, missing));
        }
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddConfiguration(ContinuousChangeFeedTests.Configuration(scope.Tenant, scope.Organization));
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication(TestAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("CiemApiAccess", policy => policy.RequireAuthenticatedUser()));
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton<IContinuousChangeFeedService, ContinuousChangeFeedService>();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapContinuousChangeFeedEndpoints(store);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var prefix = $"/api/v1/organizations/{scope.Organization:D}/continuous-change-feeds";
        async Task<HttpResponseMessage> Send(HttpMethod method, string path, OrganizationIdentity identity, object? body = null)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = JsonContent.Create(body);
            SetIdentityHeaders(request, scope.Tenant, identity.UserId, identity.Email);
            return await client.SendAsync(request);
        }
        var source = ContinuousChangeSourceConfiguration.Resolve(builder.Configuration, scope.Tenant, scope.Organization, "iam")!;
        await store.ConfigureContinuousFeedAsync(scope.Tenant, scope.Organization, source, new(true, true));
        foreach (var (identity, missing) in identities)
        {
            var readDenied = missing is OrganizationAction.ViewInventory or OrganizationAction.ViewFindings;
            using var status = await Send(HttpMethod.Get, prefix, identity);
            using var changes = await Send(HttpMethod.Get, prefix + "/iam/changes", identity);
            Assert.Equal(readDenied ? HttpStatusCode.Forbidden : HttpStatusCode.OK, status.StatusCode);
            Assert.Equal(readDenied ? HttpStatusCode.Forbidden : HttpStatusCode.OK, changes.StatusCode);
            using var configure = await Send(HttpMethod.Put, prefix + "/iam", identity, new ConfigureContinuousChangeFeedRequest(true, true));
            using var pause = await Send(HttpMethod.Post, prefix + "/iam/pause", identity);
            Assert.Equal(missing.HasValue ? HttpStatusCode.Forbidden : HttpStatusCode.OK, configure.StatusCode);
            Assert.Equal(missing.HasValue ? HttpStatusCode.Forbidden : HttpStatusCode.NoContent, pause.StatusCode);
        }
        Assert.False((await store.GetContinuousFeedStatesAsync(scope.Tenant, scope.Organization)).Single().State.Enabled);
        await app.StopAsync();
    }

    [PostgreSqlFact]
    public async Task ContinuousFeedEndpoints_AuthenticatedActiveBoundScopeOptInPauseAndSavedReadsOnly()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        var scope = ContinuousChangeFeedTests.CreateScope(store);
        var viewer = new OrganizationIdentity(Guid.NewGuid(), "viewer", scope.Tenant.ToString(), "viewer@example.invalid");
        var organizations = new OrganizationManagementService(store.CreateOrganizationStore(scope.Tenant));
        organizations.AddMember(scope.Organization, scope.Owner, viewer.Email, OrganizationMemberRole.Viewer);
        organizations.AcceptInvitation(scope.Organization, viewer);
        var config = ContinuousChangeFeedTests.Configuration(scope.Tenant, scope.Organization);
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddConfiguration(config);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication(TestAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("CiemApiAccess", policy => policy.RequireAuthenticatedUser()));
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton<IContinuousChangeFeedService, ContinuousChangeFeedService>();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapContinuousChangeFeedEndpoints(store);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var prefix = $"/api/v1/organizations/{scope.Organization:D}/continuous-change-feeds";
        async Task<HttpResponseMessage> Send(HttpMethod method, string path, OrganizationIdentity identity, object? body = null)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = JsonContent.Create(body);
            SetIdentityHeaders(request, Guid.Parse(identity.TenantId), identity.UserId, identity.Email);
            return await client.SendAsync(request);
        }
        using var anonymous = await client.GetAsync(prefix);
        Assert.NotEqual(HttpStatusCode.OK, anonymous.StatusCode);
        using var viewerRead = await Send(HttpMethod.Get, prefix, viewer);
        Assert.Equal(HttpStatusCode.OK, viewerRead.StatusCode);
        var available = Assert.Single((await viewerRead.Content.ReadFromJsonAsync<ApiEnvelope<IReadOnlyList<ContinuousChangeFeedStatus>>>())!.Data);
        Assert.False(available.Enabled);
        Assert.Null(await store.ClaimContinuousFeedAsync());
        using var deniedWrite = await Send(HttpMethod.Put, prefix + "/iam", viewer, new ConfigureContinuousChangeFeedRequest(true, true));
        Assert.Equal(HttpStatusCode.Forbidden, deniedWrite.StatusCode);
        using var missing = await Send(HttpMethod.Put, prefix + "/missing", scope.Owner, new ConfigureContinuousChangeFeedRequest(true, true));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        var foreign = scope.Owner with { TenantId = Guid.NewGuid().ToString() };
        using var foreignRead = await Send(HttpMethod.Get, prefix, foreign);
        Assert.NotEqual(HttpStatusCode.OK, foreignRead.StatusCode);
        using var invalid = await Send(HttpMethod.Put, prefix + "/iam", scope.Owner, new ConfigureContinuousChangeFeedRequest(false, true));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var connected = await Send(HttpMethod.Put, prefix + "/iam", scope.Owner, new ConfigureContinuousChangeFeedRequest(true, true));
        Assert.Equal(HttpStatusCode.OK, connected.StatusCode);
        var lease = (await store.ClaimContinuousFeedAsync())!;
        await store.CommitContinuousBatchAsync(lease, ContinuousChangeFeedTests.Batch(lease.State), 15);
        using var changes = await Send(HttpMethod.Get, prefix + "/iam/changes", viewer);
        Assert.Equal(HttpStatusCode.OK, changes.StatusCode);
        Assert.Single((await changes.Content.ReadFromJsonAsync<ApiEnvelope<IReadOnlyList<StoredContinuousChange>>>())!.Data);
        using var badLimit = await Send(HttpMethod.Get, prefix + "/iam/changes?limit=501", viewer);
        Assert.Equal(HttpStatusCode.BadRequest, badLimit.StatusCode);
        using var pause = await Send(HttpMethod.Post, prefix + "/iam/pause", scope.Owner);
        Assert.Equal(HttpStatusCode.NoContent, pause.StatusCode);
        Assert.Null(await store.ClaimContinuousFeedAsync());
        using var final = await Send(HttpMethod.Get, prefix, scope.Owner);
        var paused = Assert.Single((await final.Content.ReadFromJsonAsync<ApiEnvelope<IReadOnlyList<ContinuousChangeFeedStatus>>>())!.Data);
        Assert.False(paused.Enabled);
        Assert.False(paused.Connected);
        Assert.Equal("Paused", paused.Health);
        Assert.DoesNotContain("server-only-test", await final.Content.ReadAsStringAsync());
        await app.StopAsync();
    }
}
