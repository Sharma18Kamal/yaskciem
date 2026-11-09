using CloudEntitle.Integration.Contracts;
using CloudEntitle.LicenseServer.Services;

namespace CloudEntitle.Integration.Tests;

internal sealed class TestSubscription : ISubscriptionService
{
    public bool Active { get; set; }
    public Task<SubscriptionStatus> GetAsync(Guid tenant, bool registerTrial, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        return Task.FromResult(new SubscriptionStatus(tenant, Active ? "Trial" : "Expired", now,
            now.AddDays(-1), Active ? now.AddDays(29) : now, null, Active));
    }
}
