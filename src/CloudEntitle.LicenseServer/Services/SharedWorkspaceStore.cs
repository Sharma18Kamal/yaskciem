using System.Data;
using System.Text.Json;
using CloudEntitle.Domain;
using CloudEntitle.IdentityGovernance.Models;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.OrganizationManagement.Abstractions;
using CloudEntitle.OrganizationManagement.Models;
using Npgsql;

namespace CloudEntitle.LicenseServer.Services;

public sealed class SharedWorkspaceStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _ownsDataSource;

    public SharedWorkspaceStore(string connectionString)
        : this(new NpgsqlDataSourceBuilder(connectionString).Build(), ownsDataSource: true)
    {
    }

    public SharedWorkspaceStore(NpgsqlDataSource dataSource)
        : this(dataSource, ownsDataSource: false)
    {
    }

    private SharedWorkspaceStore(NpgsqlDataSource dataSource, bool ownsDataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
        _ownsDataSource = ownsDataSource;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS SharedOrganizationDirectories (
                TenantId uuid NOT NULL PRIMARY KEY,
                Revision bigint NOT NULL,
                PayloadJson text NOT NULL
            );
            CREATE TABLE IF NOT EXISTS SharedAccountBindings (
                Provider text NOT NULL,
                ExternalAccountId text NOT NULL,
                TenantId uuid NOT NULL,
                OrganizationId uuid NOT NULL,
                PRIMARY KEY (Provider, ExternalAccountId)
            );
            CREATE TABLE IF NOT EXISTS SharedAccessReviewCampaigns (
                TenantId uuid NOT NULL,
                OrganizationId uuid NOT NULL,
                CampaignId uuid NOT NULL,
                CreatedAtUtc timestamptz NOT NULL,
                PayloadJson text NOT NULL,
                PRIMARY KEY (TenantId, OrganizationId, CampaignId)
            );
            CREATE INDEX IF NOT EXISTS IX_SharedAccessReviewCampaigns_Org_Created
                ON SharedAccessReviewCampaigns (TenantId, OrganizationId, CreatedAtUtc);
            CREATE TABLE IF NOT EXISTS SharedScanSummaries (
                TenantId uuid NOT NULL,
                OrganizationId uuid NOT NULL,
                ScanId uuid NOT NULL,
                CompletedAtUtc timestamptz NOT NULL,
                PayloadJson text NOT NULL,
                PRIMARY KEY (TenantId, OrganizationId, ScanId)
            );
            CREATE INDEX IF NOT EXISTS IX_SharedScanSummaries_Org_Completed
                ON SharedScanSummaries (TenantId, OrganizationId, CompletedAtUtc);
            """, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("""
                     SELECT to_regclass('sharedorganizationdirectories') IS NOT NULL
                         AND to_regclass('sharedaccountbindings') IS NOT NULL
                         AND to_regclass('sharedaccessreviewcampaigns') IS NOT NULL
                         AND to_regclass('sharedscansummaries') IS NOT NULL
                """, connection);
            return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    public IOrganizationStore CreateOrganizationStore(Guid tenantId)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("An Entra tenant ID is required.", nameof(tenantId));
        return new TenantOrganizationStore(_dataSource, tenantId);
    }

    public async Task<bool> IsAccountBoundAsync(CloudProvider provider, string externalAccountId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM SharedAccountBindings WHERE Provider=@provider AND ExternalAccountId=@external)", connection);
        command.Parameters.AddWithValue("provider", provider.ToString().ToUpperInvariant());
        command.Parameters.AddWithValue("external", externalAccountId.Trim().ToUpperInvariant());
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    public async Task<IReadOnlyList<AccessReviewCampaign>> GetAccessReviewsAsync(Guid tenantId, Guid organizationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT PayloadJson FROM SharedAccessReviewCampaigns WHERE TenantId=@tenant AND OrganizationId=@organization ORDER BY CreatedAtUtc DESC", connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("organization", organizationId);
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
        await using var command = new NpgsqlCommand("""
            INSERT INTO SharedAccessReviewCampaigns (TenantId, OrganizationId, CampaignId, CreatedAtUtc, PayloadJson)
            VALUES (@tenant, @organization, @campaign, @created, @payload)
            ON CONFLICT (TenantId, OrganizationId, CampaignId) DO UPDATE SET
                CreatedAtUtc=excluded.CreatedAtUtc, PayloadJson=excluded.PayloadJson;
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("organization", organizationId);
        command.Parameters.AddWithValue("campaign", campaign.Id);
        command.Parameters.AddWithValue("created", campaign.CreatedAt.ToUniversalTime());
        command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(campaign, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveScanSummaryAsync(SharedScanSummaryDto summary, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO SharedScanSummaries (TenantId, OrganizationId, ScanId, CompletedAtUtc, PayloadJson)
            VALUES (@tenant, @organization, @scan, @completed, @payload)
            ON CONFLICT (TenantId, OrganizationId, ScanId) DO NOTHING;
            """, connection);
        command.Parameters.AddWithValue("tenant", summary.TenantId);
        command.Parameters.AddWithValue("organization", summary.OrganizationId);
        command.Parameters.AddWithValue("scan", summary.ScanId);
        command.Parameters.AddWithValue("completed", summary.CompletedAtUtc.ToUniversalTime());
        command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(summary, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SharedScanSummaryDto>> GetScanSummariesAsync(Guid tenantId, Guid organizationId, int limit = 30, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT PayloadJson FROM SharedScanSummaries WHERE TenantId=@tenant AND OrganizationId=@organization ORDER BY CompletedAtUtc DESC LIMIT @limit", connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("organization", organizationId);
        command.Parameters.AddWithValue("limit", limit);
        var summaries = new List<SharedScanSummaryDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var summary = JsonSerializer.Deserialize<SharedScanSummaryDto>(reader.GetString(0), JsonOptions);
            if (summary is not null) summaries.Add(summary);
        }
        return summaries;
    }

    private Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
        => _dataSource.OpenConnectionAsync(cancellationToken).AsTask();

    private sealed class TenantOrganizationStore(NpgsqlDataSource dataSource, Guid tenantId) : IOrganizationStore
    {
        private static readonly JsonSerializerOptions StoreJsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
        private long _revision;
        private bool _loaded;

        public OrganizationDirectoryState Load()
        {
            using var connection = dataSource.OpenConnection();
            using var command = new NpgsqlCommand("SELECT Revision, PayloadJson FROM SharedOrganizationDirectories WHERE TenantId=@tenant", connection);
            command.Parameters.AddWithValue("tenant", tenantId);
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
            using var connection = dataSource.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
            using (var command = new NpgsqlCommand(_revision == 0
                ? "INSERT INTO SharedOrganizationDirectories (TenantId, Revision, PayloadJson) VALUES (@tenant, 1, @payload) ON CONFLICT (TenantId) DO NOTHING;"
                : "UPDATE SharedOrganizationDirectories SET Revision=Revision+1, PayloadJson=@payload WHERE TenantId=@tenant AND Revision=@revision;", connection, transaction))
            {
                command.Parameters.AddWithValue("tenant", tenantId);
                command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(state, StoreJsonOptions));
                if (_revision != 0) command.Parameters.AddWithValue("revision", _revision);
                if (command.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("The shared organization directory changed concurrently. Reload it and retry the operation.");
            }

            using (var clearBindings = new NpgsqlCommand("DELETE FROM SharedAccountBindings WHERE TenantId=@tenant", connection, transaction))
            {
                clearBindings.Parameters.AddWithValue("tenant", tenantId);
                clearBindings.ExecuteNonQuery();
            }

            foreach (var binding in state.Bindings)
            {
                using var addBinding = new NpgsqlCommand("INSERT INTO SharedAccountBindings (Provider, ExternalAccountId, TenantId, OrganizationId) VALUES (@provider, @external, @tenant, @organization)", connection, transaction);
                addBinding.Parameters.AddWithValue("provider", binding.Provider.ToString().ToUpperInvariant());
                addBinding.Parameters.AddWithValue("external", binding.ExternalTenantId.Trim().ToUpperInvariant());
                addBinding.Parameters.AddWithValue("tenant", tenantId);
                addBinding.Parameters.AddWithValue("organization", binding.OrganizationId);
                try { addBinding.ExecuteNonQuery(); }
                catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
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

    public void Dispose()
    {
        if (_ownsDataSource) _dataSource.Dispose();
    }
}
