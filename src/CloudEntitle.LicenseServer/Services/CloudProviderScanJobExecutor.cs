using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Cloud.Aws;
using CloudEntitle.Cloud.Azure;
using CloudEntitle.Cloud.Gcp;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.Security.Services;

namespace CloudEntitle.LicenseServer.Services;

public sealed record ScanJobExecutionResult(string Outcome, Guid? ScanId, string? Error);

public interface IScanJobExecutor
{
    Task<ScanJobExecutionResult> ExecuteAsync(ScanJobLease lease, CancellationToken cancellationToken);
}

public sealed class CloudProviderScanJobExecutor(
    ICloudCommandRunner commandRunner,
    SharedWorkspaceStore store,
    ILogger<CloudProviderScanJobExecutor> logger) : IScanJobExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaximumRecords = 20_000;
    private const int MaximumPayloadBytes = 15 * 1024 * 1024;
    private const int MaximumRecordBytes = 262_144;

    public async Task<ScanJobExecutionResult> ExecuteAsync(ScanJobLease lease, CancellationToken cancellationToken)
    {
        var job = lease.Job;
        var scanId = Guid.NewGuid();
        var startedAt = DateTime.UtcNow;
        try
        {
            var provider = CreateProvider(job.Provider);
            var accounts = await provider.DiscoverAccountsAsync(cancellationToken);
            var account = accounts.SingleOrDefault(candidate =>
                GetStableAccountId(candidate).Equals(job.ExternalAccountId, StringComparison.OrdinalIgnoreCase));
            if (account is null)
                throw new InvalidOperationException($"The configured {job.Provider} credentials did not discover the scheduled account.");

            var coverages = new List<SharedScanCoverageDto>();
            var recordBuffer = new ScanRecordBuffer();
            var failures = new List<string>();
            var identities = await CollectAsync("Identities", () => provider.DiscoverIdentitiesAsync(account.Id, cancellationToken),
                "identity", job, coverages, recordBuffer, failures, cancellationToken);
            var roles = await CollectAsync("Roles", () => provider.DiscoverRolesAsync(account.Id, cancellationToken),
                "role", job, coverages, recordBuffer, failures, cancellationToken);
            var permissions = await CollectAsync("Permissions", () => provider.DiscoverPermissionsAsync(account.Id, cancellationToken),
                "permission", job, coverages, recordBuffer, failures, cancellationToken);

            IReadOnlyList<CloudResource> resources = Array.Empty<CloudResource>();
            if (provider is IResourceProvider resourceProvider)
                resources = await CollectAsync("Resources", () => resourceProvider.GetResourcesAsync(account.Id, cancellationToken),
                    "resource", job, coverages, recordBuffer, failures, cancellationToken);
            else
                await SaveCheckpointAsync(job, "Resources", "Unsupported", 0, "This provider connector does not expose resource inventory.", cancellationToken, coverages);

            IReadOnlyList<CloudEffectiveAccessAssignment> effectiveAccess = Array.Empty<CloudEffectiveAccessAssignment>();
            if (provider is ICloudEffectiveAccessProvider effectiveAccessProvider)
                effectiveAccess = await CollectAsync("Effective access", () => effectiveAccessProvider.DiscoverEffectiveAccessAsync(account.Id, cancellationToken),
                    "effective-access", job, coverages, recordBuffer, failures, cancellationToken);
            else
                await SaveCheckpointAsync(job, "Effective access", "Unsupported", 0, "This provider connector does not expose effective-access assignments.", cancellationToken, coverages);

            if (provider is ICloudProviderDiagnostics diagnostics)
            {
                foreach (var message in diagnostics.DrainProviderMessages())
                    failures.Add(SensitiveDataRedactor.RedactText(message));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var failedSources = coverages.Count(item => item.Status is "Unavailable" or "Denied");
            var successfulSources = coverages.Count(item => item.Status is "Collected" or "NoData" or "Partial");
            if (successfulSources == 0)
                return new ScanJobExecutionResult("Failed", null, JoinErrors(failures, "No supported scan source completed successfully."));

            var completedAt = DateTime.UtcNow;
            var scanAccounts = new[] { new SharedScanAccountDto(job.Provider, job.ExternalAccountId, job.DisplayName) };
            var request = new UploadSharedScanSummaryRequest(scanId, startedAt, completedAt, job.Provider.ToString(), scanAccounts,
                identities.Count, resources.Count, roles.Count, permissions.Count, effectiveAccess.Count, 0, 0, 0, coverages)
            {
                Records = recordBuffer.Records
            };
            var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions))).ToLowerInvariant();
            var summary = new SharedScanSummaryDto(scanId, job.OrganizationId, job.TenantId, Guid.Empty, startedAt, completedAt,
                job.Provider.ToString(), scanAccounts, identities.Count, resources.Count, roles.Count, permissions.Count,
                effectiveAccess.Count, 0, 0, 0, coverages);
            await store.SaveCiemScanAsync(summary, recordBuffer.Records, hash, cancellationToken, lease);
            var partial = failedSources > 0 || failures.Count > 0;
            return new ScanJobExecutionResult(partial ? "Partial" : "Succeeded", scanId,
                partial ? JoinErrors(failures, "One or more scan sources were incomplete.") : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Scheduled {Provider} scan failed for job {JobId}", job.Provider, job.JobId);
            return new ScanJobExecutionResult("Failed", null, SensitiveDataRedactor.RedactText(exception.Message));
        }
    }

    private ICloudProvider CreateProvider(CloudProvider provider)
        => provider switch
        {
            CloudProvider.Azure => new AzureProvider(commandRunner, useLiveDiscovery: true),
            CloudProvider.Aws => new AwsProvider(commandRunner, useLiveDiscovery: true),
            CloudProvider.Gcp => new GcpProvider(commandRunner, useLiveDiscovery: true),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported cloud provider.")
        };

    private async Task<IReadOnlyList<T>> CollectAsync<T>(
        string source,
        Func<Task<IReadOnlyList<T>>> collect,
        string kind,
        ScanJobDto job,
        ICollection<SharedScanCoverageDto> coverages,
        ScanRecordBuffer recordBuffer,
        ICollection<string> failures,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var collected = await collect();
            var truncated = false;
            foreach (var item in collected)
            {
                var payload = JsonSerializer.Serialize(item, JsonOptions);
                var payloadBytes = Encoding.UTF8.GetByteCount(payload);
                var recordId = GetRecordId(payload);
                if (payloadBytes > MaximumRecordBytes || recordBuffer.Records.Count >= MaximumRecords
                    || recordBuffer.PayloadBytes + payloadBytes > MaximumPayloadBytes
                    || !recordBuffer.Keys.Add($"{kind}\n{job.ExternalAccountId}\n{recordId}"))
                {
                    truncated = true;
                    continue;
                }
                recordBuffer.PayloadBytes += payloadBytes;
                recordBuffer.Records.Add(new SharedCiemRecordDto(job.Provider, kind, recordId, job.ExternalAccountId, payload));
            }
            if (truncated) failures.Add($"{source}: Some records were omitted because the scheduled scan persistence limit was reached.");
            var checkpointStatus = truncated ? "Partial" : collected.Count == 0 ? "NoData" : "Collected";
            await SaveCheckpointAsync(job, source, checkpointStatus, collected.Count,
                truncated ? "Some records were omitted to keep the account scan within the configured payload limits."
                    : collected.Count == 0 ? "Query completed with no records." : null, cancellationToken, coverages);
            return collected;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var message = SensitiveDataRedactor.RedactText(exception.Message);
            failures.Add($"{source}: {message}");
            await SaveCheckpointAsync(job, source, IsDenied(exception) ? "Denied" : "Unavailable", 0, message, cancellationToken, coverages);
            return Array.Empty<T>();
        }
    }

    private async Task SaveCheckpointAsync(
        ScanJobDto job,
        string source,
        string status,
        int records,
        string? details,
        CancellationToken cancellationToken,
        ICollection<SharedScanCoverageDto> coverages)
    {
        await store.SaveScanJobCheckpointAsync(job, source, status, records, details, cancellationToken);
        coverages.Add(new SharedScanCoverageDto(job.Provider.ToString(), source, status, records, details ?? $"{records} record(s) collected."));
    }

    private static string GetStableAccountId(CloudAccount account)
        => account.CloudProvider == CloudProviderType.Azure
            ? account.SubscriptionId ?? account.ExternalAccountId ?? account.ProjectId ?? account.Id.ToString("D")
            : account.ExternalAccountId ?? account.ProjectId ?? account.SubscriptionId ?? account.Id.ToString("D");

    private static string GetRecordId(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!property.Name.Equals("id", StringComparison.OrdinalIgnoreCase)) continue;
            var id = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.ToString();
            if (!string.IsNullOrWhiteSpace(id)) return id.Length <= 200 ? id : id[..200];
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static bool IsDenied(Exception exception)
        => exception.Message.Contains("denied", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("access is denied", StringComparison.OrdinalIgnoreCase);

    private static string JoinErrors(IEnumerable<string> errors, string fallback)
    {
        var summary = string.Join(" | ", errors.Where(error => !string.IsNullOrWhiteSpace(error)));
        if (string.IsNullOrWhiteSpace(summary)) return fallback;
        return summary.Length <= 4000 ? summary : summary[..4000];
    }

    private sealed class ScanRecordBuffer
    {
        public List<SharedCiemRecordDto> Records { get; } = new();
        public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);
        public int PayloadBytes { get; set; }
    }
}
