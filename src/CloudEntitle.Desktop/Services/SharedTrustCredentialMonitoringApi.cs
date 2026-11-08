using CloudEntitle.Integration.Clients;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.Domain;

namespace CloudEntitle.Desktop.Services;

public interface ISharedTrustCredentialMonitoringApi
{
    Task<TrustCredentialSnapshot> UploadTrustCredentialSnapshotAsync(Guid organizationId, TrustCredentialSnapshot snapshot,
        CancellationToken token = default);
    Task<IReadOnlyList<TrustCredentialSnapshotSummary>> GetTrustCredentialHistoryAsync(Guid organizationId, string accountId,
        CancellationToken token = default);
    Task<IReadOnlyList<TrustCredentialSnapshotSummary>> GetTrustCredentialHistoryAsync(Guid organizationId, CloudProvider provider,
        string accountId, CancellationToken token = default);
    Task<TrustCredentialSnapshot> GetTrustCredentialSnapshotAsync(Guid organizationId, string accountId, Guid snapshotId,
        CancellationToken token = default);
    Task<TrustCredentialSnapshot> GetTrustCredentialSnapshotAsync(Guid organizationId, CloudProvider provider, string accountId,
        Guid snapshotId, CancellationToken token = default);
    Task<TrustCredentialMonitoringReport> AnalyzeTrustCredentialMonitoringAsync(Guid organizationId,
        TrustCredentialMonitoringRequest request, CancellationToken token = default);
}

public sealed partial class SharedOrganizationApi : ISharedTrustCredentialMonitoringApi
{
    public async Task<TrustCredentialSnapshot> UploadTrustCredentialSnapshotAsync(Guid organizationId,
        TrustCredentialSnapshot snapshot, CancellationToken token = default)
        => await RequireDataAsync(client.UploadTrustCredentialSnapshotAsync(organizationId, snapshot, await TokenAsync(token), token));

    public async Task<IReadOnlyList<TrustCredentialSnapshotSummary>> GetTrustCredentialHistoryAsync(Guid organizationId,
        string accountId, CancellationToken token = default)
        => await GetTrustCredentialHistoryAsync(organizationId, CloudProvider.Azure, accountId, token);

    public async Task<IReadOnlyList<TrustCredentialSnapshotSummary>> GetTrustCredentialHistoryAsync(Guid organizationId,
        CloudProvider provider, string accountId, CancellationToken token = default)
        => await RequireDataAsync(client.GetTrustCredentialHistoryAsync(organizationId, provider, accountId, await TokenAsync(token), token));

    public async Task<TrustCredentialSnapshot> GetTrustCredentialSnapshotAsync(Guid organizationId, string accountId,
        Guid snapshotId, CancellationToken token = default)
        => await GetTrustCredentialSnapshotAsync(organizationId, CloudProvider.Azure, accountId, snapshotId, token);

    public async Task<TrustCredentialSnapshot> GetTrustCredentialSnapshotAsync(Guid organizationId, CloudProvider provider,
        string accountId, Guid snapshotId, CancellationToken token = default)
        => await RequireDataAsync(client.GetTrustCredentialSnapshotAsync(organizationId, provider, accountId, snapshotId,
            await TokenAsync(token), token));

    public async Task<TrustCredentialMonitoringReport> AnalyzeTrustCredentialMonitoringAsync(Guid organizationId,
        TrustCredentialMonitoringRequest request, CancellationToken token = default)
        => await RequireDataAsync(client.AnalyzeTrustCredentialMonitoringAsync(organizationId, request, await TokenAsync(token), token));
}
