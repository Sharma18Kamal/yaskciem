using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using CloudEntitle.Security.Services;

namespace CloudEntitle.LicenseServer.Services;

public sealed class AuditArchiveStatus
{
    private Snapshot _snapshot = new(false, "WORM audit archive has not passed its startup check.", 0, null, null);

    public Snapshot Current => Volatile.Read(ref _snapshot);

    public void Set(bool ready, string details, AuditArchiveProgress? progress = null)
    {
        var previous = Current;
        Volatile.Write(ref _snapshot, new Snapshot(ready, details,
            progress?.PendingEvents ?? previous.PendingEvents,
            progress?.OldestPendingAtUtc ?? previous.OldestPendingAtUtc,
            progress?.LastSuccessfulArchiveAtUtc ?? previous.LastSuccessfulArchiveAtUtc));
    }

    public sealed record Snapshot(bool Ready, string Details, long PendingEvents,
        DateTime? OldestPendingAtUtc, DateTime? LastSuccessfulArchiveAtUtc);
}

public sealed class S3ObjectLockAuditArchive : IAuditSegmentArchive, IDisposable
{
    private readonly IAmazonS3 _client;
    private readonly string _bucketName;
    private readonly int _minimumRetentionDays;

    public S3ObjectLockAuditArchive(string endpoint, string region, string bucketName, string accessKey,
        string secretKey, int minimumRetentionDays)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri)
            || endpointUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrWhiteSpace(endpointUri.UserInfo))
            throw new ArgumentException("The audit archive endpoint must be an HTTPS URL without embedded credentials.", nameof(endpoint));
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucketName);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretKey);
        if (minimumRetentionDays is < 1 or > 36500) throw new ArgumentOutOfRangeException(nameof(minimumRetentionDays));

        _bucketName = bucketName;
        _minimumRetentionDays = minimumRetentionDays;
        _client = new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey), new AmazonS3Config
        {
            ServiceURL = endpointUri.ToString().TrimEnd('/'),
            ForcePathStyle = true,
            AuthenticationRegion = region,
            UseHttp = false
        });
    }

    public async Task CheckReadinessAsync(CancellationToken cancellationToken)
    {
        var versioning = await _client.GetBucketVersioningAsync(new GetBucketVersioningRequest
        {
            BucketName = _bucketName
        }, cancellationToken);
        if (versioning.VersioningConfig?.Status?.Value != "Enabled")
            throw new InvalidOperationException("The audit archive bucket must have versioning enabled.");

        var lockConfiguration = await _client.GetObjectLockConfigurationAsync(new GetObjectLockConfigurationRequest
        {
            BucketName = _bucketName
        }, cancellationToken);
        if (lockConfiguration.ObjectLockConfiguration?.ObjectLockEnabled != ObjectLockEnabled.Enabled)
            throw new InvalidOperationException("The audit archive bucket must have Object Lock enabled.");

        var defaultRetention = lockConfiguration.ObjectLockConfiguration.Rule?.DefaultRetention;
        if (defaultRetention?.Mode != ObjectLockRetentionMode.Compliance
            || defaultRetention.Days < _minimumRetentionDays)
            throw new InvalidOperationException($"The bucket default retention must be COMPLIANCE mode for at least {_minimumRetentionDays} days.");
    }

    public async Task ArchiveAsync(SealedAuditSegment segment, CancellationToken cancellationToken)
    {
        var existing = await GetMetadataOrNullAsync(segment.ObjectKey, cancellationToken);
        if (existing is not null)
        {
            await VerifyAndExtendRetentionAsync(segment, existing, cancellationToken);
            return;
        }

        await using var payload = new MemoryStream(segment.Payload, writable: false);
        var putRequest = new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = segment.ObjectKey,
            InputStream = payload,
            ContentType = "application/json",
            ObjectLockMode = ObjectLockMode.Compliance,
            ObjectLockRetainUntilDate = segment.RetainUntilUtc
        };
        putRequest.Metadata.Add("sha256", segment.Sha256);
        await _client.PutObjectAsync(putRequest, cancellationToken);

        var stored = await _client.GetObjectMetadataAsync(_bucketName, segment.ObjectKey, cancellationToken);
        await VerifyAndExtendRetentionAsync(segment, stored, cancellationToken);
    }

    private async Task<GetObjectMetadataResponse?> GetMetadataOrNullAsync(string objectKey, CancellationToken cancellationToken)
    {
        try { return await _client.GetObjectMetadataAsync(_bucketName, objectKey, cancellationToken); }
        catch (AmazonS3Exception exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound
            || exception.ErrorCode is "NoSuchKey" or "NotFound") { return null; }
    }

    private async Task VerifyAndExtendRetentionAsync(SealedAuditSegment segment, GetObjectMetadataResponse stored,
        CancellationToken cancellationToken)
    {
        var storedHash = stored.Metadata.Keys
            .Where(key => string.Equals(key, "x-amz-meta-sha256", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "sha256", StringComparison.OrdinalIgnoreCase))
            .Select(key => stored.Metadata[key])
            .FirstOrDefault();
        if (!string.Equals(storedHash, segment.Sha256, StringComparison.OrdinalIgnoreCase)
            || stored.ContentLength != segment.Payload.LongLength)
            throw new InvalidDataException("An object already exists at the sealed audit key but does not match the segment digest.");

        if (stored.ObjectLockMode != ObjectLockMode.Compliance)
            throw new InvalidDataException("The sealed audit object is not retained in Object Lock COMPLIANCE mode.");

        var requiredRetention = DateTime.UtcNow.AddDays(_minimumRetentionDays);
        if (stored.ObjectLockRetainUntilDate >= requiredRetention) return;
        if (string.IsNullOrWhiteSpace(stored.VersionId))
            throw new InvalidDataException("The object store did not return a version ID for the locked audit object.");

        await _client.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = _bucketName,
            Key = segment.ObjectKey,
            VersionId = stored.VersionId,
            BypassGovernanceRetention = false,
            Retention = new ObjectLockRetention
            {
                Mode = ObjectLockRetentionMode.Compliance,
                RetainUntilDate = requiredRetention
            }
        }, cancellationToken);

        var verified = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest
        {
            BucketName = _bucketName,
            Key = segment.ObjectKey,
            VersionId = stored.VersionId
        }, cancellationToken);
        if (verified.ObjectLockMode != ObjectLockMode.Compliance
            || verified.ObjectLockRetainUntilDate < requiredRetention)
            throw new InvalidDataException("The object store did not confirm the extended COMPLIANCE retention period.");
    }

    public void Dispose() => _client.Dispose();
}

public sealed class AuditArchiveWorker(
    PostgresAuditSegmentArchiver segmentArchiver,
    IAuditSegmentArchive destination,
    AuditArchiveStatus status,
    int pollIntervalSeconds,
    long maximumPendingEvents,
    int maximumLagMinutes,
    ILogger<AuditArchiveWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await destination.CheckReadinessAsync(stoppingToken);
                while (await segmentArchiver.ArchiveNextAsync(destination, stoppingToken))
                {
                    stoppingToken.ThrowIfCancellationRequested();
                }
                var progress = await segmentArchiver.GetArchiveProgressAsync(stoppingToken);
                var laggedBy = progress.OldestPendingAtUtc is { } oldest
                    ? DateTime.UtcNow - oldest
                    : TimeSpan.Zero;
                var lagging = progress.PendingEvents > maximumPendingEvents
                    || laggedBy > TimeSpan.FromMinutes(maximumLagMinutes);
                var details = lagging
                    ? $"WORM archive lag threshold exceeded: {progress.PendingEvents} pending event(s), oldest pending age {laggedBy.TotalMinutes:F0} minute(s)."
                    : "S3-compatible audit archive is reachable with versioning and COMPLIANCE Object Lock retention.";
                status.Set(!lagging, details, progress);
                if (lagging)
                    logger.LogWarning("WORM audit archive is lagging: {PendingEvents} pending event(s), oldest pending age {OldestPendingAgeMinutes} minute(s).",
                        progress.PendingEvents, laggedBy.TotalMinutes);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                var details = SensitiveDataRedactor.RedactText(exception.Message);
                status.Set(false, $"WORM audit archive unavailable: {details}");
                logger.LogError("WORM audit archive worker failed: {Details}", details);
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(pollIntervalSeconds, 5, 3600)), stoppingToken);
        }
    }
}
