using System.IO.Compression;
using System.Text.Json;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.Integration.Services;
using CloudEntitle.LicenseServer.Services;
using Xunit;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;
using ProviderActivityEvent = CloudEntitle.Integration.Contracts.CloudActivityEvent;

namespace CloudEntitle.Integration.Tests;

public sealed class CloudActivityDataPlaneTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AzureArchive_FollowsMarkersAndRejectsRepeatedContinuations(bool repeatMarker)
    {
        var account = Guid.NewGuid().ToString();
        var destination = $"/subscriptions/{account}/resourceGroups/logs/providers/Microsoft.Storage/storageAccounts/audit";
        var query = new CloudActivityQuery(CloudProvider.Azure, account, "", "", DateTimeOffset.UtcNow)
        { SourceKind = CloudActivitySource.AzureBlobArchive, Destination = destination, Container = "insights-logs-storage",
            Prefix = "resourceId=/SUBSCRIPTIONS/source/y=2026/" };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { records = Array.Empty<object>() });
        string Page(string name, string? marker) => JsonSerializer.Serialize(new object[]
        {
            new { name = query.Prefix + name, properties = new { contentLength = bytes.Length, etag = "\"v1\"" } },
            new { nextMarker = marker }
        });
        var runner = new Runner(JsonSerializer.Serialize(new { id = destination }),
            Page("first.json", "page-2"), Page("second.json", repeatMarker ? "page-2" : null), "{}", "{}") { Download = bytes };
        if (repeatMarker)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new CloudActivityCollector(runner).CollectAsync(query));
            Assert.Empty(runner.Paths);
        }
        else
        {
            var snapshot = await new CloudActivityCollector(runner).CollectAsync(query);
            Assert.False(snapshot.Truncated);
            Assert.Equal(2, snapshot.Provenance.Count);
            Assert.Equal(2, runner.Paths.Count);
            Assert.All(runner.Paths, path => Assert.False(File.Exists(path)));
        }
        Assert.Contains("--marker", runner.Calls[2].Args);
        Assert.Contains("page-2", runner.Calls[2].Args);
        Assert.All(runner.Calls.Where(call => call.Args.Contains("list")), call =>
        {
            Assert.Contains("--auth-mode", call.Args);
            Assert.Contains("login", call.Args);
            Assert.Contains("--show-next-marker", call.Args);
        });
    }

    [Fact]
    public async Task ArchiveListingPageLimit_IsBoundedAndExplicitlyTruncated()
    {
        var account = Guid.NewGuid().ToString();
        var destination = $"/subscriptions/{account}/resourceGroups/logs/providers/Microsoft.Storage/storageAccounts/audit";
        var query = new CloudActivityQuery(CloudProvider.Azure, account, "", "", DateTimeOffset.UtcNow)
        { SourceKind = CloudActivitySource.AzureBlobArchive, Destination = destination, Container = "insights-logs-storage",
            Prefix = "resourceId=/SUBSCRIPTIONS/source/y=2026/" };
        var pages = Enumerable.Range(1, 64).Select(index => JsonSerializer.Serialize(new[] { new { nextMarker = $"page-{index}" } }));
        var runner = new Runner(new[] { JsonSerializer.Serialize(new { id = destination }) }.Concat(pages).ToArray());
        var snapshot = await new CloudActivityCollector(runner).CollectAsync(query);
        Assert.True(snapshot.Truncated);
        Assert.Contains("64 listing pages", snapshot.CoverageDetails);
        Assert.Equal(65, runner.Calls.Count);
        Assert.Empty(snapshot.Events);
        Assert.Empty(runner.Paths);
    }

    [Fact]
    public async Task Lake_UsesScopedScalarQueryPagesAndPreservesQueryProvenanceWithoutInventingPermission()
    {
        var now = DateTimeOffset.UtcNow;
        var store = Guid.NewGuid().ToString();
        var id = Guid.NewGuid().ToString();
        object Row(string eventId) => new[]
        {
            new Dictionary<string, string?> { ["eventID"] = eventId }, new() { ["eventTime"] = now.AddDays(-60).ToString("O") },
            new() { ["eventSource"] = "s3.amazonaws.com" }, new() { ["eventName"] = "GetObject" },
            new() { ["recipientAccountId"] = "123456789012" }, new() { ["principalArn"] = "arn:aws:iam::123456789012:user/reader" },
            new() { ["errorCode"] = null }
        };
        var runner = new Runner("{\"Account\":\"123456789012\"}",
            JsonSerializer.Serialize(new { EventDataStoreArn = $"arn:aws:cloudtrail:us-east-1:123456789012:eventdatastore/{store}" }),
            JsonSerializer.Serialize(new { QueryId = id }),
            JsonSerializer.Serialize(new { QueryStatus = "FINISHED", QueryResultRows = new[] { Row("one") }, NextToken = "page2" }),
            JsonSerializer.Serialize(new { QueryStatus = "FINISHED", QueryResultRows = new[] { Row("two") } }));
        var snapshot = await new CloudActivityCollector(runner).CollectAsync(new(CloudProvider.Aws, "123456789012", "us-east-1", "audit", now)
        { SourceKind = CloudActivitySource.AwsCloudTrailLake, Destination = store });
        Assert.Equal(2, snapshot.Events.Count);
        Assert.All(snapshot.Events, item => { Assert.Null(item.PermissionAction); Assert.Null(item.Resource); });
        Assert.Equal(2, snapshot.Provenance.Count);
        var sql = runner.Calls[2].Args[runner.Calls[2].Args.ToList().IndexOf("--query-statement") + 1];
        Assert.Contains("recipientAccountId = '123456789012'", sql);
        Assert.Contains("LIMIT 5001", sql);
        Assert.DoesNotContain("resources", sql);
        Assert.Contains("--next-token", runner.Calls[4].Args);
        SharedWorkspaceStore.ValidateCloudActivity(snapshot);
    }

    [Theory]
    [InlineData("FAILED")]
    [InlineData("TIMED_OUT")]
    [InlineData("CANCELLED")]
    public async Task LakeFailureCannotPublishSuccessfulEmptyEvidence(string status)
    {
        var store = Guid.NewGuid().ToString();
        var runner = new Runner("{\"Account\":\"123456789012\"}",
            JsonSerializer.Serialize(new { EventDataStoreArn = $"arn:aws:cloudtrail:us-east-1:123456789012:eventdatastore/{store}" }),
            JsonSerializer.Serialize(new { QueryId = Guid.NewGuid() }), JsonSerializer.Serialize(new { QueryStatus = status }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CloudActivityCollector(runner).CollectAsync(
            new(CloudProvider.Aws, "123456789012", "us-east-1", "", DateTimeOffset.UtcNow)
            { SourceKind = CloudActivitySource.AwsCloudTrailLake, Destination = store }));
    }

    [Fact]
    public async Task Workspace_ValidatesScopeReadOnlyQueryAndUnknownOutcomesAreNotFailures()
    {
        var account = Guid.NewGuid().ToString();
        var destination = $"/subscriptions/{account}/resourceGroups/logs/providers/Microsoft.OperationalInsights/workspaces/audit";
        var now = DateTimeOffset.UtcNow;
        var rows = new[] {
            new[] { now.AddDays(-1).ToString("O"), $"/subscriptions/{account}/resourceGroups/data/providers/Microsoft.Storage/storageAccounts/source", "GetBlob", "reader", "200" },
            new[] { now.AddDays(-40).ToString("O"), $"/subscriptions/{account}/resourceGroups/data/providers/Microsoft.KeyVault/vaults/source", "SecretGet", "reader", "Unknown" } };
        var result = JsonSerializer.Serialize(new { tables = new[] { new { columns =
            new[] { "TimeGenerated", "ResourceId", "Operation", "Principal", "Outcome" }.Select(name => new { name }), rows } } });
        var runner = new Runner(JsonSerializer.Serialize(new { id = destination, customerId = Guid.NewGuid() }), result);
        var snapshot = await new CloudActivityCollector(runner).CollectAsync(new(CloudProvider.Azure, account, "", "", now)
        { SourceKind = CloudActivitySource.AzureLogAnalytics, Destination = destination });
        Assert.Equal(2, snapshot.Events.Count);
        Assert.All(snapshot.Events, item => Assert.Null(item.PermissionAction));
        Assert.False(snapshot.Events[1].OutcomeKnown);
        Assert.Equal(0, CloudActivityAnalysis.Analyze(snapshot).Sum(item => item.Failed90Days));
        Assert.Contains("post", runner.Calls[1].Args);
        Assert.Contains("https://api.loganalytics.io", runner.Calls[1].Args);
        Assert.Single(snapshot.Provenance);
        SharedWorkspaceStore.ValidateCloudActivity(snapshot);
        var bad = new Runner(JsonSerializer.Serialize(new { id = destination, customerId = Guid.NewGuid() }), "{\"error\":{\"code\":\"PartialError\"}}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CloudActivityCollector(bad).CollectAsync(
            new(CloudProvider.Azure, account, "", "", now) { SourceKind = CloudActivitySource.AzureLogAnalytics, Destination = destination }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Archives_UseConditionalDownloadsNormalizeDataEventsAndCleanRawFiles(bool aws)
    {
        var now = DateTimeOffset.UtcNow;
        var account = aws ? "123456789012" : Guid.NewGuid().ToString();
        var prefix = aws ? $"AWSLogs/{account}/CloudTrail/us-east-1/2026/11/07/" : "resourceId=/SUBSCRIPTIONS/source/y=2026/";
        var key = prefix + (aws ? "events.json.gz" : "PT1H.json");
        var destination = aws ? "client-audit" : $"/subscriptions/{account}/resourceGroups/logs/providers/Microsoft.Storage/storageAccounts/audit";
        var raw = aws ? JsonSerializer.SerializeToUtf8Bytes(new { Records = new[] { new { recipientAccountId = account, eventID = "one",
            eventTime = now.AddDays(-1), eventSource = "s3.amazonaws.com", eventName = "GetObject",
            userIdentity = new { arn = "arn:aws:iam::123456789012:user/reader" },
            resources = new[] { new { ARN = "arn:aws:s3:::data/object" } } } } })
            : JsonSerializer.SerializeToUtf8Bytes(new { records = new[] { new {
                time = now.AddDays(-1), resourceId = $"/subscriptions/{account}/resourceGroups/data/providers/Microsoft.Storage/storageAccounts/source",
                operationName = "GetBlob", resultType = "Success", properties = new { requesterObjectId = "reader" } } } });
        byte[] bytes;
        if (aws)
        {
            using var buffer = new MemoryStream();
            using (var zip = new GZipStream(buffer, CompressionMode.Compress, leaveOpen: true)) zip.Write(raw);
            bytes = buffer.ToArray();
        }
        else bytes = raw;
        var listing = aws ? JsonSerializer.Serialize(new { Contents = new[] { new { Key = key, Size = bytes.Length, ETag = "\"v1\"" } }, IsTruncated = false })
            : JsonSerializer.Serialize(new[] { new { name = key, properties = new { contentLength = bytes.Length, etag = "\"v1\"" } } });
        var runner = new Runner(aws ? "{\"Account\":\"123456789012\"}" : JsonSerializer.Serialize(new { id = destination }), listing, "{}") { Download = bytes };
        var snapshot = await new CloudActivityCollector(runner).CollectAsync(new(aws ? CloudProvider.Aws : CloudProvider.Azure, account, "us-east-1", "", now)
        { SourceKind = aws ? CloudActivitySource.AwsS3Archive : CloudActivitySource.AzureBlobArchive, Destination = destination,
            Prefix = prefix, Container = aws ? "" : "insights-logs-storageread" });
        Assert.True(Assert.Single(snapshot.Events).Success);
        Assert.Null(snapshot.Events[0].PermissionAction);
        Assert.Equal(64, Assert.Single(snapshot.Provenance).Sha256.Length);
        Assert.Contains("--if-match", runner.Calls.Last().Args);
        if (aws) Assert.All(runner.Calls.Skip(1), call => Assert.Contains("--expected-bucket-owner", call.Args));
        else Assert.Contains("login", runner.Calls.Last().Args);
        Assert.All(runner.Paths, path => Assert.False(File.Exists(path)));
        SharedWorkspaceStore.ValidateCloudActivity(snapshot);
    }

    [Fact]
    public async Task MalformedArchiveAndForeignAccountAreRejectedAndFilesAreDeleted()
    {
        var query = new CloudActivityQuery(CloudProvider.Aws, "123456789012", "us-east-1", "", DateTimeOffset.UtcNow)
        { SourceKind = CloudActivitySource.AwsS3Archive, Destination = "client-audit", Prefix = "AWSLogs/123456789012/CloudTrail/" };
        var runner = new Runner("{\"Account\":\"123456789012\"}", JsonSerializer.Serialize(new { Contents = new[] {
            new { Key = query.Prefix + "bad.json.gz", Size = 3, ETag = "v1" } } }), "{}") { Download = [1, 2, 3] };
        await Assert.ThrowsAsync<InvalidDataException>(() => new CloudActivityCollector(runner).CollectAsync(query));
        Assert.All(runner.Paths, path => Assert.False(File.Exists(path)));
        var foreign = new Runner("{\"Account\":\"999999999999\"}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CloudActivityCollector(foreign).CollectAsync(query));
        Assert.Single(foreign.Calls);
        Assert.Throws<ArgumentException>(() => CloudActivityCollector.Validate(query with { Prefix = "unscoped/" }));
        Assert.Throws<ArgumentException>(() => CloudActivityCollector.Validate(query with { Destination = "https://attacker.invalid" }));
    }

    [Theory]
    [InlineData(128, false)]
    [InlineData(129, true)]
    public async Task ArchiveFileLimit_IsExactAndAlwaysDisclosesTruncation(int files, bool truncated)
    {
        var query = new CloudActivityQuery(CloudProvider.Aws, "123456789012", "us-east-1", "", DateTimeOffset.UtcNow)
        { SourceKind = CloudActivitySource.AwsS3Archive, Destination = "client-audit", Prefix = "AWSLogs/123456789012/CloudTrail/" };
        byte[] data;
        using (var buffer = new MemoryStream())
        {
            using (var zip = new GZipStream(buffer, CompressionMode.Compress, leaveOpen: true))
                zip.Write(System.Text.Encoding.UTF8.GetBytes("{\"Records\":[]}"));
            data = buffer.ToArray();
        }
        var responses = new List<string> { "{\"Account\":\"123456789012\"}", JsonSerializer.Serialize(new { Contents =
            Enumerable.Range(0, files).Select(index => new { Key = query.Prefix + index + ".json.gz", Size = data.Length, ETag = "v1" }) }) };
        responses.AddRange(Enumerable.Repeat("{}", Math.Min(files, 128)));
        var runner = new Runner(responses.ToArray()) { Download = data };
        var snapshot = await new CloudActivityCollector(runner).CollectAsync(query);
        Assert.Equal(truncated, snapshot.Truncated);
        Assert.Equal(128, snapshot.Provenance.Count);
        Assert.Equal(128, runner.Paths.Count);
        Assert.All(runner.Paths, path => Assert.False(File.Exists(path)));
    }

    [Fact]
    public async Task ArchiveDecompressionLimit_RejectsBombBeforePublishingAndDeletesDownload()
    {
        var query = new CloudActivityQuery(CloudProvider.Aws, "123456789012", "us-east-1", "", DateTimeOffset.UtcNow)
        { SourceKind = CloudActivitySource.AwsS3Archive, Destination = "client-audit", Prefix = "AWSLogs/123456789012/CloudTrail/" };
        byte[] data;
        using (var buffer = new MemoryStream())
        {
            using (var zip = new GZipStream(buffer, CompressionMode.Compress, leaveOpen: true))
                zip.Write(new byte[16 * 1024 * 1024 + 1]);
            data = buffer.ToArray();
        }
        var runner = new Runner("{\"Account\":\"123456789012\"}", JsonSerializer.Serialize(new { Contents = new[] {
            new { Key = query.Prefix + "bomb.json.gz", Size = data.Length, ETag = "v1" } } }), "{}") { Download = data };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CloudActivityCollector(runner).CollectAsync(query));
        Assert.Contains("16 MiB", error.Message);
        Assert.All(runner.Paths, path => Assert.False(File.Exists(path)));
    }

    [Fact]
    public async Task OversizedObjectIsRejectedBeforeDownloadAndProvenanceCannotClaimUnknownSuccess()
    {
        var query = new CloudActivityQuery(CloudProvider.Aws, "123456789012", "us-east-1", "", DateTimeOffset.UtcNow)
        { SourceKind = CloudActivitySource.AwsS3Archive, Destination = "client-audit", Prefix = "AWSLogs/123456789012/CloudTrail/" };
        var runner = new Runner("{\"Account\":\"123456789012\"}", JsonSerializer.Serialize(new { Contents = new[] {
            new { Key = query.Prefix + "large.json.gz", Size = 8 * 1024 * 1024 + 1, ETag = "v1" } } }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CloudActivityCollector(runner).CollectAsync(query));
        Assert.Empty(runner.Paths);
        var snapshot = new CloudActivitySnapshot(Guid.NewGuid(), query.Provider, query.AccountId, query.ThroughUtc.AddDays(-90),
            query.ThroughUtc, "Archive", "Partial", false, [
                new ProviderActivityEvent("one", "reader", "GetObject", null, null, query.ThroughUtc, true) { OutcomeKnown = false } ]);
        Assert.Throws<ArgumentException>(() => SharedWorkspaceStore.ValidateCloudActivity(snapshot));
        Assert.Throws<ArgumentException>(() => SharedWorkspaceStore.ValidateCloudActivity(snapshot with { Events = [],
            Provenance = [new("source", "not-a-digest", 1)] }));
    }

    [Fact]
    public void Attribution_IsExactScopedAndNeverClaimsHistoricalGrantUsageOrUnusedPermissions()
    {
        var account = new CloudAccount { Id = Guid.NewGuid(), CloudProvider = CloudProviderType.Gcp, ExternalId = "client-project" };
        var principal = new CloudPrincipal { Id = Guid.NewGuid(), AccountId = account.Id, CloudProvider = CloudProviderType.Gcp, ExternalId = "reader@example.invalid" };
        var resource = new CloudResource { Id = Guid.NewGuid(), AccountId = account.Id, ExternalId = "projects/client-project/buckets/data/objects/a" };
        var permission = new CloudPermission { Id = Guid.NewGuid(), AccountId = account.Id, Action = "storage.objects.get" };
        var access = new DomainEffectiveAccess { Id = Guid.NewGuid(), CloudAccountId = account.Id, Provider = CloudProvider.Gcp,
            PrincipalId = principal.Id, ResourceId = resource.Id, PermissionId = permission.Id, Decision = AccessDecision.Allow,
            EvaluationStatus = EvaluationStatus.FullyEvaluated };
        var now = DateTimeOffset.UtcNow;
        var observation = new ProviderActivityEvent("event", principal.ExternalId, "storage.objects.get", "storage.objects.get",
            resource.ExternalId, now.AddDays(-1), true) { PermissionEvidenceSource = "GCP authorizationInfo.permission" };
        var snapshot = new CloudActivitySnapshot(Guid.NewGuid(), account.Provider, account.ExternalId!, now.AddDays(-90), now, "GCP", "Partial", false, [observation]);
        var service = new ActivityEntitlementAttributionService();
        ActivityAttributionReport Calculate(params DomainEffectiveAccess[] grants) => service.Correlate(snapshot, account, [principal], [resource], [permission], grants);
        Assert.Equal("CompatibleCurrentGrant", Assert.Single(Calculate(access).Observations).Status);
        Assert.Contains("not proven used grants", Calculate(access).Notice);
        var other = new DomainEffectiveAccess { Id = Guid.NewGuid(), CloudAccountId = account.Id, Provider = CloudProvider.Gcp,
            PrincipalId = principal.Id, ResourceId = resource.Id, PermissionId = permission.Id, Decision = AccessDecision.Allow,
            EvaluationStatus = EvaluationStatus.FullyEvaluated };
        Assert.Equal("AmbiguousCurrentGrants", Assert.Single(Calculate(access, other).Observations).Status);
        permission.Action = "storage.*";
        Assert.Empty(Assert.Single(Calculate(access).Observations).CompatibleEffectiveAccessIds);
        permission.Action = "storage.objects.get";
        access.IsConditional = true;
        Assert.Empty(Assert.Single(Calculate(access).Observations).CompatibleEffectiveAccessIds);
        Assert.Throws<ArgumentException>(() => service.Correlate(snapshot with { AccountId = "other-project" }, account, [principal], [resource], [permission], [access]));
        Assert.Equal("PrincipalUnresolved", Assert.Single(service.Correlate(snapshot, account, [], [resource], [permission], [access]).Observations).Status);
        Assert.Equal("NoSuccessfulPermissionEvidence", Assert.Single(service.Correlate(snapshot with { Events = [observation with { PermissionEvidenceSource = null }] },
            account, [principal], [resource], [permission], [access]).Observations).Status);
    }

    [PostgreSqlFact]
    public async Task ProvenanceAndUnknownOutcomes_RoundTripWithinOrganizationAndCannotBeReplaced()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        var tenant = Guid.NewGuid();
        var organization = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new CloudActivitySnapshot(Guid.NewGuid(), CloudProvider.Azure, Guid.NewGuid().ToString(),
            now.AddDays(-90), now, "Blob archive", "Partial", false,
            [new ProviderActivityEvent("event", "Unattributed", "GetBlob", null, "resource", now.AddDays(-1), false) { OutcomeKnown = false }])
        { SourceScope = "selected-client-prefix", Provenance = [new("client-object", new string('A', 64), 1)] };
        await store.SaveCloudActivityAsync(tenant, organization, Guid.NewGuid(), snapshot);
        var loaded = await store.GetCloudActivityAsync(tenant, organization, snapshot.Provider, snapshot.AccountId);
        Assert.NotNull(loaded);
        Assert.Equal(snapshot.SourceScope, loaded.SourceScope);
        Assert.Equal(snapshot.Provenance[0], Assert.Single(loaded.Provenance));
        Assert.False(Assert.Single(loaded.Events).OutcomeKnown);
        Assert.Equal(0, Assert.Single(CloudActivityAnalysis.Analyze(loaded)).Failed90Days);
        var second = snapshot with { SnapshotId = Guid.NewGuid(), Events = [] };
        var older = snapshot with { SnapshotId = Guid.NewGuid(), FromUtc = now.AddDays(-91), ThroughUtc = now.AddDays(-1), Events = [] };
        await store.SaveCloudActivityAsync(tenant, organization, Guid.NewGuid(), second);
        await store.SaveCloudActivityAsync(tenant, organization, Guid.NewGuid(), older);
        var visited = new List<Guid>();
        Guid? before = null;
        do
        {
            var page = await store.GetCloudActivityHistoryAsync(tenant, organization, snapshot.Provider, snapshot.AccountId, 1, before);
            var item = Assert.Single(page.Snapshots);
            visited.Add(item.SnapshotId);
            var replay = await store.GetCloudActivityHistoryAsync(tenant, organization, snapshot.Provider, snapshot.AccountId, 1, before);
            Assert.Equal(item, Assert.Single(replay.Snapshots));
            before = page.NextBeforeSnapshotId;
        } while (before is not null);
        Assert.Equal(3, visited.Distinct().Count());
        Assert.Equal(older.SnapshotId, visited.Last());
        Assert.Equal(snapshot.SnapshotId, (await store.GetCloudActivitySnapshotAsync(tenant, organization, snapshot.Provider, snapshot.AccountId, snapshot.SnapshotId))!.SnapshotId);
        Assert.Empty((await store.GetCloudActivityHistoryAsync(Guid.NewGuid(), organization, snapshot.Provider, snapshot.AccountId)).Snapshots);
        Assert.Null(await store.GetCloudActivitySnapshotAsync(tenant, organization, snapshot.Provider, "foreign-account", snapshot.SnapshotId));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetCloudActivityHistoryAsync(tenant, organization, snapshot.Provider, snapshot.AccountId, 51));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetCloudActivityHistoryAsync(tenant, organization, snapshot.Provider, "foreign-account", 1, snapshot.SnapshotId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveCloudActivityAsync(tenant, organization,
            Guid.NewGuid(), snapshot with { Provenance = [new("client-object", new string('B', 64), 1)] }));
        Assert.Null(await store.GetCloudActivityAsync(tenant, Guid.NewGuid(), snapshot.Provider, snapshot.AccountId));
    }

    private sealed class Runner(params string[] responses) : ICloudCommandRunner
    {
        private readonly Queue<string> _responses = new(responses);
        public List<(string Command, IReadOnlyList<string> Args)> Calls { get; } = new();
        public List<string> Paths { get; } = new();
        public byte[]? Download { get; init; }
        public async Task<CloudCommandResult> RunAsync(string command, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
        {
            Calls.Add((command, args));
            if (args.Contains("get-object") || args.Contains("download"))
            {
                var file = args.Contains("--file") ? args[args.ToList().IndexOf("--file") + 1] : args[args.ToList().IndexOf("--output") - 1];
                Paths.Add(file);
                await File.WriteAllBytesAsync(file, Download ?? throw new InvalidOperationException("Missing fixture download"), cancellationToken);
            }
            return new(0, _responses.Dequeue(), "");
        }
    }
}
