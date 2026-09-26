using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudEntitle.Infrastructure.Persistence;

public sealed class SqliteEffectiveAccessStore : IEffectiveAccessStore
{
    private readonly IDbContextFactory<CloudEntitleDbContext> _contextFactory;

    public SqliteEffectiveAccessStore(IDbContextFactory<CloudEntitleDbContext> contextFactory) => _contextFactory = contextFactory;

    public async Task ReplaceForScanAsync(Guid cloudAccountId, IEnumerable<EffectiveAccess> records, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await context.EffectiveAccesses.Where(record => record.CloudAccountId == cloudAccountId).ToListAsync(cancellationToken);
        context.EffectiveAccesses.RemoveRange(existing);
        context.EffectiveAccesses.AddRange(records.Where(record => record.CloudAccountId == cloudAccountId));
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EffectiveAccess>> GetForPrincipalAsync(Guid principalId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.EffectiveAccesses.AsNoTracking().Where(record => record.PrincipalId == principalId).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EffectiveAccess>> GetForResourceAsync(Guid resourceId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.EffectiveAccesses.AsNoTracking().Where(record => record.ResourceId == resourceId).ToListAsync(cancellationToken);
    }
}
