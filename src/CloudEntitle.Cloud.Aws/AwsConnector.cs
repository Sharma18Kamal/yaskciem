using Amazon.IdentityManagement;
using Amazon.SecurityToken;
using CloudEntitle.Application.Connectors;

namespace CloudEntitle.Cloud.Aws;

public sealed class AwsConnector : IConnector
{
    private readonly IAwsAccountApiFactory _apiFactory;

    public AwsConnector(IAwsAccountApiFactory apiFactory) => _apiFactory = apiFactory;

    public string Provider => "aws";

    public async Task<ConnectorResult> DiscoverAsync(ConnectorScope scope, CancellationToken cancellationToken = default)
    {
        var coverage = new List<CoverageEntry>();
        var identities = new List<ConnectorIdentity>();

        ConnectorResult Result() => new(scope, DateTimeOffset.UtcNow, identities, coverage);

        if (string.IsNullOrWhiteSpace(scope.RoleArn) || string.IsNullOrWhiteSpace(scope.ExternalSecret))
        {
            coverage.Add(new CoverageEntry("credentials", CoverageStatus.Unavailable, "RoleArn and external ID are required."));
            return Result();
        }

        IAwsAccountApi api;
        try
        {
            api = await _apiFactory.CreateAsync(scope.RoleArn, scope.ExternalSecret, cancellationToken);
        }
        catch (AmazonSecurityTokenServiceException ex)
        {
            coverage.Add(new CoverageEntry("credentials", CoverageStatus.Denied, ex.ErrorCode));
            return Result();
        }

        using (api)
        {
            var account = await api.GetAccountIdAsync(cancellationToken);
            if (!string.Equals(account, scope.ExternalId, StringComparison.Ordinal))
            {
                coverage.Add(new CoverageEntry("credentials", CoverageStatus.Denied, "Assumed role belongs to a different account than the scope."));
                return Result();
            }

            try
            {
                var roles = await api.ListRolesAsync(cancellationToken);
                identities.AddRange(roles.Select(role => new ConnectorIdentity("role", role.Arn, role.Name, new Dictionary<string, string>
                {
                    ["path"] = role.Path,
                    ["trustPolicy"] = role.TrustPolicy
                })));
                coverage.Add(new CoverageEntry("iam.roles", identities.Count == 0 ? CoverageStatus.NoData : CoverageStatus.Collected));
            }
            catch (AmazonIdentityManagementServiceException ex) when (ex.ErrorCode is "AccessDenied" or "AccessDeniedException")
            {
                coverage.Add(new CoverageEntry("iam.roles", CoverageStatus.Denied, ex.ErrorCode));
            }
        }

        return Result();
    }
}
