using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using CloudEntitle.LicenseServer.Services;
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed class AzureBlobAuditArchiveTests
{
    private const string Endpoint = "https://auditstore.blob.core.windows.net";
    private const string ResourceId = "/subscriptions/455b906b-799c-4c50-9d04-b092ea12116d/resourceGroups/test/providers/Microsoft.Storage/storageAccounts/auditstore";

    [Theory]
    [InlineData("http://auditstore.blob.core.windows.net")]
    [InlineData("https://otherstore.blob.core.windows.net")]
    [InlineData("https://auditstore.blob.core.windows.net/container")]
    [InlineData("https://auditstore.blob.core.windows.net?sig=secret")]
    [InlineData("https://user:secret@auditstore.blob.core.windows.net")]
    [InlineData("https://auditstore.blob.core.windows.net#fragment")]
    public void ConstructorRejectsInvalidEndpoints(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => new AzureBlobAuditArchive(endpoint, ResourceId, "ciem-audit",
            new TestCredential(), 2555));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("A-container")]
    [InlineData("two--hyphens")]
    [InlineData("trailing-")]
    public void ConstructorRejectsInvalidContainers(string container)
    {
        Assert.Throws<ArgumentException>(() => new AzureBlobAuditArchive(Endpoint, ResourceId, container,
            new TestCredential(), 2555));
    }

    [Fact]
    public async Task ReadinessAcceptsPrivateVersionedLockedArchiveWithoutWriting()
    {
        using var store = new TestStore();
        await store.Archive.CheckReadinessAsync(default);
        Assert.Equal(0, store.Handler.UploadCount);
        Assert.Equal(0, store.Handler.RetentionUpdates);
        Assert.Equal(2, store.Handler.ManagementReads);
    }

    [Theory]
    [InlineData("public")]
    [InlineData("no-version-worm")]
    [InlineData("no-versioning")]
    [InlineData("unlocked")]
    [InlineData("short-retention")]
    [InlineData("append")]
    public async Task ReadinessRejectsUnsafeArchive(string condition)
    {
        using var store = new TestStore();
        switch (condition)
        {
            case "public": store.Handler.Public = true; break;
            case "no-version-worm": store.Handler.VersionWorm = false; break;
            case "no-versioning": store.Handler.Versioning = false; break;
            case "unlocked": store.Handler.PolicyState = "Unlocked"; break;
            case "short-retention": store.Handler.RetentionDays = 30; break;
            case "append": store.Handler.ProtectedAppend = true; break;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.Archive.CheckReadinessAsync(default));
        Assert.Equal(0, store.Handler.UploadCount);
    }

    [Fact]
    public async Task NewSegmentUsesConditionalLockedUploadAndVerifiesPayload()
    {
        using var store = new TestStore();
        await store.Archive.ArchiveAsync(store.Segment, default);
        Assert.Equal(1, store.Handler.UploadCount);
        Assert.Equal("*", store.Handler.UploadIfNoneMatch);
        Assert.Equal("Locked", store.Handler.UploadMode);
        Assert.True(store.Handler.RetainUntil >= store.Segment.RetainUntilUtc);
        Assert.True(store.Handler.VersionDownload);
    }

    [Fact]
    public async Task RetryExtendsAndVerifiesPinnedLockedVersionWithoutUploading()
    {
        using var store = new TestStore();
        store.Handler.Exists = true;
        store.Handler.RetainUntil = DateTimeOffset.UtcNow.AddDays(30);
        await store.Archive.ArchiveAsync(store.Segment, default);
        Assert.Equal(0, store.Handler.UploadCount);
        Assert.Equal(1, store.Handler.RetentionUpdates);
        Assert.True(store.Handler.VersionDownload);
    }

    [Fact]
    public async Task ConcurrentUploadConflictVerifiesExistingVersion()
    {
        using var store = new TestStore();
        store.Handler.UploadConflict = true;
        await store.Archive.ArchiveAsync(store.Segment, default);
        Assert.Equal(1, store.Handler.UploadCount);
        Assert.True(store.Handler.VersionDownload);
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("payload")]
    [InlineData("unlocked-blob")]
    [InlineData("expired-blob")]
    [InlineData("missing-version")]
    [InlineData("failed-extension")]
    public async Task RejectsUnverifiedStoredSegment(string condition)
    {
        using var store = new TestStore();
        store.Handler.Exists = true;
        switch (condition)
        {
            case "metadata": store.Handler.Digest = new string('0', 64); break;
            case "payload": store.Handler.StoredPayload = Encoding.UTF8.GetBytes("tampered payload"); break;
            case "unlocked-blob": store.Handler.BlobMode = "unlocked"; break;
            case "expired-blob": store.Handler.RetainUntil = DateTimeOffset.UtcNow.AddMinutes(-1); break;
            case "missing-version": store.Handler.VersionId = null; break;
            case "failed-extension":
                store.Handler.RetainUntil = DateTimeOffset.UtcNow.AddDays(30);
                store.Handler.IgnoreExtension = true;
                break;
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => store.Archive.ArchiveAsync(store.Segment, default));
        Assert.Equal(0, store.Handler.UploadCount);
    }

    [Fact]
    public async Task RejectsInvalidInputDigestBeforeAnyRequests()
    {
        using var store = new TestStore();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.Archive.ArchiveAsync(store.Segment with { Sha256 = new string('0', 64) }, default));
        Assert.Equal(0, store.Handler.ManagementReads);
    }

    [Fact]
    public async Task ManagementAccessDeniedAndCancellationAreNotSuccess()
    {
        using var store = new TestStore();
        store.Handler.DenyManagement = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => store.Archive.CheckReadinessAsync(default));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.Archive.CheckReadinessAsync(cancelled.Token));
    }

    private sealed class TestStore : IDisposable
    {
        private readonly HttpClient _client;
        public ArchiveHandler Handler { get; }
        public AzureBlobAuditArchive Archive { get; }
        public SealedAuditSegment Segment { get; }

        public TestStore()
        {
            var payload = Encoding.UTF8.GetBytes("{\"schemaVersion\":1}");
            var digest = Convert.ToHexString(SHA256.HashData(payload));
            Segment = new SealedAuditSegment(Guid.NewGuid(), 1, 1, "prior", "last", digest,
                $"audit/{digest}.json", DateTime.UtcNow.AddDays(2555), payload);
            Handler = new ArchiveHandler(payload, digest);
            _client = new HttpClient(Handler);
            var options = new BlobClientOptions { Transport = new HttpClientTransport(_client) };
            options.Retry.MaxRetries = 0;
            Archive = new AzureBlobAuditArchive(Endpoint, ResourceId, "ciem-audit",
                new TestCredential(), 2555, options, _client);
        }

        public void Dispose()
        {
            Archive.Dispose();
            _client.Dispose();
        }
    }

    private sealed class TestCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new AccessToken("test-token", DateTimeOffset.UtcNow.AddHours(1));
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class ArchiveHandler(byte[] payload, string digest) : HttpMessageHandler
    {
        private readonly long _contentLength = payload.LongLength;
        public bool Public { get; set; }
        public bool VersionWorm { get; set; } = true;
        public bool Versioning { get; set; } = true;
        public string PolicyState { get; set; } = "Locked";
        public int RetentionDays { get; set; } = 2555;
        public bool ProtectedAppend { get; set; }
        public bool DenyManagement { get; set; }
        public bool Exists { get; set; }
        public bool UploadConflict { get; set; }
        public bool IgnoreExtension { get; set; }
        public string? VersionId { get; set; } = "2026-11-09T00:00:00.0000000Z";
        public string BlobMode { get; set; } = "locked";
        public string Digest { get; set; } = digest;
        public byte[] StoredPayload { get; set; } = payload;
        public DateTimeOffset RetainUntil { get; set; } = DateTimeOffset.UtcNow.AddDays(2556);
        public int UploadCount { get; private set; }
        public int RetentionUpdates { get; private set; }
        public int ManagementReads { get; private set; }
        public string? UploadMode { get; private set; }
        public string? UploadIfNoneMatch { get; private set; }
        public bool VersionDownload { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            if (uri.Host == "management.azure.com")
            {
                ManagementReads++;
                Assert.Equal(HttpMethod.Get, request.Method);
                if (DenyManagement) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
                var json = uri.AbsolutePath.EndsWith("/immutabilityPolicies/default")
                    ? JsonSerializer.Serialize(new { properties = new
                    {
                        state = PolicyState, immutabilityPeriodSinceCreationInDays = RetentionDays,
                        allowProtectedAppendWrites = ProtectedAppend
                    } })
                    : JsonSerializer.Serialize(new { properties = new { isVersioningEnabled = Versioning } });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
            }
            if (uri.Query.Contains("restype=container"))
            {
                var container = Response(HttpStatusCode.OK);
                container.Headers.Add("x-ms-immutable-storage-with-versioning-enabled", VersionWorm.ToString().ToLowerInvariant());
                if (Public) container.Headers.Add("x-ms-blob-public-access", "blob");
                return Task.FromResult(container);
            }
            if (request.Method == HttpMethod.Put && uri.Query.Contains("comp=immutabilityPolicies"))
            {
                RetentionUpdates++;
                Assert.Contains("versionid=", uri.Query);
                if (!IgnoreExtension) RetainUntil = DateTimeOffset.Parse(Header(request, "x-ms-immutability-policy-until-date")!);
                return Task.FromResult(Response(HttpStatusCode.OK));
            }
            if (request.Method == HttpMethod.Put)
            {
                UploadCount++;
                UploadMode = Header(request, "x-ms-immutability-policy-mode");
                UploadIfNoneMatch = Header(request, "If-None-Match");
                Exists = true;
                if (UploadConflict) return Task.FromResult(Error(HttpStatusCode.PreconditionFailed, "ConditionNotMet"));
                RetainUntil = DateTimeOffset.Parse(Header(request, "x-ms-immutability-policy-until-date")!);
                return Task.FromResult(Response(HttpStatusCode.Created));
            }
            if (!Exists) return Task.FromResult(Error(HttpStatusCode.NotFound, "BlobNotFound"));
            if (request.Method == HttpMethod.Get)
            {
                VersionDownload = uri.Query.Contains("versionid=");
                Assert.Equal("\"test-etag\"", Header(request, "If-Match"));
                var download = Response(HttpStatusCode.OK);
                download.Content = new ByteArrayContent(StoredPayload);
                return Task.FromResult(download);
            }
            Assert.Equal(HttpMethod.Head, request.Method);
            var properties = Response(HttpStatusCode.OK);
            properties.Content.Headers.ContentLength = _contentLength;
            properties.Headers.Add("x-ms-meta-sha256", Digest);
            properties.Headers.Add("x-ms-immutability-policy-mode", BlobMode);
            properties.Headers.Add("x-ms-immutability-policy-until-date", RetainUntil.ToString("R"));
            if (VersionId is not null) properties.Headers.Add("x-ms-version-id", VersionId);
            return Task.FromResult(properties);
        }

        private static string? Header(HttpRequestMessage request, string name)
            => request.Headers.TryGetValues(name, out var values) ? values.Single() : null;

        private static HttpResponseMessage Response(HttpStatusCode status)
        {
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent([]) };
            response.Headers.Add("ETag", "\"test-etag\"");
            response.Content.Headers.LastModified = DateTimeOffset.UtcNow;
            response.Headers.Add("x-ms-blob-type", "BlockBlob");
            return response;
        }

        private static HttpResponseMessage Error(HttpStatusCode status, string code)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent($"<Error><Code>{code}</Code><Message>test error</Message></Error>", Encoding.UTF8, "application/xml")
            };
            response.Headers.Add("x-ms-error-code", code);
            return response;
        }
    }
}
