using System.Globalization;
using CloudEntitle.Application.Audit;
using Microsoft.Data.Sqlite;

namespace CloudEntitle.Infrastructure.Security;

/// <summary>Durable per-organization hash-chained audit log. Updates and deletes are blocked by triggers.</summary>
public sealed class SqliteAuditLog : IAuditLog
{
    private const string Columns = "organization_id, seq, timestamp, actor, action, payload, previous_hash, hash";
    private readonly string _connectionString;

    public SqliteAuditLog(string connectionString)
    {
        _connectionString = connectionString;
        using var connection = Open();
        Exec(connection, """
            CREATE TABLE IF NOT EXISTS audit_log (
                organization_id TEXT NOT NULL,
                seq INTEGER NOT NULL,
                timestamp TEXT NOT NULL,
                actor TEXT NOT NULL,
                action TEXT NOT NULL,
                payload TEXT NOT NULL,
                previous_hash TEXT NOT NULL,
                hash TEXT NOT NULL,
                PRIMARY KEY (organization_id, seq)
            );
            CREATE TRIGGER IF NOT EXISTS audit_log_no_update BEFORE UPDATE ON audit_log
            BEGIN SELECT RAISE(ABORT, 'audit_log is append-only'); END;
            CREATE TRIGGER IF NOT EXISTS audit_log_no_delete BEFORE DELETE ON audit_log
            BEGIN SELECT RAISE(ABORT, 'audit_log is append-only'); END;
            """);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public async Task<AuditEntry> AppendAsync(Guid organizationId, string actor, string action, string payload, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        // BEGIN IMMEDIATE takes the write lock first so concurrent appends serialize instead of forking the chain.
        Exec(connection, "BEGIN IMMEDIATE");
        try
        {
            AuditEntry? last = null;
            await using (var select = connection.CreateCommand())
            {
                select.CommandText = $"SELECT {Columns} FROM audit_log WHERE organization_id = $org ORDER BY seq DESC LIMIT 1";
                select.Parameters.AddWithValue("$org", organizationId.ToString());
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    last = Map(reader);
                }
            }

            var entry = AuditChain.Next(last, organizationId, DateTimeOffset.UtcNow, actor, action, payload);
            await using (var insert = connection.CreateCommand())
            {
                insert.CommandText = $"INSERT INTO audit_log ({Columns}) VALUES ($org, $seq, $ts, $actor, $action, $payload, $prev, $hash)";
                insert.Parameters.AddWithValue("$org", organizationId.ToString());
                insert.Parameters.AddWithValue("$seq", entry.Sequence);
                insert.Parameters.AddWithValue("$ts", entry.Timestamp.UtcDateTime.ToString("O"));
                insert.Parameters.AddWithValue("$actor", actor);
                insert.Parameters.AddWithValue("$action", action);
                insert.Parameters.AddWithValue("$payload", payload);
                insert.Parameters.AddWithValue("$prev", entry.PreviousHash);
                insert.Parameters.AddWithValue("$hash", entry.Hash);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            Exec(connection, "COMMIT");
            return entry;
        }
        catch
        {
            Exec(connection, "ROLLBACK");
            throw;
        }
    }

    public async Task<IReadOnlyList<AuditEntry>> ReadAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM audit_log WHERE organization_id = $org ORDER BY seq";
        command.Parameters.AddWithValue("$org", organizationId.ToString());
        var entries = new List<AuditEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(Map(reader));
        }

        return entries;
    }

    public async Task<AuditVerification> VerifyAsync(Guid organizationId, CancellationToken cancellationToken = default)
        => AuditChain.Verify(await ReadAsync(organizationId, cancellationToken));

    private static AuditEntry Map(SqliteDataReader reader) => new(
        reader.GetInt64(1),
        Guid.Parse(reader.GetString(0)),
        DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetString(7));
}
