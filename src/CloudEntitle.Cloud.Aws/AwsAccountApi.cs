using Amazon.IdentityManagement;
using Amazon.IdentityManagement.Model;
using Amazon.Runtime;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using CloudEntitle.Cloud.Aws.Authentication;

namespace CloudEntitle.Cloud.Aws;

public sealed record AwsRole(string Arn, string Name, string Path, string TrustPolicy);

/// <summary>Read-only view of one AWS account, reached through an assumed role.</summary>
public interface IAwsAccountApi : IDisposable
{
    Task<string> GetAccountIdAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<AwsRole>> ListRolesAsync(CancellationToken cancellationToken);
}

public interface IAwsAccountApiFactory
{
    Task<IAwsAccountApi> CreateAsync(string roleArn, string externalId, CancellationToken cancellationToken);
}

public sealed class SdkAwsAccountApiFactory : IAwsAccountApiFactory
{
    private readonly AwsAssumeRoleCredentialFactory _credentials;

    public SdkAwsAccountApiFactory(AwsAssumeRoleCredentialFactory credentials) => _credentials = credentials;

    public async Task<IAwsAccountApi> CreateAsync(string roleArn, string externalId, CancellationToken cancellationToken)
    {
        var creds = await _credentials.CreateAsync(roleArn, externalId, cancellationToken);
        return new SdkAwsAccountApi(creds);
    }
}

internal sealed class SdkAwsAccountApi : IAwsAccountApi
{
    private readonly AmazonSecurityTokenServiceClient _sts;
    private readonly AmazonIdentityManagementServiceClient _iam;

    public SdkAwsAccountApi(AWSCredentials credentials)
    {
        _sts = new AmazonSecurityTokenServiceClient(credentials);
        _iam = new AmazonIdentityManagementServiceClient(credentials);
    }

    public async Task<string> GetAccountIdAsync(CancellationToken cancellationToken)
        => (await _sts.GetCallerIdentityAsync(new GetCallerIdentityRequest(), cancellationToken)).Account;

    public async Task<IReadOnlyList<AwsRole>> ListRolesAsync(CancellationToken cancellationToken)
    {
        var roles = new List<AwsRole>();
        var request = new ListRolesRequest();
        ListRolesResponse page;
        do
        {
            page = await _iam.ListRolesAsync(request, cancellationToken);
            roles.AddRange(page.Roles.Select(r => new AwsRole(r.Arn, r.RoleName, r.Path, r.AssumeRolePolicyDocument ?? string.Empty)));
            request.Marker = page.Marker;
        } while (page.IsTruncated == true);

        return roles;
    }

    public void Dispose()
    {
        _sts.Dispose();
        _iam.Dispose();
    }
}
