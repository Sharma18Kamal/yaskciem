using CloudEntitle.Integration.Contracts;
using Npgsql;

namespace CloudEntitle.LicenseServer.Services;

public interface ISubscriptionService
{
    Task<SubscriptionStatus> GetAsync(Guid tenant, bool registerTrial, CancellationToken token);
}

public sealed class SubscriptionService(NpgsqlDataSource dataSource) : ISubscriptionService
{
    public async Task<SubscriptionStatus> GetAsync(Guid tenant, bool registerTrial, CancellationToken token)
    {
        if (tenant == Guid.Empty) throw new ArgumentException("A tenant is required.", nameof(tenant));
        await using var connection = await dataSource.OpenConnectionAsync(token);
        if (registerTrial)
        {
            await using var register = new NpgsqlCommand("""
                INSERT INTO CiemSubscriptions (TenantId, TrialStartedAtUtc, TrialExpiresAtUtc)
                VALUES (@tenant, statement_timestamp(), statement_timestamp() + interval '720 hours')
                ON CONFLICT (TenantId) DO NOTHING;
                """, connection);
            register.Parameters.AddWithValue("tenant", tenant);
            await register.ExecuteNonQueryAsync(token);
        }
        await using var query = new NpgsqlCommand("""
            SELECT statement_timestamp(), TrialStartedAtUtc, TrialExpiresAtUtc, LicensedUntilUtc
            FROM (SELECT @tenant::uuid AS TenantId) requested
            LEFT JOIN CiemSubscriptions USING (TenantId);
            """, connection);
        query.Parameters.AddWithValue("tenant", tenant);
        await using var reader = await query.ExecuteReaderAsync(token);
        await reader.ReadAsync(token);
        var now = new DateTimeOffset(reader.GetDateTime(0));
        DateTimeOffset? start = reader.IsDBNull(1) ? null : new(reader.GetDateTime(1));
        DateTimeOffset? end = reader.IsDBNull(2) ? null : new(reader.GetDateTime(2));
        DateTimeOffset? licensed = reader.IsDBNull(3) ? null : new(reader.GetDateTime(3));
        var status = licensed > now ? "Licensed" : end > now ? "Trial" : end is not null ? "Expired" : "NotRegistered";
        return new(tenant, status, now, start, end, licensed, status is "Licensed" or "Trial");
    }
}
