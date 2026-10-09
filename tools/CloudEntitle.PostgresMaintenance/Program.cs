using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using CloudEntitle.Integration.Contracts;
using Microsoft.Data.Sqlite;
using Npgsql;

if (args.Length == 0)
	return PrintUsage();

try
{
	return args[0] switch
	{
		"migrate-sqlite" => await MigrateSqliteAsync(args.Skip(1).ToArray()),
		"reconcile-archive" => await ReconcileArchiveAsync(args.Skip(1).ToArray()),
		_ => PrintUsage()
	};
}
catch (Exception exception)
{
	Console.Error.WriteLine($"Maintenance command failed ({exception.GetType().Name}). Check protected logs for details.");
	return 1;
}

static int PrintUsage()
{
	Console.Error.WriteLine("Commands:");
	Console.Error.WriteLine("  migrate-sqlite <source-file> [--apply]");
	Console.Error.WriteLine("  reconcile-archive");
	return 2;
}

static async Task<int> MigrateSqliteAsync(string[] arguments)
{
	if (arguments.Length is < 1 or > 2 || arguments.Length == 2 && arguments[1] != "--apply")
		return PrintUsage();
	var sourcePath = Path.GetFullPath(arguments[0]);
	var apply = arguments.Length == 2;
	if (!File.Exists(sourcePath)) throw new FileNotFoundException("SQLite source database not found.", sourcePath);
	if (apply && Environment.GetEnvironmentVariable("YASK_CIEM_ALLOW_SQLITE_MIGRATION") != "YES")
		throw new InvalidOperationException("Set YASK_CIEM_ALLOW_SQLITE_MIGRATION=YES to authorize writes to PostgreSQL.");
	var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
	if (apply && string.IsNullOrWhiteSpace(connectionString))
		throw new InvalidOperationException("Configure ConnectionStrings__Postgres through the approved secret provider before applying migration.");

	var sqliteConnectionString = new SqliteConnectionStringBuilder { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly }.ToString();
	await using var source = new SqliteConnection(sqliteConnectionString);
	await source.OpenAsync();
	var tables = new[]
	{
		"SharedOrganizationDirectories",
		"SharedAccountBindings",
		"SharedAccessReviewCampaigns",
		"SharedScanSummaries"
	};
	var rowCounts = new Dictionary<string, long>(StringComparer.Ordinal);
	foreach (var table in tables)
		rowCounts[table] = await CountSqliteRowsAsync(source, table);

	Console.WriteLine($"Source: {sourcePath}");
	foreach (var (table, count) in rowCounts) Console.WriteLine($"{table}: {count} row(s)");
	if (!apply)
	{
		Console.WriteLine("Dry run only. Review the counts, then repeat with --apply and YASK_CIEM_ALLOW_SQLITE_MIGRATION=YES.");
		return 0;
	}

	await using var target = new NpgsqlDataSourceBuilder(connectionString!).Build();
	await using var destination = await target.OpenConnectionAsync();
	await using var transaction = await destination.BeginTransactionAsync();
	await MigrateOrganizationDirectoriesAsync(source, destination, transaction);
	await MigrateAccountBindingsAsync(source, destination, transaction);
	await MigrateAccessReviewsAsync(source, destination, transaction);
	await MigrateScanSummariesAsync(source, destination, transaction);
	await transaction.CommitAsync();
	Console.WriteLine("SQLite workspace migration committed. Conflicts abort the transaction; no target rows are overwritten.");
	return 0;
}

static async Task<long> CountSqliteRowsAsync(SqliteConnection connection, string table)
{
	await using var exists = connection.CreateCommand();
	exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $table";
	exists.Parameters.AddWithValue("$table", table);
	if (await exists.ExecuteScalarAsync() is null) return 0;

	await using var count = connection.CreateCommand();
	count.CommandText = $"SELECT count(*) FROM {table}";
	return Convert.ToInt64(await count.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
}

static async Task MigrateOrganizationDirectoriesAsync(SqliteConnection source, NpgsqlConnection target, NpgsqlTransaction transaction)
{
	if (await CountSqliteRowsAsync(source, "SharedOrganizationDirectories") == 0) return;
	await using var read = source.CreateCommand();
	read.CommandText = "SELECT TenantId, Revision, PayloadJson FROM SharedOrganizationDirectories ORDER BY TenantId";
	await using var reader = await read.ExecuteReaderAsync();
	while (await reader.ReadAsync())
	{
		await using var insert = new NpgsqlCommand("""
			INSERT INTO SharedOrganizationDirectories (TenantId, Revision, PayloadJson)
			VALUES (@tenant, @revision, @payload)
			""", target, transaction);
		insert.Parameters.AddWithValue("tenant", Guid.Parse(reader.GetString(0)));
		insert.Parameters.AddWithValue("revision", reader.GetInt64(1));
		insert.Parameters.AddWithValue("payload", reader.GetString(2));
		await insert.ExecuteNonQueryAsync();
	}
}

static async Task MigrateAccountBindingsAsync(SqliteConnection source, NpgsqlConnection target, NpgsqlTransaction transaction)
{
	if (await CountSqliteRowsAsync(source, "SharedAccountBindings") == 0) return;
	await using var read = source.CreateCommand();
	read.CommandText = "SELECT Provider, ExternalAccountId, TenantId, OrganizationId FROM SharedAccountBindings ORDER BY Provider, ExternalAccountId";
	await using var reader = await read.ExecuteReaderAsync();
	while (await reader.ReadAsync())
	{
		await using var insert = new NpgsqlCommand("""
			INSERT INTO SharedAccountBindings (Provider, ExternalAccountId, TenantId, OrganizationId)
			VALUES (@provider, @external, @tenant, @organization)
			""", target, transaction);
		insert.Parameters.AddWithValue("provider", reader.GetString(0).Trim().ToUpperInvariant());
		insert.Parameters.AddWithValue("external", reader.GetString(1).Trim().ToUpperInvariant());
		insert.Parameters.AddWithValue("tenant", Guid.Parse(reader.GetString(2)));
		insert.Parameters.AddWithValue("organization", Guid.Parse(reader.GetString(3)));
		await insert.ExecuteNonQueryAsync();
	}
}

static async Task MigrateAccessReviewsAsync(SqliteConnection source, NpgsqlConnection target, NpgsqlTransaction transaction)
{
	if (await CountSqliteRowsAsync(source, "SharedAccessReviewCampaigns") == 0) return;
	await using var read = source.CreateCommand();
	read.CommandText = "SELECT TenantId, OrganizationId, CampaignId, CreatedAtUtc, PayloadJson FROM SharedAccessReviewCampaigns ORDER BY TenantId, OrganizationId, CampaignId";
	await using var reader = await read.ExecuteReaderAsync();
	while (await reader.ReadAsync())
	{
		await using var insert = new NpgsqlCommand("""
			INSERT INTO SharedAccessReviewCampaigns (TenantId, OrganizationId, CampaignId, CreatedAtUtc, PayloadJson)
			VALUES (@tenant, @organization, @campaign, @created, @payload)
			""", target, transaction);
		insert.Parameters.AddWithValue("tenant", Guid.Parse(reader.GetString(0)));
		insert.Parameters.AddWithValue("organization", Guid.Parse(reader.GetString(1)));
		insert.Parameters.AddWithValue("campaign", Guid.Parse(reader.GetString(2)));
		insert.Parameters.AddWithValue("created", ParseTimestamp(reader.GetString(3)));
		insert.Parameters.AddWithValue("payload", reader.GetString(4));
		await insert.ExecuteNonQueryAsync();
	}
}

static async Task MigrateScanSummariesAsync(SqliteConnection source, NpgsqlConnection target, NpgsqlTransaction transaction)
{
	if (await CountSqliteRowsAsync(source, "SharedScanSummaries") == 0) return;
	await using var read = source.CreateCommand();
	read.CommandText = "SELECT TenantId, OrganizationId, ScanId, CompletedAtUtc, PayloadJson FROM SharedScanSummaries ORDER BY TenantId, OrganizationId, ScanId";
	await using var reader = await read.ExecuteReaderAsync();
	while (await reader.ReadAsync())
	{
		await using var insert = new NpgsqlCommand("""
			INSERT INTO SharedScanSummaries (TenantId, OrganizationId, ScanId, CompletedAtUtc, PayloadJson)
			VALUES (@tenant, @organization, @scan, @completed, @payload)
			""", target, transaction);
		insert.Parameters.AddWithValue("tenant", Guid.Parse(reader.GetString(0)));
		insert.Parameters.AddWithValue("organization", Guid.Parse(reader.GetString(1)));
		insert.Parameters.AddWithValue("scan", Guid.Parse(reader.GetString(2)));
		insert.Parameters.AddWithValue("completed", ParseTimestamp(reader.GetString(3)));
		insert.Parameters.AddWithValue("payload", reader.GetString(4));
		await insert.ExecuteNonQueryAsync();
	}
}

static DateTime ParseTimestamp(string value)
	=> DateTime.SpecifyKind(DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal), DateTimeKind.Utc);

static async Task<int> ReconcileArchiveAsync(string[] arguments)
{
	if (arguments.Length != 0) return PrintUsage();
	var database = Environment.GetEnvironmentVariable("ConnectionStrings__AuditArchive");
	var endpoint = Environment.GetEnvironmentVariable("AuditArchive__Endpoint");
	var region = Environment.GetEnvironmentVariable("AuditArchive__Region");
	var bucket = Environment.GetEnvironmentVariable("AuditArchive__Bucket");
	var accessKey = Environment.GetEnvironmentVariable("AuditArchive__AccessKey");
	var secretKey = Environment.GetEnvironmentVariable("AuditArchive__SecretKey");
	if (new[] { database, endpoint, region, bucket, accessKey, secretKey }.Any(string.IsNullOrWhiteSpace))
		throw new InvalidOperationException("Configure the archive PostgreSQL and S3 endpoint values through the approved secret provider.");
	using var integrityKeys = ReconciliationKeyRing.FromEnvironment();

	await using var dataSource = new NpgsqlDataSourceBuilder(database!).Build();
	await using var connection = await dataSource.OpenConnectionAsync();
	await using var command = new NpgsqlCommand("""
		SELECT tenant_id, first_sequence, last_sequence, last_hash, sha256, object_key, retain_until_utc
		FROM ciem_audit_archive_segments
		ORDER BY tenant_id, first_sequence
		""", connection);
	await using var reader = await command.ExecuteReaderAsync();
	using var client = new AmazonS3Client(new BasicAWSCredentials(accessKey!, secretKey!), new AmazonS3Config
	{
		ServiceURL = endpoint,
		ForcePathStyle = true,
		AuthenticationRegion = region,
		UseHttp = false
	});

	var checkedCount = 0;
	var failedCount = 0;
	while (await reader.ReadAsync())
	{
		var tenantId = reader.GetGuid(0);
		var firstSequence = reader.GetInt64(1);
		var lastSequence = reader.GetInt64(2);
		var lastHash = reader.GetString(3);
		var expectedDigest = reader.GetString(4);
		var objectKey = reader.GetString(5);
		var retainUntil = reader.GetDateTime(6);
		try
		{
			using var response = await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = objectKey });
			await using var objectBytes = new MemoryStream();
			await response.ResponseStream.CopyToAsync(objectBytes);
			var payloadBytes = objectBytes.ToArray();
			var actualDigest = Convert.ToHexString(SHA256.HashData(payloadBytes)).ToLowerInvariant();
			var metadataDigest = response.Metadata.Keys
				.Where(key => string.Equals(key, "x-amz-meta-sha256", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(key, "sha256", StringComparison.OrdinalIgnoreCase))
				.Select(key => response.Metadata[key]).FirstOrDefault();
			if (!string.Equals(actualDigest, expectedDigest, StringComparison.OrdinalIgnoreCase)
				|| !string.Equals(metadataDigest, expectedDigest, StringComparison.OrdinalIgnoreCase)
				|| response.ObjectLockMode != ObjectLockMode.Compliance
				|| response.ObjectLockRetainUntilDate < retainUntil)
				throw new InvalidDataException("Object digest or Object Lock retention differs from the PostgreSQL ledger.");

			using var payload = JsonDocument.Parse(payloadBytes);
			ValidateArchiveManifest(payload.RootElement, tenantId, firstSequence, lastSequence, lastHash, integrityKeys);
			checkedCount++;
		}
		catch (Exception exception)
		{
			failedCount++;
			Console.Error.WriteLine($"Mismatch: tenant={tenantId:N}, sequence={firstSequence}-{lastSequence}, key={Path.GetFileName(objectKey)}, type={exception.GetType().Name}");
		}
	}

	Console.WriteLine($"Archive reconciliation: verified={checkedCount}; failed={failedCount}.");
	return failedCount == 0 ? 0 : 1;
}

static void ValidateArchiveManifest(JsonElement root, Guid tenantId, long firstSequence, long lastSequence,
	string lastHash, ReconciliationKeyRing integrityKeys)
{
	if (root.GetProperty("tenantId").GetGuid() != tenantId
		|| root.GetProperty("firstSequence").GetInt64() != firstSequence
		|| root.GetProperty("lastSequence").GetInt64() != lastSequence
		|| !string.Equals(root.GetProperty("lastHash").GetString(), lastHash, StringComparison.Ordinal))
		throw new InvalidDataException("Object manifest does not match the PostgreSQL ledger.");

	var events = root.GetProperty("events");
	if (events.ValueKind != JsonValueKind.Array || events.GetArrayLength() != lastSequence - firstSequence + 1)
		throw new InvalidDataException("Object event count does not match its sealed sequence range.");

	var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
	var expected = firstSequence;
	var previousHash = root.GetProperty("previousHash").GetString();
	foreach (var entry in events.EnumerateArray())
	{
		var auditEvent = entry.Deserialize<CentralAuditEventDto>(jsonOptions)
			?? throw new InvalidDataException("Object contains an invalid audit event.");
		if (auditEvent.TenantId != tenantId || auditEvent.Sequence != expected
			|| auditEvent.EventId == Guid.Empty
			|| !string.Equals(auditEvent.PreviousHash, previousHash, StringComparison.Ordinal)
			|| !FixedHashEquals(auditEvent.Hash, ComputeAuditEventHash(auditEvent, integrityKeys.GetKey(auditEvent.IntegrityKeyId))))
			throw new InvalidDataException("Object contains an event with an unexpected tenant or sequence.");
		previousHash = auditEvent.Hash;
		expected++;
	}

	if (!string.Equals(previousHash, lastHash, StringComparison.Ordinal))
		throw new InvalidDataException("The archive manifest's terminal HMAC differs from the ledger.");
}

static string ComputeAuditEventHash(CentralAuditEventDto entry, ReadOnlySpan<byte> key)
{
	var metadata = entry.Metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value);
	var payload = entry.IntegrityKeyId == "legacy"
		? JsonSerializer.SerializeToUtf8Bytes(new
		{
			entry.Sequence,
			entry.EventId,
			entry.TenantId,
			entry.ActorObjectId,
			entry.EventType,
			entry.Description,
			entry.RecordedAtUtc,
			entry.SourceTimestampUtc,
			metadata,
			entry.PreviousHash,
			entry.RetainUntilUtc
		}, new JsonSerializerOptions(JsonSerializerDefaults.Web))
		: JsonSerializer.SerializeToUtf8Bytes(new
		{
			entry.Sequence,
			entry.EventId,
			entry.TenantId,
			entry.ActorObjectId,
			entry.EventType,
			entry.Description,
			entry.RecordedAtUtc,
			entry.SourceTimestampUtc,
			metadata,
			entry.PreviousHash,
			entry.RetainUntilUtc,
			entry.IntegrityKeyId
		}, new JsonSerializerOptions(JsonSerializerDefaults.Web));
	return Convert.ToHexString(HMACSHA256.HashData(key, payload)).ToLowerInvariant();
}

static bool FixedHashEquals(string first, string second)
{
	try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(first), Convert.FromHexString(second)); }
	catch (FormatException) { return false; }
}

sealed class ReconciliationKeyRing : IDisposable
{
	private readonly Dictionary<string, byte[]> _keys;

	private ReconciliationKeyRing(Dictionary<string, byte[]> keys) => _keys = keys;

	public static ReconciliationKeyRing FromEnvironment()
	{
		var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
		var legacyKey = Decode(Environment.GetEnvironmentVariable("AuditRetention__IntegrityKey"));
		if (legacyKey is not null) keys.Add("legacy", legacyKey);
		foreach (System.Collections.DictionaryEntry setting in Environment.GetEnvironmentVariables())
		{
			if (setting.Key is not string name || !name.StartsWith("AuditRetention__IntegrityKeys__", StringComparison.OrdinalIgnoreCase)) continue;
			var keyId = name["AuditRetention__IntegrityKeys__".Length..];
			var key = Decode(setting.Value as string);
			if (key is not null && !keys.TryAdd(keyId, key))
			{
				CryptographicOperations.ZeroMemory(key);
				foreach (var value in keys.Values) CryptographicOperations.ZeroMemory(value);
				throw new InvalidOperationException($"Duplicate audit integrity key ID '{keyId}'.");
			}
		}
		if (keys.Count == 0) throw new InvalidOperationException("Configure the retained audit integrity key ring before reconciliation.");
		return new ReconciliationKeyRing(keys);
	}

	public ReadOnlySpan<byte> GetKey(string keyId)
		=> _keys.TryGetValue(keyId, out var key)
			? key
			: throw new InvalidDataException($"Archive event references unavailable integrity key ID '{keyId}'.");

	private static byte[]? Decode(string? value)
	{
		if (string.IsNullOrWhiteSpace(value)) return null;
		try
		{
			var key = Convert.FromBase64String(value);
			if (key.Length >= 32) return key;
			CryptographicOperations.ZeroMemory(key);
			return null;
		}
		catch (FormatException) { return null; }
	}

	public void Dispose()
	{
		foreach (var key in _keys.Values) CryptographicOperations.ZeroMemory(key);
		_keys.Clear();
	}
}
