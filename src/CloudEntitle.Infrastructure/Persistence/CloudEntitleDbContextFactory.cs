using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CloudEntitle.Infrastructure.Persistence;

public sealed class CloudEntitleDbContextFactory : IDesignTimeDbContextFactory<CloudEntitleDbContext>
{
    public CloudEntitleDbContext CreateDbContext(string[] args)
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "cloudentitle-ef-design.db");
        var options = new DbContextOptionsBuilder<CloudEntitleDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;

        return new CloudEntitleDbContext(options);
    }
}
