using System.Data;
using System.Text.Json;
using CloudEntitle.Domain;
using CloudEntitle.IdentityGovernance.Models;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.OrganizationManagement.Abstractions;
using CloudEntitle.OrganizationManagement.Models;
using Microsoft.Data.Sqlite;

namespace CloudEntitle.LicenseServer.Services;

public sealed class SharedWorkspaceStore(string connectionString)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS SharedOrganizationDirectories (
                TenantId TEXT NOT NULL PRIMARY KEY,
                Revision INTEGER NOT NULL,
                PayloadJson TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS SharedAccountBindings (
                Provider TEXT NOT NULL COLLATE NOCASE,
                ExternalAccountId TEXT NOT NULL COLLATE NOCASE,
                TenantId TEXT NOT NULL,
                OrganizationId TEXT NOT NULL,
                PRIMARY KEY (Provider, ExternalAccountId)
            );
            CREATE TABLE IF NOT EXISTS SharedAccessReviewCampaigns (
                TenantId TEXT NOT NULL,
                OrganizationId TEXT NOT NULL,
                CampaignId TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                PayloadJson TEXT NOT NULL,
                PRIMARY KEY (TenantId, OrganizationId, CampaignId)
            );
            CREATE INDEX IF NOT EXISTS IX_SharedAccessReviewCampaigns_Org_Created
                ON SharedAccessReviewCampaigns (TenantId, OrganizationId, CreatedAtUtc);
            CREATE TABLE IF NOT EXISTS SharedScanSummaries (
                TenantId TEXT NOT NULL,
                OrganizationId TEXT NOT NULL,
                ScanId TEXT NOT NULL,
                CompletedAtUtc TEXT NOT NULL,
                PayloadJson TEXT NOT NULL,
                PRIMARY KEY (TenantId, OrganizationId, ScanId)
            );
            CREATE INDEX IF NOT EXISTS IX_SharedScanSummaries_Org_Completed
                ON SharedScanSummaries (TenantId, OrganizationId, CompletedAtUtc);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public IOrganizationStore CreateOrganizationStore(Guid tenantId)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("An Entra tenant ID is required.", nameof(tenantId));
        return new TenantOrganizationStore(connectionString, tenantId);
    }

    public async Task<bool> IsAccountBoundAsync(CloudProvider provider, string externalAccountId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM SharedAccountBindings WHERE Provider=$provider AND ExternalAccountId=$external)";
        command.Parameters.AddWithValue("$provider", provider.ToString());
        command.Parameters.AddWithValue("$external", externalAccountId.Trim());
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    public async Task<IReadOnlyList<AccessReviewCampaign>> GetAccessReviewsAsync(Guid tenantId, Guid organizationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT PayloadJson FROM SharedAccessReviewCampaigns WHERE TenantId=$tenant AND OrganizationId=$organization ORDER BY CreatedAtUtc DESC";
        command.Parameters.AddWithValue("$tenant", tenantId.ToString("D"));
        command.Parameters.AddWithValue("$organization", organizationId.ToString("D"));
        var campaigns = new List<AccessReviewCampaign>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var campaign = JsonSerializer.Deserialize<AccessReviewCampaign>(reader.GetString(0), JsonOptions);
            if (campaign is not null) campaigns.Add(campaign);
        }
        return campaigns;
    }

    public async Task SaveAccessReviewAsync(Guid tenantId, Guid organizationId, AccessReviewCampaign campaign, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SharedAccessReviewCampaigns (TenantId, OrganizationId, CampaignId, CreatedAtUtc, PayloadJson)
            VALUES ($tenant, $organization, $campaign, $created, $payload)
            ON CONFLICT (TenantId, OrganizationId, CampaignId) DO UPDATE SET
                CreatedAtUtc=excluded.CreatedAtUtc, PayloadJson=excluded.PayloadJson;
            """;
        command.Parameters.AddWithValue("$tenant", tenantId.ToString("D"));
        command.Parameters.AddWithValue("$organization", organizationId.ToString("D"));
        command.Parameters.AddWithValue("$campaign", campaign.Id.ToString("D"));
        command.Parameters.AddWithValue("$created", campaign.CreatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(campaign, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveScanSummaryAsync(SharedScanSummaryDto summary, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SharedScanSummaries (TenantId, OrganizationId, ScanId, CompletedAtUtc, PayloadJson)
            VALUES ($tenant, $organization, $scan, $completed, $payload)
            ON CONFLICT (TenantId, OrganizationId, ScanId) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$tenant", summary.TenantId.ToString("D"));
        command.Parameters.AddWithValue("$organization", summary.OrganizationId.ToString("D"));
        command.Parameters.AddWithValue("$scan", summary.ScanId.ToString("D"));
        command.Parameters.AddWithValue("$completed", summary.CompletedAtUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(summary, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SharedScanSummaryDto>> GetScanSummariesAsync(Guid tenantId, Guid organizationId, int limit = 30, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT PayloadJson FROM SharedScanSummaries WHERE TenantId=$tenant AND OrganizationId=$organization ORDER BY CompletedAtUtc DESC LIMIT $limit";
        command.Parameters.AddWithValue("$tenant", tenantId.ToString("D"));
        command.Parameters.AddWithValue("$organization", organizationId.ToString("D"));
        command.Parameters.AddWithValue("$limit", limit);
        var summaries = new List<SharedScanSummaryDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var summary = JsonSerializer.Deserialize<SharedScanSummaryDto>(reader.GetString(0), JsonOptions);
            if (summary is not null) summaries.Add(summary);
        }
        return summaries;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private sealed class TenantOrganizationStore(string connectionString, Guid tenantId) : IOrganizationStore
    {
        private static readonly JsonSerializerOptions StoreJsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
        private long _revision;
        private bool _loaded;

        public OrganizationDirectoryState Load()
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Revision, PayloadJson FROM SharedOrganizationDirectories WHERE TenantId=$tenant";
            command.Parameters.AddWithValue("$tenant", tenantId.ToString("D"));
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                _revision = 0;
                _loaded = true;
                return EmptyState();
            }
            _revision = reader.GetInt64(0);
            _loaded = true;
            return JsonSerializer.Deserialize<OrganizationDirectoryState>(reader.GetString(1), StoreJsonOptions) ?? EmptyState();
        }

        public void Save(OrganizationDirectoryState state)
        {
            ArgumentNullException.ThrowIfNull(state);
            if (!_loaded) throw new InvalidOperationException("Load the tenant directory before saving it.");
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = _revision == 0
                    ? "INSERT INTO SharedOrganizationDirectories (TenantId, Revision, PayloadJson) VALUES ($tenant, 1, $payload) ON CONFLICT (TenantId) DO NOTHING;"
                    : "UPDATE SharedOrganizationDirectories SET Revision=Revision+1, PayloadJson=$payload WHERE TenantId=$tenant AND Revision=$revision;";
                command.Parameters.AddWithValue("$tenant", tenantId.ToString("D"));
                command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(state, StoreJsonOptions));
                if (_revision != 0) command.Parameters.AddWithValue("$revision", _revision);
                if (command.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("The shared organization directory changed concurrently. Reload it and retry the operation.");
            }

            using (var clearBindings = connection.CreateCommand())
            {
                clearBindings.Transaction = transaction;
                clearBindings.CommandText = "DELETE FROM SharedAccountBindings WHERE TenantId=$tenant";
                clearBindings.Parameters.AddWithValue("$tenant", tenantId.ToString("D"));
                clearBindings.ExecuteNonQuery();
            }

            foreach (var binding in state.Bindings)
            {
                using var addBinding = connection.CreateCommand();
                addBinding.Transaction = transaction;
                addBinding.CommandText = "INSERT INTO SharedAccountBindings (Provider, ExternalAccountId, TenantId, OrganizationId) VALUES ($provider, $external, $tenant, $organization)";
                addBinding.Parameters.AddWithValue("$provider", binding.Provider.ToString());
                addBinding.Parameters.AddWithValue("$external", binding.ExternalTenantId.Trim().ToUpperInvariant());
                addBinding.Parameters.AddWithValue("$tenant", tenantId.ToString("D"));
                addBinding.Parameters.AddWithValue("$organization", binding.OrganizationId.ToString("D"));
                try { addBinding.ExecuteNonQuery(); }
                catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
                {
                    throw new InvalidOperationException("This provider account is already bound to an organization.", exception);
                }
            }

            transaction.Commit();
            _revision++;
        }

        private static OrganizationDirectoryState EmptyState()
            => new(Array.Empty<OrganizationProfile>(), Array.Empty<OrganizationMember>(), Array.Empty<TenantBinding>());
    }
}
