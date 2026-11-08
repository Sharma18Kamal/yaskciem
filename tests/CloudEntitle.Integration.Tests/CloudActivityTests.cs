using System.Text.Json;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.Integration.Services;
using CloudEntitle.LicenseServer.Services;
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed class CloudActivityTests
{
    [Fact]
    public async Task Aws_VerifiesAccountPaginatesAndDeduplicatesWithoutInventingIamActions()
    {
        var now = DateTimeOffset.UtcNow;
        var raw = JsonSerializer.Serialize(new { eventID = "event", recipientAccountId = "123456789012",
            eventSource = "iam.amazonaws.com", eventName = "CreateUser", eventTime = now.AddDays(-40),
            userIdentity = new { arn = "arn:aws:iam::123456789012:user/test" } });
        var runner = new Runner(JsonSerializer.Serialize(new { Account = "123456789012" }),
            JsonSerializer.Serialize(new { Events = new[] { new { CloudTrailEvent = raw } }, NextToken = "next" }),
            JsonSerializer.Serialize(new { Events = new[] { new { CloudTrailEvent = raw } } }));
        var snapshot = await new CloudActivityCollector(runner).CollectAsync(new(CloudProvider.Aws, "123456789012", "us-east-1", "read-only", now));
        var item = Assert.Single(snapshot.Events);
        Assert.Null(item.PermissionAction);
        Assert.Equal("iam.amazonaws.com/CreateUser", item.Action);
        Assert.Contains("--next-token", runner.Calls[2].Arguments);
        Assert.All(runner.Calls, call => Assert.Contains("--profile", call.Arguments));
        var usage = Assert.Single(CloudActivityAnalysis.Analyze(snapshot));
        Assert.Equal(0, usage.Successful30Days);
        Assert.Equal(1, usage.Successful60Days);
        Assert.Equal(1, usage.Successful90Days);
        Assert.Contains("Partial", snapshot.CoverageDetails);
    }

    [Fact]
    public async Task Aws_MismatchedCliAccountStopsBeforeReadingLogs()
    {
        var runner = new Runner("{\"Account\":\"999999999999\"}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CloudActivityCollector(runner).CollectAsync(
            new(CloudProvider.Aws, "123456789012", "us-east-1", "", DateTimeOffset.UtcNow)));
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Azure_CountsOnlyTerminalEventsAndRejectsUnsafeContinuation()
    {
        var now = DateTimeOffset.UtcNow;
        var account = Guid.NewGuid().ToString();
        object Entry(string status) => new { subscriptionId = account, eventDataId = status, eventTimestamp = now.AddDays(-1),
            operationName = new { value = "Microsoft.Compute/virtualMachines/read" }, status = new { value = status }, caller = "principal" };
        var runner = new Runner(JsonSerializer.Serialize(new { value = new[] { Entry("Started"), Entry("Succeeded"), Entry("Failed") } }));
        var snapshot = await new CloudActivityCollector(runner).CollectAsync(new(CloudProvider.Azure, account, "", "", now));
        var usage = Assert.Single(CloudActivityAnalysis.Analyze(snapshot));
        Assert.Equal(1, usage.Successful30Days);
        Assert.Equal(1, usage.Failed90Days);
        Assert.Null(usage.PermissionAction);
        var bad = new Runner(JsonSerializer.Serialize(new { value = Array.Empty<object>(), nextLink = "https://attacker.invalid/steal" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CloudActivityCollector(bad).CollectAsync(new(CloudProvider.Azure, account, "", "", now)));
        Assert.Single(bad.Calls);
    }

    [Fact]
    public async Task Gcp_UsesActualAuthorizationInfoAndKeepsFailedEventsSeparate()
    {
        var now = DateTimeOffset.UtcNow;
        var runner = new Runner(JsonSerializer.Serialize(new[] { new {
            logName = "projects/client-project/logs/cloudaudit.googleapis.com%2Fdata_access", insertId = "one", timestamp = now.AddDays(-75),
            protoPayload = new { methodName = "storage.objects.get", authenticationInfo = new { principalEmail = "reader@example.invalid" },
                authorizationInfo = new[] { new { permission = "storage.objects.get", granted = true, resource = "objects/a" } } }
        } }));
        var snapshot = await new CloudActivityCollector(runner).CollectAsync(new(CloudProvider.Gcp, "client-project", "", "", now));
        var permission = Assert.Single(snapshot.Events, item => item.PermissionAction is not null);
        Assert.Equal("storage.objects.get", permission.PermissionAction);
        Assert.Contains("--project", runner.Calls[0].Arguments);
        Assert.All(CloudActivityAnalysis.Analyze(snapshot), usage =>
        {
            Assert.Equal(0, usage.Successful60Days);
            Assert.Equal(1, usage.Successful90Days);
        });
    }

    [Fact]
    public async Task InvalidContextAndProviderFailuresNeverProduceSuccessfulEmptySnapshots()
    {
        var runner = new Runner();
        await Assert.ThrowsAsync<ArgumentException>(() => new CloudActivityCollector(runner).CollectAsync(
            new(CloudProvider.Aws, "123456789012", "us-east-1&bad", "", DateTimeOffset.UtcNow)));
        Assert.Empty(runner.Calls);
        var failure = new Runner { Failure = true };
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => new CloudActivityCollector(failure).CollectAsync(
            new(CloudProvider.Gcp, "client-project", "", "", DateTimeOffset.UtcNow)));
        Assert.DoesNotContain("secret", exception.Message);
        var malformed = new Runner("not JSON");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CloudActivityCollector(malformed).CollectAsync(
            new(CloudProvider.Gcp, "client-project", "", "", DateTimeOffset.UtcNow)));
    }

    [PostgreSqlFact]
    public async Task Storage_IsScopedIdempotentAndRejectsConflictingSnapshotEvidence()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        var tenant = Guid.NewGuid();
        var organization = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new CloudActivitySnapshot(Guid.NewGuid(), CloudProvider.Aws, "123456789012", now.AddDays(-90), now,
            "CloudTrail regional event history", "Partial management only", false,
            new[] { new CloudActivityEvent("event", "reader", "GetObject", null, null, now.AddDays(-1), true) });
        await store.SaveCloudActivityAsync(tenant, organization, Guid.NewGuid(), snapshot);
        await store.SaveCloudActivityAsync(tenant, organization, Guid.NewGuid(), snapshot);
        Assert.Equal(snapshot.SnapshotId, (await store.GetCloudActivityAsync(tenant, organization, snapshot.Provider, snapshot.AccountId))!.SnapshotId);
        Assert.Null(await store.GetCloudActivityAsync(Guid.NewGuid(), organization, snapshot.Provider, snapshot.AccountId));
        Assert.Null(await store.GetCloudActivityAsync(tenant, Guid.NewGuid(), snapshot.Provider, snapshot.AccountId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveCloudActivityAsync(tenant, organization, Guid.NewGuid(), snapshot with { Source = "Different" }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveCloudActivityAsync(tenant, organization, Guid.NewGuid(),
            snapshot with { Events = new[] { snapshot.Events[0] with { Timestamp = now.AddDays(-91) } } }));
    }

    private sealed class Runner(params string[] responses) : ICloudCommandRunner
    {
        private readonly Queue<string> _responses = new(responses);
        public List<(string Command, IReadOnlyList<string> Arguments)> Calls { get; } = new();
        public bool Failure { get; init; }
        public Task<CloudCommandResult> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add((command, arguments));
            return Task.FromResult(Failure ? new CloudCommandResult(1, "", "private secret error") : new CloudCommandResult(0, _responses.Dequeue(), ""));
        }
    }

    [Fact]
    public void Analysis_UsesInclusiveWindowsAndDoesNotDoubleCountResourceChecks()
    {
        var now = DateTimeOffset.UtcNow;
        var rows = new[]
        {
            new CloudActivityEvent("30", "principal", "action", "permission", "a", now.AddDays(-30), true),
            new CloudActivityEvent("30", "principal", "action", "permission", "b", now.AddDays(-30), true),
            new CloudActivityEvent("60", "principal", "action", "permission", "a", now.AddDays(-60), true),
            new CloudActivityEvent("90", "principal", "action", "permission", "a", now.AddDays(-90), true),
            new CloudActivityEvent("failed", "principal", "action", "permission", "a", now, false)
        };
        var snapshot = new CloudActivitySnapshot(Guid.NewGuid(), CloudProvider.Gcp, "client-project",
            now.AddDays(-90), now, "GCP Cloud Audit Logs", "Partial", false, rows);
        var usage = Assert.Single(CloudActivityAnalysis.Analyze(snapshot));
        Assert.Equal(1, usage.Successful30Days);
        Assert.Equal(2, usage.Successful60Days);
        Assert.Equal(3, usage.Successful90Days);
        Assert.Equal(1, usage.Failed90Days);
        Assert.Equal(now.AddDays(-30), usage.LastUsedAtUtc);
        Assert.Equal("Partial", snapshot.CoverageStatus);
    }

    [Fact]
    public async Task Gcp_TruncatedQueriesStayPartialAndCancellationMakesNoCloudCall()
    {
        var now = DateTimeOffset.UtcNow;
        var rows = Enumerable.Range(0, 5001).Select(index => new {
            logName = "projects/client-project/logs/cloudaudit.googleapis.com%2Factivity",
            insertId = index.ToString(), timestamp = now.AddHours(-1),
            protoPayload = new { methodName = "test.method" }
        }).ToArray();
        var runner = new Runner(JsonSerializer.Serialize(rows));
        var collector = new CloudActivityCollector(runner);
        var snapshot = await collector.CollectAsync(new(CloudProvider.Gcp, "client-project", "", "", now));
        Assert.True(snapshot.Truncated);
        Assert.Equal(5000, snapshot.Events.Count);
        Assert.Equal("Partial", snapshot.CoverageStatus);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collector.CollectAsync(
            new(CloudProvider.Gcp, "client-project", "", "", now), cancelled.Token));
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task AzurePermissionEvidenceUsesAuthorizationActionNotMethodAndGcpRejectsNonAuditLogNames()
    {
        var now = DateTimeOffset.UtcNow;
        var account = Guid.NewGuid().ToString();
        var runner = new Runner(JsonSerializer.Serialize(new { value = new[] { new {
            subscriptionId = account, eventDataId = "one", eventTimestamp = now.AddDays(-1),
            operationName = new { value = "display-operation" }, authorization = new { action = "Microsoft.Compute/virtualMachines/read" },
            status = new { value = "Succeeded" }, caller = "reader" } } }));
        var snapshot = await new CloudActivityCollector(runner).CollectAsync(new(CloudProvider.Azure, account, "", "", now));
        var item = Assert.Single(snapshot.Events);
        Assert.Equal("display-operation", item.Action);
        Assert.Equal("Microsoft.Compute/virtualMachines/read", item.PermissionAction);
        Assert.Equal("Azure authorization.action", item.PermissionEvidenceSource);
        var invalid = new Runner(JsonSerializer.Serialize(new[] { new {
            logName = "projects/client-project/logs/cloudaudit.googleapis.com-not-audit", insertId = "one", timestamp = now,
            protoPayload = new { methodName = "fake" } } }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CloudActivityCollector(invalid).CollectAsync(new(CloudProvider.Gcp, "client-project", "", "", now)));
    }
}
