using CloudEntitle.Application.Connectors;
using CloudEntitle.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CloudEntitle.Infrastructure.Tests;

public class SqliteAuditAndResultStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ciem-{Guid.NewGuid():N}.db");
    private string ConnectionString => $"Data Source={_path};Pooling=False";

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    [Fact]
    public async Task Audit_chain_persists_verifies_and_isolates_organizations()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var log = new SqliteAuditLog(ConnectionString);
        await log.AppendAsync(a, "system", "one", "{}");
        await log.AppendAsync(a, "system", "two", "{}");
        await log.AppendAsync(b, "system", "other", "{}");

        var reopened = new SqliteAuditLog(ConnectionString);
        Assert.Equal(2, (await reopened.ReadAsync(a)).Count);
        Assert.Single(await reopened.ReadAsync(b));
        Assert.True((await reopened.VerifyAsync(a)).IsValid);
    }

    [Fact]
    public async Task Concurrent_appends_keep_a_valid_chain()
    {
        var org = Guid.NewGuid();
        var log = new SqliteAuditLog(ConnectionString);
        await Task.WhenAll(Enumerable.Range(0, 10).Select(i => log.AppendAsync(org, "t", $"a{i}", "{}")));
        Assert.Equal(10, (await log.ReadAsync(org)).Count);
        Assert.True((await log.VerifyAsync(org)).IsValid);
    }

    [Fact]
    public async Task Updates_and_deletes_are_rejected()
    {
        var org = Guid.NewGuid();
        var log = new SqliteAuditLog(ConnectionString);
        await log.AppendAsync(org, "system", "one", "{}");

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE audit_log SET payload = 'x'";
        Assert.Throws<SqliteException>(() => update.ExecuteNonQuery());
        using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM audit_log";
        Assert.Throws<SqliteException>(() => delete.ExecuteNonQuery());
    }

    [Fact]
    public async Task Result_store_round_trips_without_secret_and_is_tenant_scoped()
    {
        var org = Guid.NewGuid();
        var store = new SqliteConnectorResultStore(ConnectionString);
        var scope = new ConnectorScope(org, "aws", "123456789012", "arn:aws:iam::123456789012:role/r", "secret");
        var identity = new ConnectorIdentity("role", "arn", "r", new Dictionary<string, string> { ["path"] = "/" });
        await store.SaveAsync(new ConnectorResult(scope, DateTimeOffset.UtcNow, [identity], [new CoverageEntry("iam.roles", CoverageStatus.Collected)]));

        var loaded = await store.GetLatestAsync(org, "aws", "123456789012");
        Assert.NotNull(loaded);
        Assert.Null(loaded!.Scope.ExternalSecret);
        Assert.Equal("/", loaded.Identities[0].Attributes["path"]);
        Assert.Null(await store.GetLatestAsync(Guid.NewGuid(), "aws", "123456789012"));
    }
}
