using CloudEntitle.Integration.Contracts;
using CloudEntitle.OrganizationManagement.Models;
using CloudEntitle.OrganizationManagement.Services;

namespace CloudEntitle.LicenseServer.Services;

public interface IContinuousChangeFeedService
{
    Task<IReadOnlyList<ContinuousChangeFeedStatus>> GetStatusAsync(Guid tenant, Guid organization,
        OrganizationIdentity identity, CancellationToken token);
    Task<ContinuousChangeFeedStatus> ConfigureAsync(Guid tenant, Guid organization, OrganizationIdentity identity,
        string alias, ConfigureContinuousChangeFeedRequest request, CancellationToken token);
    Task PauseAsync(Guid tenant, Guid organization, OrganizationIdentity identity, string alias, CancellationToken token);
    Task<IReadOnlyList<StoredContinuousChange>> GetChangesAsync(Guid tenant, Guid organization,
        OrganizationIdentity identity, string alias, int limit, CancellationToken token);
}

public sealed class ContinuousChangeFeedService(SharedWorkspaceStore store, IConfiguration configuration) : IContinuousChangeFeedService
{
    public async Task<IReadOnlyList<ContinuousChangeFeedStatus>> GetStatusAsync(Guid tenant, Guid organization,
        OrganizationIdentity identity, CancellationToken token)
    {
        var access = Authorize(tenant, organization, identity, false);
        var bindings = access.GetTenantBindings(organization, identity);
        var saved = await store.GetContinuousFeedStatesAsync(tenant, organization, token);
        var states = saved.Where(s => bindings.Any(b => b.Provider == s.State.Provider
            && b.ExternalTenantId.Equals(s.State.AccountId, StringComparison.OrdinalIgnoreCase)))
            .Select(item =>
            {
                var source = ContinuousChangeSourceConfiguration.Resolve(configuration, tenant, organization, item.State.Alias);
                if (source is null) return item.State with { Health = "NotConfigured", FailureCode = "server_source_disabled" };
                if (source.ConfigurationSha256 != item.Fingerprint)
                    return item.State with { Health = "SourceChanged", FailureCode = "source_reset_required" };
                if (item.State.Connected && item.State.LastSuccessAtUtc is { } last
                    && DateTimeOffset.UtcNow - last > TimeSpan.FromSeconds(Math.Max(180, source.PollSeconds * 3))
                    && item.State.Health is "Partial" or "Polling")
                    return item.State with { Health = "Stale" };
                return item.State;
            }).ToList();
        foreach (var section in configuration.GetSection($"ContinuousChangeFeeds:Organizations:{tenant:D}:{organization:D}:Sources").GetChildren())
        {
            var source = ContinuousChangeSourceConfiguration.Resolve(configuration, tenant, organization, section.Key);
            if (source is null || states.Any(s => s.Alias == source.Alias)
                || !bindings.Any(b => b.Provider == source.Provider && b.ExternalTenantId.Equals(source.AccountId, StringComparison.OrdinalIgnoreCase)))
                continue;
            states.Add(new(source.Alias, source.Provider, source.AccountId, source.SourceId, Guid.Empty, false, false, null,
                "NotConnected", 0, null, null, null, ContinuousChangeFeedValidation.UnknownCoverage()));
        }
        return states.OrderBy(s => s.Alias).Take(100).ToArray();
    }

    public async Task<ContinuousChangeFeedStatus> ConfigureAsync(Guid tenant, Guid organization, OrganizationIdentity identity,
        string alias, ConfigureContinuousChangeFeedRequest request, CancellationToken token)
    {
        var access = Authorize(tenant, organization, identity, true);
        var source = ContinuousChangeSourceConfiguration.Resolve(configuration, tenant, organization, alias)
            ?? throw new ArgumentException("Server bridge alias is not enabled, connected or safely configured.");
        if (!access.GetTenantBindings(organization, identity).Any(b => b.Provider == source.Provider
            && b.ExternalTenantId.Equals(source.AccountId, StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException();
        return await store.ConfigureContinuousFeedAsync(tenant, organization, source, request, token);
    }

    public async Task PauseAsync(Guid tenant, Guid organization, OrganizationIdentity identity, string alias, CancellationToken token)
    {
        Authorize(tenant, organization, identity, true);
        var states = await GetStatusAsync(tenant, organization, identity, token);
        if (!states.Any(s => s.Alias == alias && s.SourceEpoch != Guid.Empty)) throw new KeyNotFoundException();
        await store.PauseContinuousFeedAsync(tenant, organization, alias, token);
    }

    public async Task<IReadOnlyList<StoredContinuousChange>> GetChangesAsync(Guid tenant, Guid organization,
        OrganizationIdentity identity, string alias, int limit, CancellationToken token)
    {
        var states = await GetStatusAsync(tenant, organization, identity, token);
        if (!states.Any(s => s.Alias == alias)) throw new KeyNotFoundException();
        return await store.GetContinuousChangesAsync(tenant, organization, alias, limit, token);
    }

    private OrganizationManagementService Authorize(Guid tenant, Guid organization, OrganizationIdentity identity, bool write)
    {
        if (!Guid.TryParse(identity.TenantId, out var identityTenant) || tenant != identityTenant) throw new UnauthorizedAccessException();
        var access = new OrganizationManagementService(store.CreateOrganizationStore(tenant));
        if (!access.Authorize(organization, identity, OrganizationAction.ViewInventory).Allowed
            || !access.Authorize(organization, identity, OrganizationAction.ViewFindings).Allowed
            || write && !access.Authorize(organization, identity, OrganizationAction.ManageConnections).Allowed
            || write && !access.Authorize(organization, identity, OrganizationAction.ManagePolicies).Allowed)
            throw new UnauthorizedAccessException();
        return access;
    }
}
