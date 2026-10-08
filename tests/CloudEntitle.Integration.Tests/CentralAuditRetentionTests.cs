using System.IO;
using System.Security.Cryptography;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.LicenseServer.Services;
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed class CentralAuditRetentionTests
{
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
