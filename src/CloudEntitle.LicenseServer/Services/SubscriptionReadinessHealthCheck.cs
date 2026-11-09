using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace CloudEntitle.LicenseServer.Services;

public sealed class SubscriptionReadinessHealthCheck(ISubscriptionService subscriptions) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await subscriptions.GetAsync(new Guid("00000000-0000-0000-0000-000000000001"), false, cancellationToken);
            return HealthCheckResult.Healthy("Subscription storage is readable.");
        }
        catch (NpgsqlException)
        {
            return HealthCheckResult.Unhealthy("Subscription storage is unavailable; apply all numbered migrations.");
        }
    }
}
