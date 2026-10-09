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
    public void AuditIntegrityKeyRing_ShouldLoadPreviousAndActiveKeysForRotation()
    {
        var previousKey = RandomNumberGenerator.GetBytes(32);
        var activeKey = RandomNumberGenerator.GetBytes(32);
        using var keyRing = new AuditIntegrityKeyRing("key-current", new Dictionary<string, byte[]>
        {
            ["key-previous"] = previousKey,
            ["key-current"] = activeKey
        });
        var activeCopy = keyRing.CopyCurrentKey();
        try
        {
            Assert.Equal("key-current", keyRing.CurrentKeyId);
            Assert.Equal(activeKey, activeCopy);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(previousKey);
            CryptographicOperations.ZeroMemory(activeKey);
            CryptographicOperations.ZeroMemory(activeCopy);
        }
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
        await using var dataSource = new NpgsqlDataSourceBuilder(database.ConnectionString).Build();
        var previousKey = RandomNumberGenerator.GetBytes(32);
        var activeKey = RandomNumberGenerator.GetBytes(32);
        using var originalKeyRing = new AuditIntegrityKeyRing("key-2026-09",
            new Dictionary<string, byte[]> { ["key-2026-09"] = previousKey });
        using var store = new PostgresAuditRetentionStore(dataSource, 30, originalKeyRing);
        await store.InitializeAsync();
        var tenantId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var request = new CentralAuditAppendRequest("User.Login", "token=secret", null,
            new Dictionary<string, string> { ["api-token"] = "secret" }, eventId);

        var first = await store.AppendAsync(tenantId, actorId, request);
        var duplicate = await store.AppendAsync(tenantId, Guid.NewGuid(), request);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendAsync(tenantId, actorId,
            request with { Description = "different content" }));
        var parallel = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => store.AppendAsync(tenantId, actorId,
            new CentralAuditAppendRequest($"Event.{index}", "concurrent", null, new Dictionary<string, string>()))));

        Assert.Equal(first.Hash, duplicate.Hash);
        Assert.Equal("token[REDACTED]", first.Description);
        Assert.Equal("[REDACTED]", first.Metadata["api-token"]);
        Assert.True((await store.CheckReadinessAsync()).ProductionReady);
        Assert.Equal(9, (await store.ReadAsync(tenantId, 0, 20)).Count);
        Assert.Empty(await store.ReadAsync(Guid.NewGuid(), 0, 20));
        Assert.Equal(Enumerable.Range(2, 8).Select(value => (long)value).Order(), parallel.Select(entry => entry.Sequence).Order());

        using var rotatedKeyRing = new AuditIntegrityKeyRing("key-2026-10", new Dictionary<string, byte[]>
        {
            ["key-2026-09"] = previousKey,
            ["key-2026-10"] = activeKey
        });
        using var rotatedStore = new PostgresAuditRetentionStore(dataSource, 30, rotatedKeyRing);
        var priorEvents = await rotatedStore.ReadAsync(tenantId, 0, 20);
        var rotatedEvent = await rotatedStore.AppendAsync(tenantId, actorId,
            new CentralAuditAppendRequest("Rotation.Completed", "new key in use", null, new Dictionary<string, string>()));
        Assert.All(priorEvents, entry => Assert.Equal("key-2026-09", entry.IntegrityKeyId));
        Assert.Equal("key-2026-10", rotatedEvent.IntegrityKeyId);
        Assert.Equal(10, (await rotatedStore.ReadAsync(tenantId, 0, 20)).Count);

        await using var tamperDataSource = new NpgsqlDataSourceBuilder(database.ConnectionString).Build();
        await using var connection = await tamperDataSource.OpenConnectionAsync();
        await using (var setTenant = new NpgsqlCommand("SELECT set_config('app.tenant_id', @tenant, false)", connection))
        {
            setTenant.Parameters.AddWithValue("tenant", tenantId.ToString("D"));
            await setTenant.ExecuteScalarAsync();
        }
        await using (var rejectUpdate = new NpgsqlCommand("UPDATE ciem_audit_events SET event_json = event_json WHERE tenant_id = @tenant AND sequence = 1", connection))
        {
            rejectUpdate.Parameters.AddWithValue("tenant", tenantId);
            await Assert.ThrowsAsync<PostgresException>(() => rejectUpdate.ExecuteNonQueryAsync());
        }
        await using (var disableTrigger = new NpgsqlCommand("ALTER TABLE ciem_audit_events DISABLE TRIGGER TR_CiemAuditEvents_AppendOnly", connection))
            await disableTrigger.ExecuteNonQueryAsync();
        await using var tamper = new NpgsqlCommand("UPDATE ciem_audit_events SET event_json = replace(event_json, 'Event.', 'Tamper.') WHERE tenant_id = @tenant AND sequence = 2", connection);
        tamper.Parameters.AddWithValue("tenant", tenantId);
        await tamper.ExecuteNonQueryAsync();
        await using (var enableTrigger = new NpgsqlCommand("ALTER TABLE ciem_audit_events ENABLE TRIGGER TR_CiemAuditEvents_AppendOnly", connection))
            await enableTrigger.ExecuteNonQueryAsync();
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
