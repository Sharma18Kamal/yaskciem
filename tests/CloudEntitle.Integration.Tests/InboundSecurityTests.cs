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
using Npgsql;
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed partial class SharedWorkspaceEndpointTests
{
    [PostgreSqlFact]
    public async Task InboundAlerts_RequireEnabledScopeAndAuthorizedActorAndNeverExecuteResponse()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        var tenant = Guid.NewGuid();
        var owner = new OrganizationIdentity(Guid.NewGuid(), "owner", tenant.ToString(), "owner@example.invalid");
        var viewer = new OrganizationIdentity(Guid.NewGuid(), "viewer", tenant.ToString(), "viewer@example.invalid");
        var service = new OrganizationManagementService(store.CreateOrganizationStore(tenant));
        var organization = service.CreateOrganization("SIEM", "siem", tenant.ToString(), owner);
        service.AddMember(organization.Id, owner, viewer.Email, OrganizationMemberRole.Viewer);
        service.AcceptInvitation(organization.Id, viewer);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var root = $"InboundSecurity:Organizations:{tenant:D}:{organization.Id:D}:Channels:security";
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        { [$"{root}:Enabled"] = "true", [$"{root}:Product"] = "Splunk" });
        builder.Services.AddAuthentication(TestAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("CiemApiAccess", policy => policy.RequireAuthenticatedUser()));
        builder.Services.AddSingleton(store);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapSharedWorkspaceEndpoints(store);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var prefix = $"/api/v1/organizations/{organization.Id:D}/security-alerts";
        var batch = new ImportSecurityAlertsRequest(Guid.NewGuid(), null, "checkpoint-1",
            [new("alert-1", DateTimeOffset.UtcNow.AddMinutes(-1), "Review account", "High", "Open", null, null, null)]);
        async Task<HttpResponseMessage> Send(HttpMethod method, string path, OrganizationIdentity identity, object? body = null)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = JsonContent.Create(body);
            SetIdentityHeaders(request, Guid.Parse(identity.TenantId), identity.UserId, identity.Email);
            return await client.SendAsync(request);
        }
        using var denied = await Send(HttpMethod.Post, prefix + "/Splunk/security/batches", viewer, batch);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var unconfigured = await Send(HttpMethod.Post, prefix + "/Splunk/missing/batches", owner, batch);
        Assert.Equal(HttpStatusCode.Forbidden, unconfigured.StatusCode);
        using var wrongProduct = await Send(HttpMethod.Post, prefix + "/QRadar/security/batches", owner, batch);
        Assert.Equal(HttpStatusCode.Forbidden, wrongProduct.StatusCode);
        foreach (var chunked in new[] { false, true })
        {
            using var oversizedRequest = new HttpRequestMessage(HttpMethod.Post, prefix + "/Splunk/security/batches");
            var bytes = new byte[InboundSecurityProducts.MaximumBatchBytes + 1];
            oversizedRequest.Content = chunked ? new StreamContent(new MemoryStream(bytes)) : new ByteArrayContent(bytes);
            oversizedRequest.Content.Headers.ContentType = new("application/json");
            if (chunked) oversizedRequest.Headers.TransferEncodingChunked = true;
            SetIdentityHeaders(oversizedRequest, tenant, owner.UserId, owner.Email);
            using var oversized = await client.SendAsync(oversizedRequest);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        }
        using var malformedRequest = new HttpRequestMessage(HttpMethod.Post, prefix + "/Splunk/security/batches");
        malformedRequest.Content = new StringContent("{invalid", System.Text.Encoding.UTF8, "application/json");
        SetIdentityHeaders(malformedRequest, tenant, owner.UserId, owner.Email);
        using var malformed = await client.SendAsync(malformedRequest);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var accepted = await Send(HttpMethod.Post, prefix + "/Splunk/security/batches", owner, batch);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(prefix + "/Splunk/security", accepted.Headers.Location!.OriginalString);
        using var replay = await Send(HttpMethod.Post, prefix + "/Splunk/security/batches", owner, batch);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        using var conflict = await Send(HttpMethod.Post, prefix + "/Splunk/security/batches", owner, batch with { Cursor = "changed" });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var invalid = await Send(HttpMethod.Post, prefix + "/Splunk/security/batches", owner,
            batch with { BatchId = Guid.NewGuid(), PreviousCursor = "checkpoint-1", Cursor = "checkpoint-2",
                Alerts = [batch.Alerts[0] with { EvidenceUrl = "https://client.invalid/?token=secret" }] });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var read = await Send(HttpMethod.Get, prefix + "/Splunk/security", viewer);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var alert = Assert.Single((await read.Content.ReadFromJsonAsync<ApiEnvelope<IReadOnlyList<StoredSecurityAlert>>>())!.Data);
        Assert.Equal(owner.UserId, alert.UploadedBy);
        using var cursor = await Send(HttpMethod.Get, prefix + "/Splunk/security/cursor", viewer);
        Assert.Equal("checkpoint-1", (await cursor.Content.ReadFromJsonAsync<ApiEnvelope<SecurityAlertCursor>>())!.Data.Cursor);
        var foreign = new OrganizationIdentity(owner.UserId, "foreign", Guid.NewGuid().ToString(), owner.Email);
        using var foreignRead = await Send(HttpMethod.Get, prefix + "/Splunk/security", foreign);
        Assert.NotEqual(HttpStatusCode.OK, foreignRead.StatusCode);
        Assert.Empty(await store.GetDriftEventsAsync(tenant, organization.Id));
        Assert.Empty(await store.GetNotificationDeliveriesAsync(tenant, organization.Id));
        await app.StopAsync();
    }
}

public sealed class InboundSecurityStoreTests
{
    [Theory]
    [InlineData("Splunk")]
    [InlineData("QRadar")]
    [InlineData("Sentinel")]
    [InlineData("ClientBridge")]
    public void NormalizedBridgeContractValidatesSupportedProduct(string product)
    {
        var alert = new InboundSecurityAlert("one", DateTimeOffset.UtcNow, "Review", "Unknown", "Unknown", null, null, null);
        var batch = new ImportSecurityAlertsRequest(Guid.NewGuid(), null, "one", [alert]);
        InboundSecurityProducts.Validate(product, "security", batch);
        Assert.Throws<ArgumentException>(() => InboundSecurityProducts.Validate(product, "security", batch with { Alerts = [alert, alert] }));
        Assert.Throws<ArgumentException>(() => InboundSecurityProducts.Validate(product, "https://unsafe", batch));
        Assert.Throws<ArgumentException>(() => InboundSecurityProducts.Validate(product, "security", batch with { PreviousCursor = "one" }));
        Assert.Throws<ArgumentException>(() => InboundSecurityProducts.Validate(product, "security", batch with { Alerts = Enumerable.Repeat(alert, 501).ToArray() }));
        Assert.Throws<ArgumentException>(() => InboundSecurityProducts.Validate(product, "security", batch with { Alerts = [alert with { Severity = "invented" }] }));
    }

    [PostgreSqlFact]
    public async Task CursorsAndRevisions_AreAtomicIsolatedReplayableAndAppendOnly()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        var tenant = Guid.NewGuid();
        var organization = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var time = DateTimeOffset.UtcNow.AddMinutes(-10);
        var first = new ImportSecurityAlertsRequest(Guid.NewGuid(), null, "one",
            [new("alert", time, "Review", "High", "Open", null, null, null)]);
        var receipt = await store.ImportSecurityAlertsAsync(tenant, organization, actor, "Sentinel", "security", first);
        Assert.Equal(receipt, await store.ImportSecurityAlertsAsync(tenant, organization, Guid.NewGuid(), "Sentinel", "security", first));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ImportSecurityAlertsAsync(tenant, organization, actor,
            "Sentinel", "security", first with { BatchId = Guid.NewGuid(), Cursor = "stale" }));
        var changed = first with { BatchId = Guid.NewGuid(), PreviousCursor = "one", Cursor = "two",
            Alerts = [first.Alerts[0] with { UpdatedAtUtc = time.AddMinutes(1), Status = "Closed" }] };
        await store.ImportSecurityAlertsAsync(tenant, organization, actor, "Sentinel", "security", changed);
        Assert.Equal("Closed", Assert.Single(await store.GetSecurityAlertsAsync(tenant, organization, "Sentinel", "security")).Alert.Status);
        var conflicting = changed with { BatchId = Guid.NewGuid(), PreviousCursor = "two", Cursor = "three",
            Alerts = [new("another", time, "New", "Low", "Open", null, null, null), changed.Alerts[0] with { Title = "Conflicting evidence" }] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ImportSecurityAlertsAsync(tenant, organization, actor, "Sentinel", "security", conflicting));
        Assert.Equal("two", (await store.GetSecurityAlertCursorAsync(tenant, organization, "Sentinel", "security")).Cursor);
        Assert.Single(await store.GetSecurityAlertsAsync(tenant, organization, "Sentinel", "security"));
        var old = first with { BatchId = Guid.NewGuid(), PreviousCursor = "two", Cursor = "three" };
        Assert.Equal(1, (await store.ImportSecurityAlertsAsync(tenant, organization, actor, "Sentinel", "security", old)).Revisions);
        Assert.Equal("Closed", Assert.Single(await store.GetSecurityAlertsAsync(tenant, organization, "Sentinel", "security")).Alert.Status);
        Assert.True(await store.IsReadyAsync());
        Assert.Empty(await store.GetSecurityAlertsAsync(tenant, Guid.NewGuid(), "Sentinel", "security"));
        Assert.Empty(await store.GetSecurityAlertsAsync(Guid.NewGuid(), organization, "Sentinel", "security"));
        Assert.Empty(await store.GetSecurityAlertsAsync(tenant, organization, "Splunk", "security"));
        Assert.Empty(await store.GetSecurityAlertsAsync(tenant, organization, "Sentinel", "other"));
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var mutate = new NpgsqlCommand("DELETE FROM SharedSecurityAlertRevisions", connection);
        var error = await Assert.ThrowsAsync<PostgresException>(() => mutate.ExecuteNonQueryAsync());
        Assert.Contains("append-only", error.MessageText);
        await using var truncate = new NpgsqlCommand("TRUNCATE SharedSecurityAlertBatches", connection);
        var truncateError = await Assert.ThrowsAsync<PostgresException>(() => truncate.ExecuteNonQueryAsync());
        Assert.Contains("append-only", truncateError.MessageText);
        await using var removeGuard = new NpgsqlCommand("ALTER TABLE SharedSecurityAlertRevisions DISABLE TRIGGER TR_SharedSecurityAlertRevisions_AppendOnly", connection);
        await removeGuard.ExecuteNonQueryAsync();
        Assert.False(await store.IsReadyAsync());
    }

    [PostgreSqlFact]
    public async Task ConcurrentFirstImports_OnlyOneCursorAdvanceWins()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        var tenant = Guid.NewGuid();
        var organization = Guid.NewGuid();
        async Task<bool> Import(string cursor)
        {
            try { await store.ImportSecurityAlertsAsync(tenant, organization, Guid.NewGuid(), "QRadar", "security", new(Guid.NewGuid(), null, cursor, [])); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var results = await Task.WhenAll(Import("first"), Import("second"));
        Assert.Single(results, value => value);
        Assert.Contains((await store.GetSecurityAlertCursorAsync(tenant, organization, "QRadar", "security")).Cursor, new[] { "first", "second" });
    }
}
