using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace CloudEntitle.LicenseServer.Services;

public sealed class AzureBlobAuditArchive : IAuditSegmentArchive, IDisposable
{
    private readonly BlobContainerClient _container;
    private readonly TokenCredential _credential;
    private readonly HttpClient _managementClient;
    private readonly bool _ownsManagementClient;
    private readonly string _blobServiceResourceId;
    private readonly int _minimumRetentionDays;

    public AzureBlobAuditArchive(string endpoint, string storageAccountResourceId, string containerName,
        TokenCredential credential, int minimumRetentionDays, BlobClientOptions? clientOptions = null,
        HttpClient? managementClient = null)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (minimumRetentionDays is < 1 or > 36500)
            throw new ArgumentOutOfRangeException(nameof(minimumRetentionDays));
        var resourceMatch = Regex.Match(storageAccountResourceId ?? "",
            @"^/subscriptions/[0-9a-fA-F-]{36}/resourceGroups/[^/?#]+/providers/Microsoft\.Storage/storageAccounts/([a-z0-9]{3,24})$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!resourceMatch.Success)
            throw new ArgumentException("Provide the Azure storage account ARM resource ID.", nameof(storageAccountResourceId));
        var accountName = resourceMatch.Groups[1].Value;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort
            || uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || !string.Equals(uri.Host, $"{accountName}.blob.core.windows.net", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use the matching Azure public-cloud Blob service HTTPS endpoint without paths, SAS tokens or credentials.", nameof(endpoint));
        if (!Regex.IsMatch(containerName ?? "", @"^[a-z0-9](?:[a-z0-9]|-(?!-)){1,61}[a-z0-9]$",
            RegexOptions.CultureInvariant))
            throw new ArgumentException("Provide a valid private Azure Blob container name.", nameof(containerName));

        _container = new BlobServiceClient(uri, credential, clientOptions).GetBlobContainerClient(containerName);
        _credential = credential;
        _minimumRetentionDays = minimumRetentionDays;
        _blobServiceResourceId = storageAccountResourceId + "/blobServices/default";
        _ownsManagementClient = managementClient is null;
        _managementClient = managementClient ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        }) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task CheckReadinessAsync(CancellationToken cancellationToken)
    {
        var container = (await _container.GetPropertiesAsync(cancellationToken: cancellationToken)).Value;
        if (container.PublicAccess != PublicAccessType.None)
            throw new InvalidOperationException("The Azure audit archive container must not allow anonymous public access.");
        if (!container.HasImmutableStorageWithVersioning)
            throw new InvalidOperationException("Enable version-level immutable storage on the Azure audit archive container.");

        using var service = await ReadManagementResourceAsync(_blobServiceResourceId, cancellationToken);
        if (!service.RootElement.GetProperty("properties").TryGetProperty("isVersioningEnabled", out var versioning)
            || versioning.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("Azure Blob versioning must be enabled for the audit archive.");

        using var policy = await ReadManagementResourceAsync(
            $"{_blobServiceResourceId}/containers/{_container.Name}/immutabilityPolicies/default", cancellationToken);
        var properties = policy.RootElement.GetProperty("properties");
        if (!properties.TryGetProperty("state", out var state) || state.GetString() != "Locked"
            || !properties.TryGetProperty("immutabilityPeriodSinceCreationInDays", out var days)
            || !days.TryGetInt32(out var retentionDays) || retentionDays < _minimumRetentionDays
            || properties.TryGetProperty("allowProtectedAppendWrites", out var append) && append.ValueKind == JsonValueKind.True
            || properties.TryGetProperty("allowProtectedAppendWritesAll", out var appendAll) && appendAll.ValueKind == JsonValueKind.True)
            throw new InvalidOperationException($"The Azure archive requires a locked default retention policy of at least {_minimumRetentionDays} days without protected append writes.");
    }

    public async Task ArchiveAsync(SealedAuditSegment segment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var digest = Convert.ToHexString(SHA256.HashData(segment.Payload));
        if (!string.Equals(digest, segment.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The sealed audit payload does not match its digest.");
        await CheckReadinessAsync(cancellationToken);
        var requiredRetention = new DateTimeOffset(DateTime.UtcNow.AddDays(_minimumRetentionDays));
        var segmentRetention = new DateTimeOffset(segment.RetainUntilUtc.ToUniversalTime());
        if (segmentRetention > requiredRetention) requiredRetention = segmentRetention;
        // Azure serializes retention timestamps to seconds; round up, never shorten retention.
        requiredRetention = DateTimeOffset.FromUnixTimeSeconds(requiredRetention.ToUnixTimeSeconds() + 1);
        var blob = _container.GetBlobClient(segment.ObjectKey);
        var existing = await GetPropertiesOrNullAsync(blob, cancellationToken);
        if (existing is null)
        {
            await using var payload = new MemoryStream(segment.Payload, writable: false);
            try
            {
                await blob.UploadAsync(payload, new BlobUploadOptions
                {
                    Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                    HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
                    Metadata = new Dictionary<string, string> { ["sha256"] = segment.Sha256 },
                    ImmutabilityPolicy = new BlobImmutabilityPolicy
                    {
                        PolicyMode = BlobImmutabilityPolicyMode.Locked,
                        ExpiresOn = requiredRetention
                    }
                }, cancellationToken);
            }
            catch (RequestFailedException exception) when (
                exception.Status == 409 && exception.ErrorCode == "BlobAlreadyExists"
                || exception.Status == 412 && exception.ErrorCode == "ConditionNotMet")
            {
                // Another worker committed this key; verify its locked version instead of overwriting it.
            }
            existing = (await blob.GetPropertiesAsync(cancellationToken: cancellationToken)).Value;
        }

        if (string.IsNullOrWhiteSpace(existing.VersionId))
            throw new InvalidDataException("Azure did not return an immutable audit blob version ID.");
        var version = blob.WithVersion(existing.VersionId);
        var stored = (await version.GetPropertiesAsync(cancellationToken: cancellationToken)).Value;
        if (stored.ContentLength != segment.Payload.LongLength
            || !stored.Metadata.TryGetValue("sha256", out var storedDigest)
            || !string.Equals(storedDigest, digest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("An existing Azure audit blob does not match the sealed segment.");
        if (stored.ImmutabilityPolicy?.PolicyMode != BlobImmutabilityPolicyMode.Locked
            || stored.ImmutabilityPolicy.ExpiresOn <= DateTimeOffset.UtcNow)
            throw new InvalidDataException("The Azure audit blob version is not protected by locked retention.");

        var downloaded = (await version.DownloadContentAsync(
            new BlobRequestConditions { IfMatch = stored.ETag }, cancellationToken)).Value.Content;
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(downloaded.ToMemory().Span)), digest,
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The archived Azure audit blob payload failed digest verification.");

        if (stored.ImmutabilityPolicy.ExpiresOn < requiredRetention)
        {
            await version.SetImmutabilityPolicyAsync(new BlobImmutabilityPolicy
            {
                PolicyMode = BlobImmutabilityPolicyMode.Locked,
                ExpiresOn = requiredRetention
            }, cancellationToken: cancellationToken);
            stored = (await version.GetPropertiesAsync(cancellationToken: cancellationToken)).Value;
        }
        if (stored.ImmutabilityPolicy?.PolicyMode != BlobImmutabilityPolicyMode.Locked
            || stored.ImmutabilityPolicy.ExpiresOn < requiredRetention)
            throw new InvalidDataException("Azure did not confirm the required locked audit retention period.");
    }

    private static async Task<BlobProperties?> GetPropertiesOrNullAsync(BlobClient blob, CancellationToken cancellationToken)
    {
        try { return (await blob.GetPropertiesAsync(cancellationToken: cancellationToken)).Value; }
        catch (RequestFailedException exception) when (exception.Status == 404 && exception.ErrorCode == "BlobNotFound")
        {
            return null;
        }
    }

    private async Task<JsonDocument> ReadManagementResourceAsync(string resourceId, CancellationToken cancellationToken)
    {
        var token = await _credential.GetTokenAsync(
            new TokenRequestContext(["https://management.azure.com/.default"]), cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://management.azure.com{resourceId}?api-version=2023-05-01");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var response = await _managementClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    public void Dispose()
    {
        if (_ownsManagementClient) _managementClient.Dispose();
    }
}
