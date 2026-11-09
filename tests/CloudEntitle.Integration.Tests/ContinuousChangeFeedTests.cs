using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.LicenseServer.Services;
using CloudEntitle.OrganizationManagement.Models;
using CloudEntitle.OrganizationManagement.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed class ContinuousChangeFeedTests
{
    internal static ContinuousChangeFeedStatus State() => new("iam", CloudProvider.Aws, "123456789012", "client-cloudtrail",
        Guid.NewGuid(), true, true, null, "Pending", 0, null, null, null, ContinuousChangeFeedValidation.UnknownCoverage());
    internal static ContinuousChangeBatch Batch(ContinuousChangeFeedStatus state, string cursor = "one",
        IReadOnlyList<NormalizedCloudChange>? changes = null)
    {
        var now = DateTimeOffset.UtcNow.AddSeconds(-1);
        return new(Guid.NewGuid(), state.SourceEpoch, state.SourceId, state.Provider, state.AccountId, state.Cursor, cursor, now,
            ContinuousChangeFeedValidation.Categories.Select(c => new ChangeFeedCategoryCoverage(c, "Current", true,
                now.AddHours(-1), now, null)).ToArray(),
            changes ?? [new("event-1", "revision-1", "IamPolicy", "Changed", "role-1", "policy-1",
                now.AddSeconds(-2), now.AddSeconds(-1), new string('a', 64), new string('b', 64))]);
    }

    [Fact]
    public void ContinuousFeed_ExplicitCategoryClaimsNeverBecomeVerifiedCompleteAndRejectUnsafeOrUnscopedEvidence()
    {
        var state = State();
        var batch = Batch(state);
        ContinuousChangeFeedValidation.Validate(batch, state);
        Assert.All(batch.Coverage, c => Assert.False(c.VerifiedComplete));
        Assert.Throws<ArgumentException>(() => ContinuousChangeFeedValidation.Validate(batch with { SourceEpoch = Guid.NewGuid() }, state));
        Assert.Throws<ArgumentException>(() => ContinuousChangeFeedValidation.Validate(batch with { AccountId = "999999999999" }, state));
        Assert.Throws<ArgumentException>(() => ContinuousChangeFeedValidation.Validate(batch with { Coverage = [] }, state));
        Assert.Throws<ArgumentException>(() => ContinuousChangeFeedValidation.Validate(batch with { Changes = [batch.Changes[0], batch.Changes[0]] }, state));
        Assert.Throws<ArgumentException>(() => ContinuousChangeFeedValidation.Validate(batch with
            { Changes = [batch.Changes[0] with { ResourceId = "-----BEGIN PRIVATE KEY-----\nvalue" }] }, state));
        Assert.Throws<ArgumentException>(() => ContinuousChangeFeedValidation.Validate(batch with
            { Changes = [batch.Changes[0] with { AfterSha256 = "credential-value" }] }, state));
        foreach (var tokenValue in new[] { "eyJhbGciOiJIUzI1NiJ9.payload.signature", "AKIAABCDEFGHIJKLMNOP",
            "ASIAABCDEFGHIJKLMNOP", "ya29.access-token", "ghp_secret", "base64==" })
            Assert.Throws<ArgumentException>(() => ContinuousChangeFeedValidation.Validate(batch with
                { Changes = [batch.Changes[0] with { CredentialMetadataId = tokenValue }] }, state));
        Assert.Throws<ArgumentException>(() => ContinuousChangeFeedValidation.Validate(batch with
            { Coverage = batch.Coverage.Select(c => c with { State = "Gap", GapCode = null }).ToArray() }, state));
        var json = JsonSerializer.Serialize(batch, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ContinuousChangeBatch>(
            json[..^1] + ",\"rawPayload\":\"secret\"}", new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Theory]
    [InlineData("http://bridge.example.invalid/changes")]
    [InlineData("https://bridge.example.invalid/changes?token=secret")]
    [InlineData("https://user:pass@bridge.example.invalid/changes")]
    [InlineData("https://127.0.0.1/changes")]
    [InlineData("https://other.example.invalid/changes")]
    [InlineData("https://bridge.example.invalid:444/changes")]
    public void ContinuousFeed_ServerAliasRejectsUnsafeEndpoints(string endpoint)
    {
        var tenant = Guid.NewGuid(); var organization = Guid.NewGuid();
        var config = Configuration(tenant, organization, endpoint);
        Assert.Null(ContinuousChangeSourceConfiguration.Resolve(config, tenant, organization, "iam"));
    }

    [Fact]
    public void ContinuousFeed_RequiresServerEnabledConnectedSecretAndExactAllowlist()
    {
        var tenant = Guid.NewGuid(); var organization = Guid.NewGuid();
        var config = Configuration(tenant, organization);
        var source = ContinuousChangeSourceConfiguration.Resolve(config, tenant, organization, "iam");
        Assert.NotNull(source);
        Assert.Null(ContinuousChangeSourceConfiguration.Resolve(config, Guid.NewGuid(), organization, "iam"));
        var prefix = Root(tenant, organization);
        foreach (var field in new[] { "Enabled", "Connected", "BearerToken", "SourceVersion" })
        {
            var copy = Configuration(tenant, organization);
            copy[$"{prefix}:{field}"] = null;
            Assert.Null(ContinuousChangeSourceConfiguration.Resolve(copy, tenant, organization, "iam"));
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Redirect, "bridge_response_uncertain")]
    [InlineData(HttpStatusCode.Unauthorized, "bridge_auth")]
    [InlineData(HttpStatusCode.TooManyRequests, "bridge_throttled")]
    [InlineData(HttpStatusCode.NoContent, "bridge_response_uncertain")]
    public async Task ContinuousFeed_BridgeNeverTreatsUncertainResponsesAsSuccessfulEmptyPolls(HttpStatusCode code, string expected)
    {
        var handler = new Handler(_ => new HttpResponseMessage(code) { Content = new StringContent("{}") });
        using var client = new HttpClient(handler);
        var source = ContinuousChangeSourceConfiguration.Resolve(Configuration(Guid.Empty, Guid.Empty), Guid.Empty, Guid.Empty, "iam")!;
        var error = await Assert.ThrowsAsync<ChangeBridgeException>(() =>
            new HttpsContinuousChangeBridge(new Factory(client)).PollAsync(source, State(), default));
        Assert.Equal(expected, error.Code);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ContinuousFeed_BridgeUsesBoundedAuthenticatedNormalizedRequestAndRejectsUnknownFields()
    {
        var state = State();
        var source = ContinuousChangeSourceConfiguration.Resolve(Configuration(Guid.Empty, Guid.Empty), Guid.Empty, Guid.Empty, "iam")!;
        var handler = new Handler(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("server-only-test", request.Headers.Authorization?.Parameter);
            Assert.Contains($"epoch={state.SourceEpoch:D}", request.RequestUri!.Query);
            Assert.Contains("limit=500", request.RequestUri.Query);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(Batch(state)) };
        });
        using var client = new HttpClient(handler);
        var batch = await new HttpsContinuousChangeBridge(new Factory(client)).PollAsync(source, state, default);
        Assert.Single(batch.Changes);
        var unsafeHandler = new Handler(_ => new(HttpStatusCode.OK)
            { Content = new StringContent("{\"rawPayload\":\"secret\"}", Encoding.UTF8, "application/json") });
        using var unsafeClient = new HttpClient(unsafeHandler);
        await Assert.ThrowsAsync<JsonException>(() => new HttpsContinuousChangeBridge(new Factory(unsafeClient)).PollAsync(source, state, default));
    }

    [PostgreSqlFact]
    public async Task ContinuousFeed_AtomicCursorExactReplayOverlapRevisionConflictsSourceResetAndOutbox()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        var scope = CreateScope(store);
        var source = ContinuousChangeSourceConfiguration.Resolve(Configuration(scope.Tenant, scope.Organization), scope.Tenant, scope.Organization, "iam")!;
        var state = await store.ConfigureContinuousFeedAsync(scope.Tenant, scope.Organization, source, new(true, true));
        await store.SaveNotificationRuleAsync(scope.Tenant, scope.Organization,
            new(Guid.NewGuid(), "change-feed", true, SharedAlertSeverity.Low, true, false, true, true, true, "operations")
            { EventTypes = ["cloud-change.observed"] });
        var lease = (await store.ClaimContinuousFeedAsync())!;
        Assert.Null(await store.ClaimContinuousFeedAsync());
        var first = Batch(state);
        var receipt = await store.CommitContinuousBatchAsync(lease, first, 15);
        Assert.Equal(1, receipt.AcceptedCount);
        Assert.Single(await store.GetNotificationDeliveriesAsync(scope.Tenant, scope.Organization));
        Assert.Single(await store.GetDriftEventsAsync(scope.Tenant, scope.Organization));
        await Due(database.ConnectionString);
        var replayLease = (await store.ClaimContinuousFeedAsync())!;
        var replay = await store.CommitContinuousBatchAsync(replayLease, first, 15);
        Assert.Equal(receipt, replay);
        await Due(database.ConnectionString);
        var nextLease = (await store.ClaimContinuousFeedAsync())!;
        var overlap = Batch(nextLease.State, "two", [first.Changes[0], first.Changes[0] with { SourceEventId = "event-2" }]);
        Assert.Equal(1, (await store.CommitContinuousBatchAsync(nextLease, overlap, 15)).AcceptedCount);
        Assert.Equal(2, (await store.GetContinuousChangesAsync(scope.Tenant, scope.Organization, "iam")).Count);
        await Due(database.ConnectionString);
        var conflictLease = (await store.ClaimContinuousFeedAsync())!;
        var conflicting = Batch(conflictLease.State, "three",
            [first.Changes[0] with { SourceEventId = "must-rollback-event" }, first.Changes[0] with { AfterSha256 = new string('c', 64) }]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitContinuousBatchAsync(conflictLease, conflicting, 15));
        Assert.Equal("two", (await store.GetContinuousFeedStatesAsync(scope.Tenant, scope.Organization)).Single().State.Cursor);
        Assert.Equal(2, (await store.GetDriftEventsAsync(scope.Tenant, scope.Organization)).Count);
        Assert.Equal(2, (await store.GetContinuousChangesAsync(scope.Tenant, scope.Organization, "iam")).Count);
        Assert.Equal(2, (await store.GetNotificationDeliveriesAsync(scope.Tenant, scope.Organization)).Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CommitContinuousBatchAsync(conflictLease, first with { Cursor = "changed" }, 15));
        var replacement = source with { SourceId = "client-replacement", ConfigurationSha256 = new string('d', 64) };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ConfigureContinuousFeedAsync(scope.Tenant, scope.Organization, replacement, new(true, true)));
        var reset = await store.ConfigureContinuousFeedAsync(scope.Tenant, scope.Organization, replacement, new(true, true, true));
        Assert.NotEqual(state.SourceEpoch, reset.SourceEpoch);
        Assert.Null(reset.Cursor);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitContinuousBatchAsync(conflictLease, conflicting, 15));
        Assert.Equal(2, (await store.GetContinuousChangesAsync(scope.Tenant, scope.Organization, "iam")).Count);
        Assert.All(reset.Coverage, coverage => Assert.False(coverage.VerifiedComplete));
    }

    [PostgreSqlFact]
    public async Task ContinuousFeed_GapsPersistPauseFencesStaleWorkersAndBackoffRetainsCursor()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        var scope = CreateScope(store);
        var config = Configuration(scope.Tenant, scope.Organization);
        var source = ContinuousChangeSourceConfiguration.Resolve(config, scope.Tenant, scope.Organization, "iam")!;
        var state = await store.ConfigureContinuousFeedAsync(scope.Tenant, scope.Organization, source, new(true, true));
        var lease = (await store.ClaimContinuousFeedAsync())!;
        var initial = Batch(state);
        var gap = initial with { Coverage = initial.Coverage.Select(c => c.Category == "IamPolicy"
            ? c with { State = "Gap", SourceReportedComplete = false, GapCode = "retention_gap" } : c).ToArray() };
        await store.CommitContinuousBatchAsync(lease, gap, 15);
        await Due(database.ConnectionString);
        var repairClaim = (await store.ClaimContinuousFeedAsync())!;
        await store.CommitContinuousBatchAsync(repairClaim, Batch(repairClaim.State, "two", []), 15);
        var saved = (await store.GetContinuousFeedStatesAsync(scope.Tenant, scope.Organization)).Single().State;
        Assert.Equal("Gap", saved.Health);
        Assert.Contains(saved.Coverage, c => c.Category == "IamPolicy" && c.GapCode == "retention_gap");
        await Due(database.ConnectionString);
        var stale = (await store.ClaimContinuousFeedAsync())!;
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var expire = new NpgsqlCommand("UPDATE SharedContinuousChangeFeeds SET LeaseExpiresAtUtc=now()-interval '1 second';", connection);
            await expire.ExecuteNonQueryAsync();
        }
        var replacement = (await store.ClaimContinuousFeedAsync())!;
        Assert.NotEqual(stale.AttemptId, replacement.AttemptId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitContinuousBatchAsync(stale, Batch(stale.State, "three"), 15));
        await store.FailContinuousFeedAsync(stale, "old_worker");
        Assert.Equal(0, (await store.GetContinuousFeedStatesAsync(scope.Tenant, scope.Organization)).Single().State.ConsecutiveFailures);
        var worker = new ContinuousChangeFeedWorker(store, config, new FailingBridge(), NullLogger<ContinuousChangeFeedWorker>.Instance,
            new WorkerHealthRegistry());
        await worker.PollLeaseAsync(replacement, default);
        saved = (await store.GetContinuousFeedStatesAsync(scope.Tenant, scope.Organization)).Single().State;
        Assert.Equal("two", saved.Cursor);
        Assert.Equal("bridge_transport_uncertain", saved.FailureCode);
        Assert.Equal(1, saved.ConsecutiveFailures);
        Assert.True(saved.NextPollAtUtc > DateTimeOffset.UtcNow.AddSeconds(20));
        Assert.Null(await store.ClaimContinuousFeedAsync());
        await Due(database.ConnectionString);
        var pausedLease = (await store.ClaimContinuousFeedAsync())!;
        await store.PauseContinuousFeedAsync(scope.Tenant, scope.Organization, "iam");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitContinuousBatchAsync(pausedLease, Batch(pausedLease.State, "three"), 15));
        Assert.Null(await store.ClaimContinuousFeedAsync());
    }

    [PostgreSqlFact]
    public async Task ContinuousFeed_SourceConfigRevocationAndUnboundOrSuspendedOrganizationNeverContactBridge()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        var scope = CreateScope(store);
        var config = Configuration(scope.Tenant, scope.Organization);
        var source = ContinuousChangeSourceConfiguration.Resolve(config, scope.Tenant, scope.Organization, "iam")!;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.ConfigureContinuousFeedAsync(
            scope.Tenant, scope.Organization, source with { AccountId = "999999999999" }, new(true, true)));
        var state = await store.ConfigureContinuousFeedAsync(scope.Tenant, scope.Organization, source, new(true, true));
        var lease = (await store.ClaimContinuousFeedAsync())!;
        var subscription = new TestSubscription { Active = false };
        var unlicensedBridge = new CountingBridge();
        await new ContinuousChangeFeedWorker(store, config, unlicensedBridge,
            NullLogger<ContinuousChangeFeedWorker>.Instance, new WorkerHealthRegistry(), subscription).PollLeaseAsync(lease, default);
        Assert.Equal(0, unlicensedBridge.Calls);
        Assert.Equal("subscription_inactive", (await store.GetContinuousFeedStatesAsync(scope.Tenant, scope.Organization)).Single().State.FailureCode);
        await Due(database.ConnectionString);
        lease = (await store.ClaimContinuousFeedAsync())!;
        config[$"{Root(scope.Tenant, scope.Organization)}:Connected"] = "false";
        var bridge = new CountingBridge();
        var worker = new ContinuousChangeFeedWorker(store, config, bridge, NullLogger<ContinuousChangeFeedWorker>.Instance, new WorkerHealthRegistry());
        await worker.PollLeaseAsync(lease, default);
        Assert.Equal(0, bridge.Calls);
        Assert.Equal("server_source_disabled", (await store.GetContinuousFeedStatesAsync(scope.Tenant, scope.Organization)).Single().State.FailureCode);
        var directory = store.CreateOrganizationStore(scope.Tenant);
        var original = directory.Load();
        directory.Save(original with { Bindings = [] });
        await Due(database.ConnectionString);
        Assert.Null(await store.ClaimContinuousFeedAsync());
        Assert.Equal("Blocked", (await store.GetContinuousFeedStatesAsync(scope.Tenant, scope.Organization)).Single().State.Health);
        directory.Save(original);
        await Due(database.ConnectionString);
        var activeLease = (await store.ClaimContinuousFeedAsync())!;
        directory.Save(original with { Organizations = original.Organizations.Select(o => o with { Status = OrganizationStatus.Suspended }).ToArray() });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.CommitContinuousBatchAsync(activeLease, Batch(state), 15));
        Assert.Null((await store.GetContinuousFeedStatesAsync(scope.Tenant, scope.Organization)).Single().State.Cursor);
        Assert.Empty(await store.GetContinuousChangesAsync(scope.Tenant, scope.Organization, "iam"));
    }

    [PostgreSqlFact]
    public async Task ContinuousFeed_RuntimeRoleCanPollButCannotMutateImmutableEvidence()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var admin = new SharedWorkspaceStore(database.ConnectionString);
        await admin.InitializeAsync();
        var scope = CreateScope(admin);
        await using var grantConnection = new NpgsqlConnection(database.ConnectionString);
        await grantConnection.OpenAsync();
        // InitializeAsync bootstraps tables, not base migration001 grants. Mirror the deployed
        // directory UPDATE grant; account bindings intentionally have no UPDATE grant.
        await using (var grant = new NpgsqlCommand($"""
            GRANT USAGE ON SCHEMA "{database.Schema}" TO yask_ciem_runtime;
            GRANT SELECT,INSERT,UPDATE ON SharedOrganizationDirectories TO yask_ciem_runtime;
            GRANT SELECT,INSERT,DELETE ON SharedAccountBindings TO yask_ciem_runtime;
            """, grantConnection))
            await grant.ExecuteNonQueryAsync();
        var runtimeBuilder = new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(database.ConnectionString) { Pooling = false }.ConnectionString);
        runtimeBuilder.UsePhysicalConnectionInitializer(
            runtimeConnection =>
            {
                using var role = new NpgsqlCommand("SET ROLE yask_ciem_runtime;", runtimeConnection);
                role.ExecuteNonQuery();
            },
            async runtimeConnection =>
            {
                await using var role = new NpgsqlCommand("SET ROLE yask_ciem_runtime;", runtimeConnection);
                await role.ExecuteNonQueryAsync();
            });
        await using var runtimeSource = runtimeBuilder.Build();
        using var runtime = new SharedWorkspaceStore(runtimeSource);
        var source = ContinuousChangeSourceConfiguration.Resolve(Configuration(scope.Tenant, scope.Organization), scope.Tenant, scope.Organization, "iam")!;
        var state = await runtime.ConfigureContinuousFeedAsync(scope.Tenant, scope.Organization, source, new(true, true));
        var lease = (await runtime.ClaimContinuousFeedAsync())!;
        await runtime.CommitContinuousBatchAsync(lease, Batch(state), 15);
        Assert.Single(await runtime.GetContinuousChangesAsync(scope.Tenant, scope.Organization, "iam"));
        await using var connection = await runtimeSource.OpenConnectionAsync();
        await using (var privileges = new NpgsqlCommand("""
            SELECT has_table_privilege(current_user,'SharedOrganizationDirectories','UPDATE'),
                   has_table_privilege(current_user,'SharedAccountBindings','UPDATE');
            """, connection))
        await using (var reader = await privileges.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0));
            Assert.False(reader.GetBoolean(1));
        }
        foreach (var sql in new[] { "UPDATE SharedContinuousChangeRevisions SET PayloadJson='{}';",
            "DELETE FROM SharedContinuousChangeBatches;", "TRUNCATE SharedContinuousChangeRevisions;" })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        }
        foreach (var sql in new[] { "UPDATE SharedContinuousChangeRevisions SET PayloadJson='{}';",
            "DELETE FROM SharedContinuousChangeBatches;", "TRUNCATE SharedContinuousChangeRevisions;" })
        {
            await using var command = new NpgsqlCommand(sql, grantConnection);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.RaiseException, error.SqlState);
        }
    }

    internal static (Guid Tenant, Guid Organization, OrganizationIdentity Owner) CreateScope(SharedWorkspaceStore store)
    {
        var tenant = Guid.NewGuid();
        var owner = new OrganizationIdentity(Guid.NewGuid(), "owner", tenant.ToString(), "owner@example.invalid");
        var service = new OrganizationManagementService(store.CreateOrganizationStore(tenant));
        var organization = service.CreateOrganization("Continuous feeds", "continuous", tenant.ToString(), owner);
        service.BindCloudAccount(organization.Id, owner, CloudProvider.Aws, Guid.NewGuid(), "123456789012", "AWS");
        return (tenant, organization.Id, owner);
    }
    internal static string Root(Guid tenant, Guid organization) => $"ContinuousChangeFeeds:Organizations:{tenant:D}:{organization:D}:Sources:iam";
    internal static IConfigurationRoot Configuration(Guid tenant, Guid organization, string endpoint = "https://bridge.example.invalid/changes")
    {
        var root = Root(tenant, organization);
        return new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{root}:Enabled"] = "true", [$"{root}:Connected"] = "true", [$"{root}:Provider"] = "Aws",
            [$"{root}:AccountId"] = "123456789012", [$"{root}:SourceId"] = "client-cloudtrail",
            [$"{root}:SourceVersion"] = "v1", [$"{root}:Endpoint"] = endpoint,
            [$"{root}:AllowedHosts:0"] = "bridge.example.invalid", [$"{root}:BearerToken"] = "server-only-test",
            [$"{root}:PollSeconds"] = "15"
        }).Build();
    }
    private static async Task Due(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE SharedContinuousChangeFeeds SET NextPollAtUtc=now()-interval '1 second';", connection);
        await command.ExecuteNonQueryAsync();
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; return Task.FromResult(response(request)); }
    }
    private sealed class Factory(HttpClient client) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => client; }
    private sealed class FailingBridge : IContinuousChangeBridge
    {
        public Task<ContinuousChangeBatch> PollAsync(ContinuousChangeSource source, ContinuousChangeFeedStatus state, CancellationToken token)
            => throw new HttpRequestException("Do not persist or log this raw secret transport exception.");
    }
    private sealed class CountingBridge : IContinuousChangeBridge
    {
        public int Calls { get; private set; }
        public Task<ContinuousChangeBatch> PollAsync(ContinuousChangeSource source, ContinuousChangeFeedStatus state, CancellationToken token)
        { Calls++; return Task.FromResult(Batch(state)); }
    }
}
