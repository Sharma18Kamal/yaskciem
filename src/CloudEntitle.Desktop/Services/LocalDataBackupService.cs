using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CloudEntitle.Desktop.Services;

public sealed record LocalDataBackupResult(int FilesIncluded, DateTime CompletedAtUtc, bool RestartRequired = false);

public interface ILocalDataBackupService
{
    Task<LocalDataBackupResult> CreateBackupAsync(string destinationPath, CancellationToken cancellationToken = default);
    Task<LocalDataBackupResult> RestoreBackupAsync(string sourcePath, CancellationToken cancellationToken = default);
}

public sealed class LocalDataBackupService : ILocalDataBackupService
{
    private const int ManifestVersion = 1;
    private const long MaximumArchivePayloadBytes = 2L * 1024 * 1024 * 1024;
    private static readonly string[] ManagedPaths =
    {
        "CloudEntitle/Data/cloudentitle.db",
        "YASK-CIEM/scan-history/scans.jsonl",
        "YASK-CIEM/governance/access-reviews.json",
        "YASK-CIEM/remediation/plans.json",
        "YASK-CIEM/compliance/assessments.json",
        "YASK-CIEM/organizations/directory.json",
        "YASK-CIEM/ticketing/requests.json",
        "YASK-CIEM/monitoring-schedule.json",
        "YASK-CIEM/feedback/submissions.jsonl"
    };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly string _localDataRoot;
    private readonly string _databasePath;

    public LocalDataBackupService(string? localDataRoot = null, string? databasePath = null)
    {
        _localDataRoot = localDataRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _databasePath = databasePath ?? Path.Combine(_localDataRoot, "CloudEntitle", "Data", "cloudentitle.db");
    }

    public async Task<LocalDataBackupResult> CreateBackupAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var stagingDirectory = CreateStagingDirectory();
        try
        {
            var entries = new List<BackupManifestEntry>();
            var stagedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var relativePath in ManagedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = GetManagedPath(relativePath);
                if (!File.Exists(sourcePath))
                {
                    entries.Add(new BackupManifestEntry(relativePath, false, 0, string.Empty));
                    continue;
                }

                var stagedPath = Path.Combine(stagingDirectory, "payload", relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
                if (relativePath.Equals("CloudEntitle/Data/cloudentitle.db", StringComparison.OrdinalIgnoreCase))
                    await CreateConsistentDatabaseSnapshotAsync(sourcePath, stagedPath, cancellationToken);
                else
                    File.Copy(sourcePath, stagedPath, overwrite: true);

                var info = new FileInfo(stagedPath);
                var hash = await ComputeSha256Async(stagedPath, cancellationToken);
                entries.Add(new BackupManifestEntry(relativePath, true, info.Length, hash));
                stagedPaths.Add(relativePath, stagedPath);
            }

            var manifest = new BackupManifest(ManifestVersion, DateTime.UtcNow, entries);
            var output = Path.GetFullPath(destinationPath);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await using var outputStream = new FileStream(output, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true);
            using var archive = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var entry in entries.Where(entry => entry.Included))
            {
                cancellationToken.ThrowIfCancellationRequested();
                archive.CreateEntryFromFile(stagedPaths[entry.Path], $"payload/{entry.Path}", CompressionLevel.Optimal);
            }
            var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
            await using (var manifestStream = manifestEntry.Open())
                await JsonSerializer.SerializeAsync(manifestStream, manifest, JsonOptions, cancellationToken);

            return new LocalDataBackupResult(entries.Count(entry => entry.Included), DateTime.UtcNow);
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
        }
    }

    public async Task<LocalDataBackupResult> RestoreBackupAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var stagingDirectory = CreateStagingDirectory();
        var originalFiles = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var databaseRollbackPath = Path.Combine(stagingDirectory, "database-before-restore.db");
        var databaseWasPresent = File.Exists(_databasePath);
        var databaseChanged = false;
        try
        {
            var archiveEntries = await ReadAndValidateArchiveAsync(sourcePath, stagingDirectory, cancellationToken);
            if (databaseWasPresent)
                await CreateConsistentDatabaseSnapshotAsync(_databasePath, databaseRollbackPath, cancellationToken);

            foreach (var relativePath in ManagedPaths.Where(path => !path.Equals("CloudEntitle/Data/cloudentitle.db", StringComparison.OrdinalIgnoreCase)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var targetPath = GetManagedPath(relativePath);
                if (File.Exists(targetPath))
                {
                    var rollbackPath = Path.Combine(stagingDirectory, "rollback", relativePath.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(rollbackPath)!);
                    File.Copy(targetPath, rollbackPath, overwrite: true);
                    originalFiles[relativePath] = rollbackPath;
                }
                else
                {
                    originalFiles[relativePath] = null;
                }

                var manifestEntry = archiveEntries.Manifest.Entries.Single(entry => entry.Path == relativePath);
                if (manifestEntry.Included)
                    ReplaceFile(archiveEntries.StagedPaths[relativePath], targetPath);
                else if (File.Exists(targetPath))
                    File.Delete(targetPath);
            }

            var databaseEntry = archiveEntries.Manifest.Entries.Single(entry => entry.Path == "CloudEntitle/Data/cloudentitle.db");
            if (databaseEntry.Included)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
                databaseChanged = true;
                await RestoreDatabaseAsync(archiveEntries.StagedPaths[databaseEntry.Path], _databasePath, cancellationToken);
            }
            else if (File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
                databaseChanged = true;
            }

            return new LocalDataBackupResult(archiveEntries.Manifest.Entries.Count(entry => entry.Included), DateTime.UtcNow, RestartRequired: true);
        }
        catch
        {
            foreach (var original in originalFiles)
            {
                var targetPath = GetManagedPath(original.Key);
                if (original.Value is null)
                {
                    if (File.Exists(targetPath)) File.Delete(targetPath);
                }
                else
                {
                    ReplaceFile(original.Value, targetPath);
                }
            }

            if (databaseChanged)
            {
                if (databaseWasPresent)
                    await RestoreDatabaseAsync(databaseRollbackPath, _databasePath, CancellationToken.None);
                else if (File.Exists(_databasePath))
                    File.Delete(_databasePath);
            }
            throw;
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
        }
    }

    private async Task<(BackupManifest Manifest, Dictionary<string, string> StagedPaths)> ReadAndValidateArchiveAsync(
        string sourcePath, string stagingDirectory, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(sourcePath);
        var archiveFiles = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToArray();
        if (archiveFiles.Sum(entry => entry.Length) > MaximumArchivePayloadBytes)
            throw new InvalidDataException("The backup archive exceeds the supported size limit.");

        var manifestEntry = archiveFiles.SingleOrDefault(entry => entry.FullName == "manifest.json")
            ?? throw new InvalidDataException("The backup manifest is missing.");
        if (manifestEntry.Length > 1024 * 1024)
            throw new InvalidDataException("The backup manifest is too large.");
        BackupManifest manifest;
        await using (var stream = manifestEntry.Open())
            manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(stream, JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("The backup manifest is invalid.");
        if (manifest.SchemaVersion != ManifestVersion || manifest.Entries is null)
            throw new InvalidDataException("The backup version is not supported.");

        var allowed = ManagedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entriesByPath = new Dictionary<string, BackupManifestEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Entries)
        {
            if (!allowed.Contains(entry.Path) || !entriesByPath.TryAdd(entry.Path, entry))
                throw new InvalidDataException("The backup contains an unsupported or duplicate data path.");
            if (entry.Included && (entry.Length < 0 || string.IsNullOrWhiteSpace(entry.Sha256)))
                throw new InvalidDataException("The backup manifest contains invalid file metadata.");
            if (!entry.Included && (entry.Length != 0 || !string.IsNullOrEmpty(entry.Sha256)))
                throw new InvalidDataException("The backup manifest contains inconsistent file metadata.");
        }
        if (!allowed.SetEquals(entriesByPath.Keys))
            throw new InvalidDataException("The backup manifest does not describe the complete local-data set.");

        var expectedArchiveFiles = entriesByPath.Values.Where(entry => entry.Included)
            .Select(entry => $"payload/{entry.Path}").Append("manifest.json")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!expectedArchiveFiles.SetEquals(archiveFiles.Select(entry => entry.FullName)))
            throw new InvalidDataException("The backup contains an unexpected or missing archive entry.");

        var stagedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var manifestFile in entriesByPath.Values.Where(entry => entry.Included))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var archiveEntry = archive.GetEntry($"payload/{manifestFile.Path}")!;
            var destination = Path.Combine(stagingDirectory, "payload", manifestFile.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using (var input = archiveEntry.Open())
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await input.CopyToAsync(output, cancellationToken);

            var info = new FileInfo(destination);
            if (info.Length != manifestFile.Length
                || !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(await ComputeSha256Async(destination, cancellationToken)),
                    Convert.FromHexString(manifestFile.Sha256)))
                throw new InvalidDataException($"Backup integrity validation failed for {manifestFile.Path}.");
            stagedPaths.Add(manifestFile.Path, destination);
        }

        if (stagedPaths.TryGetValue("CloudEntitle/Data/cloudentitle.db", out var databasePath))
            await ValidateDatabaseAsync(databasePath, cancellationToken);

        return (manifest, stagedPaths);
    }

    private async Task CreateConsistentDatabaseSnapshotAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destinationPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        await source.OpenAsync(cancellationToken);
        await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);
    }

    private static async Task RestoreDatabaseAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destinationPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        await source.OpenAsync(cancellationToken);
        await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);
    }

    private static async Task ValidateDatabaseAsync(string databasePath, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (!string.Equals(result as string, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The backup database failed SQLite integrity validation.");
    }

    private string GetManagedPath(string relativePath)
    {
        if (!ManagedPaths.Contains(relativePath, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("The requested path is not part of the local backup allowlist.");
        return Path.GetFullPath(Path.Combine(_localDataRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string CreateStagingDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "YASK-CIEM-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static void ReplaceFile(string stagedPath, string targetPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var temporaryPath = targetPath + ".restore.tmp";
        File.Copy(stagedPath, temporaryPath, overwrite: true);
        File.Move(temporaryPath, targetPath, overwrite: true);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record BackupManifest(int SchemaVersion, DateTime CreatedAtUtc, IReadOnlyList<BackupManifestEntry> Entries);
    private sealed record BackupManifestEntry(string Path, bool Included, long Length, string Sha256);
}
