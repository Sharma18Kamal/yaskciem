using System.IO;
using System.Security.Cryptography;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.LicenseServer.Services;
using Npgsql;
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed class CentralAuditRetentionTests
{
    [Fact]
    public void PostgresAuditStore_ShouldRequireRetentionAndIntegrityKeyConfiguration()
    {
        using var dataSource = new NpgsqlDataSourceBuilder("Host=localhost;Database=yask_test;Username=yask_test;Password=test").Build();

        Assert.Throws<ArgumentOutOfRangeException>(() => new PostgresAuditRetentionStore(dataSource, 0, RandomNumberGenerator.GetBytes(32)));
        Assert.Throws<ArgumentException>(() => new PostgresAuditRetentionStore(dataSource, 30, RandomNumberGenerator.GetBytes(16)));
    }

    [Fact]
    public async Task LocalFileAuditStore_ShouldNeverReportProductionReadiness()
    {
        var directory = Path.Combine(Path.GetTempPath(), "YaskCiemLocalAuditReadiness", Guid.NewGuid().ToString("N"));
        using var store = new AppendOnlyAuditRetentionStore(directory, 30, RandomNumberGenerator.GetBytes(32));

        var readiness = await store.CheckReadinessAsync();

        Assert.True(readiness.Ready);
        Assert.False(readiness.ProductionReady);
        Assert.Contains("not multi-instance safe", readiness.Details, StringComparison.OrdinalIgnoreCase);
    }

    [PostgreSqlFact]
    public async Task PostgresAuditStore_ShouldSequenceDeduplicateIsolateAndDetectTampering()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new PostgresAuditRetentionStore(database.ConnectionString, 30, RandomNumberGenerator.GetBytes(32));
        await store.InitializeAsync();
        var tenantId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var request = new CentralAuditAppendRequest("User.Login", "token=secret", null,
            new Dictionary<string, string> { ["api-token"] = "secret" }, eventId);

        var first = await store.AppendAsync(tenantId, actorId, request);
        var duplicate = await store.AppendAsync(tenantId, Guid.NewGuid(), request);
        var parallel = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => store.AppendAsync(tenantId, actorId,
            new CentralAuditAppendRequest($"Event.{index}", "concurrent", null, new Dictionary<string, string>()))));

        Assert.Equal(first.Hash, duplicate.Hash);
        Assert.Equal("token=[REDACTED]", first.Description);
        Assert.Equal("[REDACTED]", first.Metadata["api-token"]);
        Assert.Equal(9, (await store.ReadAsync(tenantId, 0, 20)).Count);
        Assert.Empty(await store.ReadAsync(Guid.NewGuid(), 0, 20));
        Assert.Equal(Enumerable.Range(2, 8).Select(value => (long)value).Order(), parallel.Select(entry => entry.Sequence).Order());

        await using var tamperDataSource = new NpgsqlDataSourceBuilder(database.ConnectionString).Build();
        await using var connection = await tamperDataSource.OpenConnectionAsync();
        await using (var disableTrigger = new NpgsqlCommand("ALTER TABLE ciem_audit_events DISABLE TRIGGER TR_CiemAuditEvents_AppendOnly", connection))
            await disableTrigger.ExecuteNonQueryAsync();
        await using var tamper = new NpgsqlCommand("UPDATE ciem_audit_events SET event_json = replace(event_json, 'Event.', 'Tamper.') WHERE tenant_id = @tenant AND sequence = 2", connection);
        tamper.Parameters.AddWithValue("tenant", tenantId);
        await tamper.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(tenantId, 0, 20));
    }

    [Fact]
    public async Task AppendAsync_ShouldSequenceByTenantRedactSensitiveDataAndRejectTampering()
    {
        var directory = Path.Combine(Path.GetTempPath(), "YaskCiemCentralAuditTests", Guid.NewGuid().ToString("N"));
        using var store = new AppendOnlyAuditRetentionStore(directory, minimumRetentionDays: 30, RandomNumberGenerator.GetBytes(32));
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        try
        {
            var eventId = Guid.NewGuid();
            var firstRequest = new CentralAuditAppendRequest(
                "User.Login", "request token=hidden", DateTime.UtcNow,
                new Dictionary<string, string> { ["ticket"] = "CR-12", ["api-token"] = "hidden" }, eventId);
            var first = await store.AppendAsync(tenantId, actorId, firstRequest);
            var retry = await store.AppendAsync(tenantId, Guid.NewGuid(), firstRequest);
            var second = await store.AppendAsync(tenantId, actorId, new CentralAuditAppendRequest(
                "Review.Completed", "review submitted", null, new Dictionary<string, string>()));
            var otherTenantEvent = await store.AppendAsync(otherTenantId, Guid.NewGuid(), new CentralAuditAppendRequest(
                "Other.Event", "isolated", null, new Dictionary<string, string>()));

            Assert.Equal(1, first.Sequence);
            Assert.Equal(first.Hash, retry.Hash);
            Assert.Equal(2, second.Sequence);
            Assert.Equal(1, otherTenantEvent.Sequence);
            Assert.Equal(actorId, first.ActorObjectId);
            Assert.Equal("request token[REDACTED]", first.Description);
            Assert.Equal("[REDACTED]", first.Metadata["api-token"]);
            Assert.True(first.RetainUntilUtc > first.RecordedAtUtc);
            Assert.Equal(new[] { 1L, 2L }, (await store.ReadAsync(tenantId, 0, 10)).Select(entry => entry.Sequence));
            Assert.Empty(await store.ReadAsync(Guid.NewGuid(), 0, 10));

            var logPath = Path.Combine(directory, $"{tenantId:N}.audit.jsonl");
            var log = await File.ReadAllTextAsync(logPath);
            await File.WriteAllTextAsync(logPath, log.Replace("User.Login", "User.L0gin", StringComparison.Ordinal));

            await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(tenantId, 0, 10));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.AppendAsync(tenantId, actorId, new CentralAuditAppendRequest(
                "Another.Event", "must fail", null, new Dictionary<string, string>())));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
