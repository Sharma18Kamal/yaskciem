using Microsoft.EntityFrameworkCore;
using Serilog;

namespace CloudEntitle.Infrastructure.Persistence;

public sealed class DbInitializer
{
    private readonly IDatabaseConfiguration _configuration;
    private readonly DbContextOptions<CloudEntitleDbContext> _options;

    public DbInitializer(IDatabaseConfiguration configuration)
    {
        _configuration = configuration;
        _options = new DbContextOptionsBuilder<CloudEntitleDbContext>()
            .UseSqlite(configuration.ConnectionString)
            .Options;
    }

    public DbContextOptions<CloudEntitleDbContext> Options => _options;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(_configuration.DatabaseDirectory);
            await using var context = new CloudEntitleDbContext(_options);
            await context.Database.MigrateAsync(cancellationToken);
            Log.Information("CloudEntitle database initialized at {DatabasePath}", _configuration.DatabasePath);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Unable to initialize CloudEntitle database at {DatabasePath}", _configuration.DatabasePath);
            throw new InvalidOperationException("Unable to initialize the local CIEM database.", exception);
        }
    }
}
