using CloudEntitle.Domain.Entities;

namespace CloudEntitle.Application.Interfaces;

public interface IEffectiveAccessStore
{
    Task ReplaceForScanAsync(Guid cloudAccountId, IEnumerable<EffectiveAccess> records, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EffectiveAccess>> GetForPrincipalAsync(Guid principalId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EffectiveAccess>> GetForResourceAsync(Guid resourceId, CancellationToken cancellationToken = default);
}
