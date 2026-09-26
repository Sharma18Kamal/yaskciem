using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudEntitle.Infrastructure.Persistence;

public sealed class SqliteRecommendationStore : IRecommendationStore
{
    private readonly IDbContextFactory<CloudEntitleDbContext> _contextFactory;

    public SqliteRecommendationStore(IDbContextFactory<CloudEntitleDbContext> contextFactory) => _contextFactory = contextFactory;

    public async Task<IReadOnlyList<RecommendationModel>> GetAsync(string? search = null, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.NormalizedRecommendations.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(item => item.Title.Contains(term) || item.Description.Contains(term) || item.CurrentState.Contains(term) || item.SuggestedState.Contains(term));
        }

        return await query.OrderBy(item => item.Priority).ThenByDescending(item => item.CreatedAt).ToListAsync(cancellationToken);
    }
}
