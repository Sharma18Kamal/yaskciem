using System.Net;
using CloudEntitle.Domain;
using CloudEntitle.LicenseServer.Services;
using CloudEntitle.OrganizationManagement.Models;
using CloudEntitle.OrganizationManagement.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed partial class SharedWorkspaceEndpointTests
{
    [PostgreSqlFact]
    public async Task ConsoleReadContracts_ShouldEnforceExistingRoleMembershipAndTenantBoundaries()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        var tenant = Guid.NewGuid();
        var owner = new OrganizationIdentity(Guid.NewGuid(), "owner", tenant.ToString(), "console-owner@example.invalid");
        var viewer = new OrganizationIdentity(Guid.NewGuid(), "viewer", tenant.ToString(), "console-viewer@example.invalid");
        var inventoryOnly = new OrganizationIdentity(Guid.NewGuid(), "limited", tenant.ToString(), "console-limited@example.invalid");
        var service = new OrganizationManagementService(store.CreateOrganizationStore(tenant));
        var organization = service.CreateOrganization("Console", "console", tenant.ToString(), owner);
        service.BindCloudAccount(organization.Id, owner, CloudProvider.Aws, Guid.NewGuid(), "123456789012", "Production");
        service.AddMember(organization.Id, owner, viewer.Email, OrganizationMemberRole.Viewer);
        service.AcceptInvitation(organization.Id, viewer);
        var role = service.CreateCustomRole(organization.Id, owner, "Inventory reader", new[] { OrganizationAction.ViewInventory });
        service.AddMember(organization.Id, owner, inventoryOnly.Email, new OrganizationRoleOption(role.Name, null, role.Id));
        service.AcceptInvitation(organization.Id, inventoryOnly);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication(TestAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("CiemApiAccess", policy => policy.RequireAuthenticatedUser()));
        builder.Services.AddSingleton(store);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapSharedWorkspaceEndpoints(store);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var prefix = $"/api/v1/organizations/{organization.Id}";
        async Task<HttpResponseMessage> Read(string path, OrganizationIdentity actor)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, prefix + path);
            SetIdentityHeaders(request, Guid.Parse(actor.TenantId), actor.UserId, actor.Email);
            return await client.SendAsync(request);
        }
        foreach (var path in new[] { "/workspace", "/scan-summaries?limit=200", "/findings?limit=1000", "/governance/report" })
        {
            using var allowed = await Read(path, viewer);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
            using var anonymous = await client.GetAsync(prefix + path);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            using var outsider = await Read(path, viewer with { UserId = Guid.NewGuid() });
            Assert.Equal(path == "/workspace" ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden, outsider.StatusCode);
            using var crossTenant = await Read(path, viewer with { TenantId = Guid.NewGuid().ToString() });
            Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);
        }
        foreach (var path in new[] { "/findings?limit=1000", "/governance/report", "/scan-jobs?limit=100" })
        {
            using var denied = await Read(path, inventoryOnly);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using var deniedJobs = await Read("/scan-jobs?limit=100", viewer);
        Assert.Equal(HttpStatusCode.Forbidden, deniedJobs.StatusCode);
        await app.StopAsync();
    }
}
