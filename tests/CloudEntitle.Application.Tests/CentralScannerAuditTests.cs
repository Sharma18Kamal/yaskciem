using Xunit;
using CloudEntitle.Application.Audit;
using CloudEntitle.Application.Connectors;

namespace CloudEntitle.Application.Tests;

public class CentralScannerAuditTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private sealed class FakeConnector(Func<ConnectorScope, ConnectorResult> run) : IConnector
    {
        public string Provider => "aws";

        public Task<ConnectorResult> DiscoverAsync(ConnectorScope scope, CancellationToken cancellationToken = default)
            => Task.FromResult(run(scope));
    }

    [Fact]
    public async Task Chain_verifies_and_is_isolated_per_organization()
    {
        var log = new InMemoryAuditLog();
        await log.AppendAsync(OrgA, "a", "x", "1");
        await log.AppendAsync(OrgA, "a", "y", "2");
        var b = await log.AppendAsync(OrgB, "b", "x", "1");

        Assert.Equal(1, b.Sequence);
        Assert.Equal(AuditChain.GenesisHash, b.PreviousHash);
        Assert.True((await log.VerifyAsync(OrgA)).IsValid);
        Assert.Equal(2, (await log.ReadAsync(OrgA)).Count);
    }

    [Fact]
    public async Task Tampering_and_gaps_are_detected()
    {
        var log = new InMemoryAuditLog();
        for (var i = 0; i < 3; i++) await log.AppendAsync(OrgA, "a", "act", i.ToString());
        var entries = (await log.ReadAsync(OrgA)).ToList();

        var edited = entries.ToList();
        edited[1] = edited[1] with { Payload = "forged" };
        var result = AuditChain.Verify(edited);
        Assert.False(result.IsValid);
        Assert.Equal(2, result.BrokenAtSequence);

        var gap = new List<AuditEntry> { entries[0], entries[2] };
        Assert.False(AuditChain.Verify(gap).IsValid);
    }

    [Fact]
    public async Task Scan_writes_started_then_completed_and_stores_result()
    {
        var audit = new InMemoryAuditLog();
        var store = new InMemoryConnectorResultStore();
        var scanner = new CentralScanner(
            [new FakeConnector(s => new ConnectorResult(s, DateTimeOffset.UtcNow, [], [new CoverageEntry("iam.roles", CoverageStatus.NoData)]))],
            store, audit);
        var scope = new ConnectorScope(OrgA, "aws", "111111111111");

        await scanner.ScanAsync(scope);

        Assert.Equal(["scan.started", "scan.completed"], (await audit.ReadAsync(OrgA)).Select(e => e.Action));
        Assert.NotNull(await store.GetLatestAsync(OrgA, "aws", "111111111111"));
        Assert.Null(await store.GetLatestAsync(OrgB, "aws", "111111111111"));
    }

    [Fact]
    public async Task Failing_connector_is_audited_and_rethrown()
    {
        var audit = new InMemoryAuditLog();
        var scanner = new CentralScanner([new FakeConnector(_ => throw new InvalidOperationException("boom"))], new InMemoryConnectorResultStore(), audit);

        await Assert.ThrowsAsync<InvalidOperationException>(() => scanner.ScanAsync(new ConnectorScope(OrgA, "aws", "1")));

        Assert.Equal(["scan.started", "scan.failed"], (await audit.ReadAsync(OrgA)).Select(e => e.Action));
    }

    [Fact]
    public async Task Unknown_provider_is_reported_as_unsupported()
    {
        var scanner = new CentralScanner([], new InMemoryConnectorResultStore(), new InMemoryAuditLog());

        var result = await scanner.ScanAsync(new ConnectorScope(OrgA, "oracle", "1"));

        Assert.Equal(CoverageStatus.Unsupported, Assert.Single(result.Coverage).Status);
    }
}
