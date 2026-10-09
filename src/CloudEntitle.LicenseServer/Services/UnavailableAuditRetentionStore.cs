using CloudEntitle.Integration.Contracts;

namespace CloudEntitle.LicenseServer.Services;

public sealed class UnavailableAuditRetentionStore(string details) : IAuditRetentionStore
{
    public Task<AuditStoreReadiness> CheckReadinessAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new AuditStoreReadiness(false, false, details));

    public Task<CentralAuditEventDto> AppendAsync(Guid tenantId, Guid actorObjectId, CentralAuditAppendRequest request,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException($"Central audit storage is unavailable: {details}");

    public Task<IReadOnlyList<CentralAuditEventDto>> ReadAsync(Guid tenantId, long afterSequence, int limit,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException($"Central audit storage is unavailable: {details}");
}
