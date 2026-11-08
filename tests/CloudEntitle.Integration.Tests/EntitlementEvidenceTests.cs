using System.Text.Json;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.Integration.Services;
using CloudEntitle.LicenseServer.Services;
using Npgsql;
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed class EntitlementEvidenceTests
{
    [Theory]
    [InlineData("{\"IsTruncated\":false}")]
    [InlineData("{\"IsTruncated\":false,\"UserDetailList\":[{\"Arn\":\"arn:aws:iam::123456789012:user/empty\"}]}")]
    public async Task Aws_MissingOptionalEmptyListsRemainPartialWithoutInventedPermissions(string payload)
    {
        var runner = new Runner("{\"Account\":\"123456789012\"}", payload);
        var result = await new EntitlementEvidenceCollector(runner).CollectAsync(new(CloudProvider.Aws, "123456789012", ""));
        Assert.Empty(result.Grants);
        Assert.Equal("Partial", result.CoverageStatus);
        Assert.All(runner.Calls, call => Assert.Contains("--no-cli-pager", call.Args));
        var invalid = new Runner("{\"Account\":\"123456789012\"}", "{\"Policies\":null}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new EntitlementEvidenceCollector(invalid).CollectAsync(new(CloudProvider.Aws, "123456789012", "")));
    }
    [Fact]
    public async Task Azure_ReadsActualDefinitionAndPreservesExclusionsConditionsAndDigest()
    {
        var subscription = Guid.NewGuid().ToString();
        var role = Guid.NewGuid().ToString();
        var scope = $"/subscriptions/{subscription}";
        var runner = new Runner(JsonSerializer.Serialize(new { id = subscription }), JsonSerializer.Serialize(new[] { new
        {
            id = $"{scope}/providers/Microsoft.Authorization/roleAssignments/{Guid.NewGuid()}",
            principalId = Guid.NewGuid().ToString(), roleDefinitionId = $"{scope}/providers/Microsoft.Authorization/roleDefinitions/{role}",
            scope, condition = "@Resource[Microsoft.Storage/storageAccounts:Name] StringEquals 'source'"
        } }), JsonSerializer.Serialize(new[] { new { name = role, permissions = new[] { new
        {
            actions = new[] { "Microsoft.Storage/storageAccounts/read" }, notActions = new[] { "Microsoft.Storage/storageAccounts/delete" },
            dataActions = new[] { "Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read" }, notDataActions = new[] { "*" }
        } } } }));
        var snapshot = await new EntitlementEvidenceCollector(runner).CollectAsync(new(CloudProvider.Azure, subscription, ""));
        Assert.Equal(4, snapshot.Grants.Count);
        Assert.Equal(2, snapshot.Grants.Count(item => item.IsExclusion));
        Assert.All(snapshot.Grants, item => { Assert.Equal("Allow", item.Effect); Assert.True(item.HasCondition); Assert.Equal(64, item.SourceSha256.Length); });
        Assert.Equal(2, snapshot.Grants.Count(item => item.IsDataAction));
        Assert.Equal("Partial", snapshot.CoverageStatus);
        Assert.Contains("definition", runner.Calls[2].Args);
        SharedWorkspaceStore.ValidateEntitlementEvidence(snapshot);
    }

    [Fact]
    public async Task Gcp_ReadsProjectEtagAndVersionedIncludedPermissionsWithoutRoleNameInference()
    {
        const string project = "client-project";
        const string member = "serviceAccount:reader@client-project.iam.gserviceaccount.com";
        var runner = new Runner(JsonSerializer.Serialize(new { projectId = project }),
            JsonSerializer.Serialize(new { etag = "etag-v1", bindings = new[] { new { role = "roles/storage.objectViewer", members = new[] { member } } } }),
            JsonSerializer.Serialize(new { name = "roles/storage.objectViewer", includedPermissions = new[] { "storage.objects.get", "storage.objects.list" } }));
        var snapshot = await new EntitlementEvidenceCollector(runner).CollectAsync(new(CloudProvider.Gcp, project, ""));
        Assert.Equal(2, snapshot.Grants.Count);
        Assert.All(snapshot.Grants, item => { Assert.Equal(member, item.Principal); Assert.StartsWith("etag-v1:", item.SourceVersion); Assert.False(item.HasCondition); });
        Assert.Contains(snapshot.Grants, item => item.Action == "storage.objects.get");
        Assert.Contains(snapshot.Limitations, item => item.Contains("deny policies"));
        SharedWorkspaceStore.ValidateEntitlementEvidence(snapshot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Aws_DecodesDefaultPolicyVersionAndGroupsWithoutTrustOrEventNameGuessing(bool encoded)
    {
        const string account = "123456789012";
        const string userArn = "arn:aws:iam::123456789012:user/reader";
        const string groupArn = "arn:aws:iam::123456789012:group/readers";
        const string policyArn = "arn:aws:iam::123456789012:policy/client";
        var document = new { Version = "2012-10-17", Statement = new object[]
        {
            new { Sid = "Read", Effect = "Allow", Action = "s3:GetObject", Resource = "arn:aws:s3:::source/object" },
            new { Sid = "Deny", Effect = "Deny", Action = "s3:DeleteObject", Resource = "*", Condition = new { StringEquals = new { tag = "protected" } } },
            new { Effect = "Allow", NotAction = "iam:*", Resource = "*" }
        } };
        object policyDocument = encoded ? Uri.EscapeDataString(JsonSerializer.Serialize(document)) : document;
        var payload = new
        {
            UserDetailList = new[] { new { Arn = userArn, GroupList = new[] { "readers" }, UserPolicyList = Array.Empty<object>(), AttachedManagedPolicies = Array.Empty<object>() } },
            RoleDetailList = Array.Empty<object>(),
            GroupDetailList = new[] { new { Arn = groupArn, GroupName = "readers", GroupPolicyList = Array.Empty<object>(), AttachedManagedPolicies = new[] { new { PolicyArn = policyArn } } } },
            Policies = new[] { new { Arn = policyArn, DefaultVersionId = "v2", PolicyVersionList = new[] { new { VersionId = "v2", Document = policyDocument } } } },
            IsTruncated = false
        };
        var runner = new Runner("{\"Account\":\"123456789012\"}", JsonSerializer.Serialize(payload));
        var snapshot = await new EntitlementEvidenceCollector(runner).CollectAsync(new(CloudProvider.Aws, account, "audit-profile"));
        Assert.Equal(4, snapshot.Grants.Count);
        Assert.Contains(snapshot.Grants, item => item.Principal == userArn && item.SourcePrincipal == groupArn && item.Action == "s3:GetObject");
        Assert.All(snapshot.Grants, item => Assert.Equal("v2", item.SourceVersion));
        Assert.Equal(2, snapshot.Grants.Count(item => item.Effect == "Deny" && item.HasCondition));
        Assert.DoesNotContain(snapshot.Grants, item => item.Action == "iam:*");
        Assert.All(runner.Calls, item => { Assert.Contains("--profile", item.Args); Assert.Contains("audit-profile", item.Args); });
        Assert.DoesNotContain(runner.Calls, item => item.Args.Contains("put-policy") || item.Args.Contains("attach-user-policy"));
        SharedWorkspaceStore.ValidateEntitlementEvidence(snapshot);
    }

    [Fact]
    public async Task ForeignIdentityTruncationFailureAndCancellationCannotPublishEvidence()
    {
        var foreign = new Runner("{\"Account\":\"999999999999\"}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new EntitlementEvidenceCollector(foreign).CollectAsync(new(CloudProvider.Aws, "123456789012", "")));
        Assert.Single(foreign.Calls);
        var truncated = new Runner("{\"Account\":\"123456789012\"}", "{\"IsTruncated\":true}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new EntitlementEvidenceCollector(truncated).CollectAsync(new(CloudProvider.Aws, "123456789012", "")));
        var failed = new Runner() { Fail = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new EntitlementEvidenceCollector(failed).CollectAsync(new(CloudProvider.Gcp, "client-project", "")));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var empty = new Runner();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EntitlementEvidenceCollector(empty).CollectAsync(new(CloudProvider.Gcp, "client-project", ""), cancelled.Token));
        Assert.Empty(empty.Calls);
        await Assert.ThrowsAsync<ArgumentException>(() => new EntitlementEvidenceCollector(empty).CollectAsync(new(CloudProvider.Aws, "--bad", "")));
    }

    [Fact]
    public void HistoricalCorrelationNeverUsesFutureSnapshotsOrInfersIntervalsAndRemovalFromAbsence()
    {
        var now = DateTimeOffset.UtcNow;
        var account = Guid.NewGuid().ToString();
        var scope = $"/subscriptions/{account}";
        var grant = new ObservedPermissionGrant("identity", "identity", "role", "assignment", "version", new string('A', 64),
            "statement", scope, "Microsoft.Storage/storageAccounts/read", scope, "Allow", false, false, false);
        var observed = new EntitlementEvidenceSnapshot(Guid.NewGuid(), CloudProvider.Azure, account, now.AddDays(-2), now.AddDays(-2).AddMinutes(1), [grant], ["Partial"]);
        var ev = new CloudActivityEvent("event", "identity", grant.Action, grant.Action, scope + "/resourceGroups/data/providers/Microsoft.Storage/storageAccounts/source", now.AddDays(-1), true)
        { PermissionEvidenceSource = "Azure authorization.action" };
        var activity = new CloudActivitySnapshot(Guid.NewGuid(), CloudProvider.Azure, account, now.AddDays(-90), now, "Azure", "Partial", false, [ev]);
        var service = new HistoricalEntitlementAttributionService();
        HistoricalGrantCorrelation Correlate(params EntitlementEvidenceSnapshot[] history) => Assert.Single(service.Correlate(activity, history).Observations);
        Assert.Equal("CompatiblePriorObservedGrant", Correlate(observed).Status);
        Assert.Equal("NoPriorDocumentObservation", Correlate(observed with { CollectedAtUtc = now }).Status);
        var removed = observed with { SnapshotId = Guid.NewGuid(), CollectedAtUtc = now.AddDays(-1).AddHours(-1), Grants = [] };
        Assert.Equal("NoExactPriorObservedGrant", Correlate(observed, removed).Status);
        Assert.Equal("NoExactPriorObservedGrant", Correlate(observed with { Grants = [grant with { HasCondition = true }] }).Status);
        Assert.Equal("NoExactPriorObservedGrant", Correlate(observed with { Grants = [grant with { IsExclusion = true }] }).Status);
        Assert.Equal("AmbiguousObservationTime", Correlate(observed, observed with { SnapshotId = Guid.NewGuid() }).Status);
        Assert.Contains("not an authoritative validity interval", service.Correlate(activity, [observed]).Notice);
        Assert.Throws<ArgumentException>(() => service.Correlate(activity, [observed with { AccountId = Guid.NewGuid().ToString() }]));
    }

    [PostgreSqlFact]
    public async Task SavedEvidenceIsScopedIdempotentAppendOnlyAndRequiredForReadiness()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        var tenant = Guid.NewGuid();
        var organization = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new EntitlementEvidenceSnapshot(Guid.NewGuid(), CloudProvider.Gcp, "client-project", now.AddMinutes(-1), now,
            [new("user:reader@example.invalid", "user:reader@example.invalid", "roles/viewer", "project-policy", "v1", new string('A', 64), "one", "projects/client-project", "storage.objects.get", "projects/client-project", "Allow", false, false, false)], ["Partial"]);
        await store.SaveEntitlementEvidenceAsync(tenant, organization, actor, snapshot);
        await store.SaveEntitlementEvidenceAsync(tenant, organization, Guid.NewGuid(), snapshot);
        var summary = Assert.Single(await store.GetEntitlementEvidenceHistoryAsync(tenant, organization, snapshot.Provider, snapshot.AccountId));
        Assert.Equal(actor, summary.UploadedBy);
        Assert.Equal(1, summary.GrantCount);
        Assert.Equal(snapshot.Grants[0], Assert.Single((await store.GetEntitlementEvidenceAsync(tenant, organization, snapshot.Provider, snapshot.AccountId, snapshot.SnapshotId))!.Grants));
        Assert.Null(await store.GetEntitlementEvidenceAsync(Guid.NewGuid(), organization, snapshot.Provider, snapshot.AccountId, snapshot.SnapshotId));
        Assert.Empty(await store.GetEntitlementEvidenceHistoryAsync(tenant, Guid.NewGuid(), snapshot.Provider, snapshot.AccountId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveEntitlementEvidenceAsync(tenant, organization, actor, snapshot with { Grants = [] }));
        Assert.True(await store.IsReadyAsync());
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        foreach (var sql in new[] { "DELETE FROM SharedEntitlementEvidenceSnapshots", "TRUNCATE SharedEntitlementEvidenceSnapshots" })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Contains("append-only", error.MessageText);
        }
        await using var disable = new NpgsqlCommand("ALTER TABLE SharedEntitlementEvidenceSnapshots DISABLE TRIGGER TR_SharedEntitlementEvidenceSnapshots_AppendOnly", connection);
        await disable.ExecuteNonQueryAsync();
        Assert.False(await store.IsReadyAsync());
    }

    private sealed class Runner(params string[] responses) : ICloudCommandRunner
    {
        private readonly Queue<string> _responses = new(responses);
        public bool Fail { get; init; }
        public List<(string Command, IReadOnlyList<string> Args)> Calls { get; } = new();
        public Task<CloudCommandResult> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add((command, arguments));
            return Task.FromResult(Fail ? new CloudCommandResult(1, "", "secret provider error") : new(0, _responses.Dequeue(), ""));
        }
    }
}
