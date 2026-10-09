using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.Security.Services;

namespace CloudEntitle.LicenseServer.Services;

public sealed class AzureBlobAuditRetentionStore : IAuditRetentionStore, IDisposable
{
    private const int MaximumAppendAttempts = 8;
    private const int MaximumAppendBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient ManagementClient = new();
    private readonly BlobContainerClient _container;
    private readonly TokenCredential _credential;
    private readonly string _storageAccountResourceId;
    private readonly int _minimumRetentionDays;
    private readonly byte[] _integrityKey;
    private readonly bool _ownsCredential;

    public AzureBlobAuditRetentionStore(
        Uri blobServiceUri,
        string containerName,
        string storageAccountResourceId,
        int minimumRetentionDays,
        ReadOnlySpan<byte> integrityKey,
        string managedIdentityClientId,
        TokenCredential? credential = null)
    {
        ArgumentNullException.ThrowIfNull(blobServiceUri);
        if (blobServiceUri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(blobServiceUri.Host)
            || blobServiceUri.AbsolutePath.Trim('/') is not "")
            throw new ArgumentException("Audit Blob URI must be an HTTPS storage-account endpoint without a path.", nameof(blobServiceUri));
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        if (containerName.Length is < 3 or > 63 || containerName.Any(character => !(char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-')))
            throw new ArgumentException("Audit container name is invalid.", nameof(containerName));
        ArgumentException.ThrowIfNullOrWhiteSpace(storageAccountResourceId);
        if (minimumRetentionDays is < 1 or > 36500) throw new ArgumentOutOfRangeException(nameof(minimumRetentionDays));
        if (integrityKey.Length < 32) throw new ArgumentException("The audit integrity key must contain at least 32 bytes.", nameof(integrityKey));
        ArgumentException.ThrowIfNullOrWhiteSpace(managedIdentityClientId);

        _storageAccountResourceId = NormalizeStorageAccountResourceId(storageAccountResourceId);
        _minimumRetentionDays = minimumRetentionDays;
        _integrityKey = integrityKey.ToArray();
        _credential = credential ?? new ManagedIdentityCredential(managedIdentityClientId);
        _ownsCredential = credential is null;
        _container = new BlobServiceClient(blobServiceUri, _credential).GetBlobContainerClient(containerName);
    }

    public async Task<AuditStoreReadiness> CheckReadinessAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var containerProperties = await _container.GetPropertiesAsync(cancellationToken: cancellationToken);
            if (containerProperties.Value.HasImmutabilityPolicy != true || containerProperties.Value.PublicAccess is not null and not PublicAccessType.None)
                return new(false, false, "Audit container is not private with an immutability policy.");

            var account = await GetArmResourceAsync(_storageAccountResourceId, "2026-06-01", cancellationToken);
            var accountProperties = account.RootElement.GetProperty("properties");
            if (ReadString(accountProperties, "publicNetworkAccess") != "Disabled"
                || ReadBoolean(accountProperties, "allowSharedKeyAccess")
                || !ReadBoolean(accountProperties, "supportsHttpsTrafficOnly")
                || ReadString(accountProperties, "minimumTlsVersion") is not ("TLS1_2" or "TLS1_3")
                || ReadString(ReadProperty(accountProperties, "networkAcls"), "defaultAction") != "Deny")
                return new(false, false, "Storage account must disable public access and shared keys, require HTTPS/TLS 1.2+, and default-deny network traffic.");

            var policy = await GetArmResourceAsync($"{_storageAccountResourceId}/blobServices/default/containers/{_container.Name}/immutabilityPolicies/default", "2026-06-01", cancellationToken);
            var policyProperties = policy.RootElement.GetProperty("properties");
            var retentionDays = ReadInt32(policyProperties, "immutabilityPeriodSinceCreationInDays");
            if (ReadString(policyProperties, "state") != "Locked"
                || retentionDays < _minimumRetentionDays
                || !ReadBoolean(policyProperties, "allowProtectedAppendWrites"))
                return new(false, false, $"Audit container immutability must be Locked for at least {_minimumRetentionDays} days with protected append writes enabled.");

            return new(true, true, $"Locked container-level WORM verified for {retentionDays} days; protected append enabled; account networking and shared-key restrictions verified.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return new(false, false, $"Azure Blob audit readiness check failed: {SensitiveDataRedactor.RedactText(exception.Message)}");
        }
    }

    public async Task<CentralAuditEventDto> AppendAsync(Guid tenantId, Guid actorObjectId, CentralAuditAppendRequest request,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty || actorObjectId == Guid.Empty) throw new ArgumentException("Authenticated tenant and actor IDs are required.");
        Validate(request);
        var readiness = await CheckReadinessAsync(cancellationToken);
        if (!readiness.ProductionReady) throw new InvalidOperationException(readiness.Details);

        var log = _container.GetAppendBlobClient($"{tenantId:N}.audit.jsonl");
        await log.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        var eventType = request.EventType.Trim();
        var description = SensitiveDataRedactor.RedactText(request.Description.Trim());
        var sourceTimestamp = request.SourceTimestampUtc?.ToUniversalTime();
        var metadata = SensitiveDataRedactor.RedactMetadata(request.Metadata)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        for (var attempt = 0; attempt < MaximumAppendAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scan = await ReadAndVerifyAsync(log, tenantId, afterSequence: 0, limit: 0, request.EventId, cancellationToken);
            if (scan.Duplicate is { } duplicate)
            {
                if (duplicate.EventType != eventType || duplicate.Description != description
                    || duplicate.SourceTimestampUtc != sourceTimestamp || !MetadataEquals(duplicate.Metadata, metadata))
                    throw new InvalidOperationException("The audit event ID is already assigned to different event content.");
                return duplicate;
            }

            var recordedAtUtc = DateTime.UtcNow;
            var entry = new CentralAuditEventDto(scan.LastSequence + 1,
                request.EventId == Guid.Empty ? Guid.NewGuid() : request.EventId, tenantId, actorObjectId,
                eventType, description, recordedAtUtc, sourceTimestamp, metadata,
                scan.LastHash, string.Empty, recordedAtUtc.AddDays(_minimumRetentionDays));
            entry = entry with { Hash = ComputeHash(entry) };
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, JsonOptions) + "\n");
            if (bytes.Length > MaximumAppendBytes)
            {
                CryptographicOperations.ZeroMemory(bytes);
                throw new ArgumentException("The serialized audit event exceeds the Azure Append Blob block limit.", nameof(request));
            }

            try
            {
                await using var stream = new MemoryStream(bytes, writable: false);
                await log.AppendBlockAsync(stream, new AppendBlobAppendBlockOptions
                {
                    Conditions = new AppendBlobRequestConditions { IfAppendPositionEqual = scan.ByteLength }
                }, cancellationToken);
                return entry;
            }
            catch (RequestFailedException exception) when (exception.Status == (int)HttpStatusCode.PreconditionFailed
                || exception.ErrorCode is "AppendPositionConditionNotMet" or "ConditionNotMet")
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), cancellationToken);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        throw new InvalidOperationException("Audit append contention exceeded the retry limit.");
    }

    public async Task<IReadOnlyList<CentralAuditEventDto>> ReadAsync(Guid tenantId, long afterSequence, int limit,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("An authenticated tenant ID is required.", nameof(tenantId));
        if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        var readiness = await CheckReadinessAsync(cancellationToken);
        if (!readiness.ProductionReady) throw new InvalidOperationException(readiness.Details);
        var log = _container.GetAppendBlobClient($"{tenantId:N}.audit.jsonl");
        return (await ReadAndVerifyAsync(log, tenantId, afterSequence, limit, null, cancellationToken)).VisibleEntries;
    }

    private async Task<AuditLogScan> ReadAndVerifyAsync(AppendBlobClient log, Guid tenantId, long afterSequence, int limit,
        Guid eventId, CancellationToken cancellationToken)
    {
        BlobDownloadStreamingResult download;
        try
        {
            download = (await log.DownloadStreamingAsync(cancellationToken: cancellationToken)).Value;
        }
        catch (RequestFailedException exception) when (exception.Status == (int)HttpStatusCode.NotFound)
        {
            return new AuditLogScan(0, 0, new string('0', 64), null, Array.Empty<CentralAuditEventDto>());
        }

        await using var content = download.Content;
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var visible = new List<CentralAuditEventDto>();
        CentralAuditEventDto? duplicate = null;
        var previousHash = new string('0', 64);
        long expectedSequence = 1;
        long byteLength = 0;
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            byteLength += Encoding.UTF8.GetByteCount(line) + 1;
            if (line.Length == 0) throw new InvalidDataException("The Azure audit append blob contains an empty or truncated record.");
            CentralAuditEventDto entry;
            try
            {
                entry = JsonSerializer.Deserialize<CentralAuditEventDto>(line, JsonOptions)
                    ?? throw new InvalidDataException("The Azure audit append blob contains an invalid record.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The Azure audit append blob contains an invalid record; refusing read or append.", exception);
            }
            if (entry.TenantId != tenantId || entry.EventId == Guid.Empty || entry.Sequence != expectedSequence
                || !string.Equals(entry.PreviousHash, previousHash, StringComparison.Ordinal)
                || !FixedEquals(entry.Hash, ComputeHash(entry)))
                throw new InvalidDataException("The Azure audit append blob hash chain is invalid; refusing read or append.");
            if (eventId != Guid.Empty && entry.EventId == eventId) duplicate = entry;
            if (entry.Sequence > afterSequence && limit > 0 && visible.Count < limit) visible.Add(entry);
            previousHash = entry.Hash;
            expectedSequence++;
        }

        return new AuditLogScan(expectedSequence - 1, byteLength, previousHash, duplicate, visible);
    }

    private async Task<JsonDocument> GetArmResourceAsync(string resourceId, string apiVersion, CancellationToken cancellationToken)
    {
        var accessToken = await _credential.GetTokenAsync(new TokenRequestContext(new[] { "https://management.azure.com/.default" }), cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://management.azure.com{resourceId}?api-version={Uri.EscapeDataString(apiVersion)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        using var response = await ManagementClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private string ComputeHash(CentralAuditEventDto entry)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            entry.Sequence,
            entry.EventId,
            entry.TenantId,
            entry.ActorObjectId,
            entry.EventType,
            entry.Description,
            entry.RecordedAtUtc,
            entry.SourceTimestampUtc,
            metadata = entry.Metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value),
            entry.PreviousHash,
            entry.RetainUntilUtc
        }, JsonOptions);
        return Convert.ToHexString(HMACSHA256.HashData(_integrityKey, payload)).ToLowerInvariant();
    }

    private static void Validate(CentralAuditAppendRequest? request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.EventType) || request.EventType.Length > 120)
            throw new ArgumentException("Event type must contain 1 to 120 characters.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Description) || request.Description.Length > 4000)
            throw new ArgumentException("Description must contain 1 to 4000 characters.", nameof(request));
        if (request.Metadata is null || request.Metadata.Count > 50)
            throw new ArgumentException("Metadata must contain at most 50 entries.", nameof(request));
        if (request.Metadata.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 100 || pair.Value is null || pair.Value.Length > 2000))
            throw new ArgumentException("Metadata keys are limited to 100 characters and values to 2000 characters.", nameof(request));
    }

    private static string NormalizeStorageAccountResourceId(string value)
    {
        var normalized = value.Trim().TrimEnd('/');
        if (!normalized.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase)
            || !normalized.Contains("/resourceGroups/", StringComparison.OrdinalIgnoreCase)
            || !normalized.EndsWith("/providers/Microsoft.Storage/storageAccounts/" + normalized.Split('/').Last(), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A full Azure Storage account resource ID is required.", nameof(value));
        return normalized;
    }

    private static JsonElement ReadProperty(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) ? property : default;

    private static string? ReadString(JsonElement element, string name)
        => ReadProperty(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static bool ReadBoolean(JsonElement element, string name)
        => ReadProperty(element, name).ValueKind == JsonValueKind.True;

    private static int ReadInt32(JsonElement element, string name)
        => ReadProperty(element, name).TryGetInt32(out var value) ? value : 0;

    private static bool FixedEquals(string first, string second)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(first), Convert.FromHexString(second)); }
        catch (FormatException) { return false; }
    }

    private static bool MetadataEquals(IReadOnlyDictionary<string, string> first, IReadOnlyDictionary<string, string> second)
        => first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out var value) && value == pair.Value);

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_integrityKey);
        if (_ownsCredential && _credential is IDisposable disposableCredential) disposableCredential.Dispose();
    }

    private sealed record AuditLogScan(long LastSequence, long ByteLength, string LastHash,
        CentralAuditEventDto? Duplicate, IReadOnlyList<CentralAuditEventDto> VisibleEntries);
}
