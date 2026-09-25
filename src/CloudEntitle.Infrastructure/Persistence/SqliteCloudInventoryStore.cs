namespace CloudEntitle.Infrastructure.Persistence;

using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;
using Microsoft.EntityFrameworkCore;

public sealed class SqliteCloudInventoryStore : ICloudInventoryStore
{
    private readonly DbContextOptions<CloudEntitleDbContext> _options;

    public SqliteCloudInventoryStore(DbContextOptions<CloudEntitleDbContext> options)
    {
        _options = options;
    }

    public async Task ReplaceInventoryAsync(
        IEnumerable<CloudAccount> accounts,
        IEnumerable<CloudPrincipal> principals,
        IEnumerable<CloudRole> roles,
        IEnumerable<CloudPermission> permissions,
        CancellationToken cancellationToken = default)
    {
        await using var context = new CloudEntitleDbContext(_options);
        context.CloudAccounts.RemoveRange(await context.CloudAccounts.ToListAsync(cancellationToken));
        context.CloudPrincipals.RemoveRange(await context.CloudPrincipals.ToListAsync(cancellationToken));
        context.CloudRoles.RemoveRange(await context.CloudRoles.ToListAsync(cancellationToken));
        context.CloudPermissions.RemoveRange(await context.CloudPermissions.ToListAsync(cancellationToken));
        context.CloudAccounts.AddRange(accounts);
        context.CloudPrincipals.AddRange(principals);
        context.CloudRoles.AddRange(roles);
        context.CloudPermissions.AddRange(permissions);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<InventorySummary> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        await using var context = new CloudEntitleDbContext(_options);
        return new InventorySummary
        {
            AccountCount = await context.CloudAccounts.CountAsync(cancellationToken),
            PrincipalCount = await context.CloudPrincipals.CountAsync(cancellationToken),
            RoleCount = await context.CloudRoles.CountAsync(cancellationToken),
            PermissionCount = await context.CloudPermissions.CountAsync(cancellationToken)
        };
    }
}
