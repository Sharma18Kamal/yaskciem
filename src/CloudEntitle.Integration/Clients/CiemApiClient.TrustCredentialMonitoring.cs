using System.Net.Http;
using CloudEntitle.Integration.Contracts;

namespace CloudEntitle.Integration.Clients;

public sealed partial class CiemApiClient
{
    public Task<ApiEnvelope<TrustCredentialSnapshot>?> UploadTrustCredentialSnapshotAsync(Guid organizationId,
        TrustCredentialSnapshot snapshot, string accessToken, CancellationToken token = default)
        => SendForEnvelopeAsync<TrustCredentialSnapshot>(HttpMethod.Post,
            $"api/v1/organizations/{organizationId:D}/trust-credential-evidence", accessToken, snapshot, token);

    public Task<ApiEnvelope<IReadOnlyList<TrustCredentialSnapshotSummary>>?> GetTrustCredentialHistoryAsync(Guid organizationId,
        string accountId, string accessToken, CancellationToken token = default)
        => GetTrustCredentialHistoryAsync(organizationId, CloudEntitle.Domain.CloudProvider.Azure, accountId, accessToken, token);

    public Task<ApiEnvelope<IReadOnlyList<TrustCredentialSnapshotSummary>>?> GetTrustCredentialHistoryAsync(Guid organizationId,
        CloudEntitle.Domain.CloudProvider provider, string accountId, string accessToken, CancellationToken token = default)
        => SendForEnvelopeAsync<IReadOnlyList<TrustCredentialSnapshotSummary>>(HttpMethod.Get,
            $"api/v1/organizations/{organizationId:D}/trust-credential-evidence?provider={Uri.EscapeDataString(provider.ToString())}&accountId={Uri.EscapeDataString(accountId)}",
            accessToken, cancellationToken: token);

    public Task<ApiEnvelope<TrustCredentialSnapshot>?> GetTrustCredentialSnapshotAsync(Guid organizationId,
        string accountId, Guid snapshotId, string accessToken, CancellationToken token = default)
        => GetTrustCredentialSnapshotAsync(organizationId, CloudEntitle.Domain.CloudProvider.Azure, accountId, snapshotId, accessToken, token);

    public Task<ApiEnvelope<TrustCredentialSnapshot>?> GetTrustCredentialSnapshotAsync(Guid organizationId,
        CloudEntitle.Domain.CloudProvider provider, string accountId, Guid snapshotId, string accessToken, CancellationToken token = default)
        => SendForEnvelopeAsync<TrustCredentialSnapshot>(HttpMethod.Get,
            $"api/v1/organizations/{organizationId:D}/trust-credential-evidence/{snapshotId:D}?provider={Uri.EscapeDataString(provider.ToString())}&accountId={Uri.EscapeDataString(accountId)}",
            accessToken, cancellationToken: token);

    public Task<ApiEnvelope<TrustCredentialMonitoringReport>?> AnalyzeTrustCredentialMonitoringAsync(Guid organizationId,
        TrustCredentialMonitoringRequest request, string accessToken, CancellationToken token = default)
        => SendForEnvelopeAsync<TrustCredentialMonitoringReport>(HttpMethod.Post,
            $"api/v1/organizations/{organizationId:D}/trust-credential-monitoring/analyze", accessToken, request, token);
}
