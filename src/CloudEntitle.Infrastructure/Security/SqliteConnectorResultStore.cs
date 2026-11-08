using System.Text.Json;
using CloudEntitle.Application.Connectors;
using Microsoft.Data.Sqlite;

namespace CloudEntitle.Infrastructure.Security;

public sealed class SqliteConnectorResultStore : IConnectorResultStore
{
    private readonly string _connectionString;

    public SqliteConnectorResultStore(string connectionString)
    {
        _connectionString = connectionString;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS connector_results (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                organization_id TEXT NOT NULL,
                provider TEXT NOT NULL,
                external_id TEXT NOT NULL,
                collected_at TEXT NOT NULL,
                result_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_connector_results_scope
                ON connector_results (organization_id, provider, external_id, collected_at);
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public async Task SaveAsync(ConnectorResult result, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO connector_results (organization_id, provider, external_id, collected_at, result_json) VALUES ($org, $provider, $ext, $at, $json)";
        command.Parameters.AddWithValue("$org", result.Scope.OrganizationId.ToString());
        command.Parameters.AddWithValue("$provider", result.Scope.Provider);
        command.Parameters.AddWithValue("$ext", result.Scope.ExternalId);
        command.Parameters.AddWithValue("$at", result.CollectedAt.UtcDateTime.ToString("O"));
        // The external secret must never be persisted.
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(result with { Scope = result.Scope with { ExternalSecret = null } }));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ConnectorResult?> GetLatestAsync(Guid organizationId, string provider, string externalId, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT result_json FROM connector_results WHERE organization_id = $org AND provider = $provider AND external_id = $ext ORDER BY collected_at DESC, id DESC LIMIT 1";
        command.Parameters.AddWithValue("$org", organizationId.ToString());
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$ext", externalId);
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        return json is null ? null : JsonSerializer.Deserialize<ConnectorResult>(json);
    }
}
