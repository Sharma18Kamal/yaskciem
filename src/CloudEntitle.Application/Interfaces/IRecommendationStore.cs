using CloudEntitle.Domain.Entities;

namespace CloudEntitle.Application.Interfaces;

public interface IRecommendationStore
{
    Task<IReadOnlyList<RecommendationModel>> GetAsync(string? search = null, CancellationToken cancellationToken = default);
}
