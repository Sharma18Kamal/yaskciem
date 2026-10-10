using System.IO;
using System.IO.Compression;
using CloudEntitle.Desktop.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CloudEntitle.Desktop.Tests;

public sealed class LocalDataBackupServiceTests
{
    [Fact]
    public async Task BackupAndRestore_ShouldRoundTripWorkspaceDataWithoutExportingSettingsOrCredentials()
    {
        var root = Path.Combine(Path.GetTempPath(), "YaskCiemBackupTests", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(root, "CloudEntitle", "Data", "cloudentitle.db");
        var scanHistoryPath = Path.Combine(root, "YASK-CIEM", "scan-history", "scans.jsonl");
        var ticketSettingsPath = Path.Combine(root, "YASK-CIEM", "ticketing-connections.json");
        var backupPath = Path.Combine(root, "backup.zip");
        try
        {
            await ExecuteSqlAsync(databasePath, "CREATE TABLE sample (value TEXT NOT NULL); INSERT INTO sample VALUES ('before');");
            Directory.CreateDirectory(Path.GetDirectoryName(scanHistoryPath)!);
            await File.WriteAllTextAsync(scanHistoryPath, "historical scan");
            await File.WriteAllTextAsync(ticketSettingsPath, "secret-bearing settings");
            var service = new LocalDataBackupService(root, databasePath);

            var backup = await service.CreateBackupAsync(backupPath);
            Assert.True(backup.FilesIncluded >= 2);
            using (var archive = ZipFile.OpenRead(backupPath))
                Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("ticketing-connections", StringComparison.OrdinalIgnoreCase));

            await ExecuteSqlAsync(databasePath, "UPDATE sample SET value = 'after';");
            await File.WriteAllTextAsync(scanHistoryPath, "changed scan");
            var restore = await service.RestoreBackupAsync(backupPath);

            Assert.True(restore.RestartRequired);
            Assert.Equal("historical scan", await File.ReadAllTextAsync(scanHistoryPath));
            Assert.Equal("before", await ReadSqlValueAsync(databasePath));
            Assert.Equal("secret-bearing settings", await File.ReadAllTextAsync(ticketSettingsPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task ExecuteSqlAsync(string databasePath, string sql)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ReadSqlValueAsync(string databasePath)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM sample;";
        return await command.ExecuteScalarAsync() as string;
    }
}
