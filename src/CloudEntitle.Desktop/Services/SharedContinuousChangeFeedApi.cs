using CloudEntitle.Integration.Contracts;

namespace CloudEntitle.Desktop.Services;

public interface ISharedContinuousChangeFeedApi
{
    Task<IReadOnlyList<ContinuousChangeFeedStatus>> GetContinuousChangeFeedsAsync(Guid organizationId, CancellationToken token = default);
    Task<ContinuousChangeFeedStatus> ConfigureContinuousChangeFeedAsync(Guid organizationId, string alias,
        ConfigureContinuousChangeFeedRequest request, CancellationToken token = default);
    Task PauseContinuousChangeFeedAsync(Guid organizationId, string alias, CancellationToken token = default);
    Task<IReadOnlyList<StoredContinuousChange>> GetContinuousCloudChangesAsync(Guid organizationId, string alias,
        CancellationToken token = default);
}

public sealed partial class SharedOrganizationApi : ISharedContinuousChangeFeedApi
{
    public async Task<IReadOnlyList<ContinuousChangeFeedStatus>> GetContinuousChangeFeedsAsync(Guid organizationId, CancellationToken token = default)
        => await RequireDataAsync(client.GetContinuousChangeFeedsAsync(organizationId, await TokenAsync(token), token));

    public async Task<ContinuousChangeFeedStatus> ConfigureContinuousChangeFeedAsync(Guid organizationId, string alias,
        ConfigureContinuousChangeFeedRequest request, CancellationToken token = default)
        => await RequireDataAsync(client.ConfigureContinuousChangeFeedAsync(organizationId, alias, request, await TokenAsync(token), token));

    public async Task PauseContinuousChangeFeedAsync(Guid organizationId, string alias, CancellationToken token = default)
        => await client.PauseContinuousChangeFeedAsync(organizationId, alias, await TokenAsync(token), token);

    public async Task<IReadOnlyList<StoredContinuousChange>> GetContinuousCloudChangesAsync(Guid organizationId, string alias,
        CancellationToken token = default)
        => await RequireDataAsync(client.GetContinuousCloudChangesAsync(organizationId, alias, await TokenAsync(token), token));
}
