namespace CloudEntitle.Infrastructure.Persistence;

using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;
using Microsoft.EntityFrameworkCore;

public sealed class SqliteScanResultStore : IScanResultStore
{
    private readonly DbContextOptions<CloudEntitleDbContext> _options;

    public SqliteScanResultStore(DbContextOptions<CloudEntitleDbContext> options)
    {
        _options = options;
    }

    public async Task ReplaceFindingsAsync(
        IEnumerable<RiskFinding> findings,
        CancellationToken cancellationToken = default)
    {
        await using var context = new CloudEntitleDbContext(_options);
        var existingFindings = await context.RiskFindings.ToListAsync(cancellationToken);
        context.RiskFindings.RemoveRange(existingFindings);
        context.RiskFindings.AddRange(findings);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RiskFinding>> GetRecentFindingsAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return Array.Empty<RiskFinding>();
        }

        await using var context = new CloudEntitleDbContext(_options);
        return await context.RiskFindings
            .AsNoTracking()
            .OrderByDescending(finding => finding.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }
}
