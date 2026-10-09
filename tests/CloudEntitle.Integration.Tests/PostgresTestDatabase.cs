using Npgsql;
using Xunit;

namespace CloudEntitle.Integration.Tests;

internal sealed class PostgresTestDatabase : IAsyncDisposable
{
    private readonly NpgsqlDataSource _adminDataSource;
    private readonly NpgsqlDataSource _testDataSource;

    private PostgresTestDatabase(NpgsqlDataSource adminDataSource, NpgsqlDataSource testDataSource, string schema)
    {
        _adminDataSource = adminDataSource;
        _testDataSource = testDataSource;
        Schema = schema;
    }

    public string Schema { get; }
    public string ConnectionString => _testDataSource.ConnectionString;

    public static async Task<PostgresTestDatabase> CreateAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("YASK_CIEM_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Set YASK_CIEM_TEST_POSTGRES to run PostgreSQL integration tests.");

        var adminDataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        var schema = $"yask_test_{Guid.NewGuid():N}";
        try
        {
            await using var connection = await adminDataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", connection);
            await command.ExecuteNonQueryAsync();
            var testConnectionString = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema }.ConnectionString;
            var testDataSource = new NpgsqlDataSourceBuilder(testConnectionString).Build();
            return new PostgresTestDatabase(adminDataSource, testDataSource, schema);
        }
        catch
        {
            await adminDataSource.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _testDataSource.DisposeAsync();
        await using (var connection = await _adminDataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE", connection))
            await command.ExecuteNonQueryAsync();
        await _adminDataSource.DisposeAsync();
    }
}

internal sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YASK_CIEM_TEST_POSTGRES")))
            Skip = "Set YASK_CIEM_TEST_POSTGRES to run PostgreSQL integration tests.";
    }
}
