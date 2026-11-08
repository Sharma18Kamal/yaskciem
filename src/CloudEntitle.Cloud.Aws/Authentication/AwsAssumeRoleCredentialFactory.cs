using Amazon.Runtime;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;

namespace CloudEntitle.Cloud.Aws.Authentication;

/// <summary>Obtains short-lived read-only credentials via sts:AssumeRole with a per-tenant external ID.</summary>
public sealed class AwsAssumeRoleCredentialFactory
{
    private readonly AWSCredentials _brokerCredentials;

    public AwsAssumeRoleCredentialFactory(AWSCredentials brokerCredentials) => _brokerCredentials = brokerCredentials;

    public async Task<AWSCredentials> CreateAsync(string roleArn, string externalId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(roleArn)) throw new ArgumentException("Role ARN is required.", nameof(roleArn));
        if (string.IsNullOrWhiteSpace(externalId)) throw new ArgumentException("External ID is required.", nameof(externalId));

        using var sts = new AmazonSecurityTokenServiceClient(_brokerCredentials);
        var response = await sts.AssumeRoleAsync(new AssumeRoleRequest
        {
            RoleArn = roleArn,
            ExternalId = externalId,
            RoleSessionName = "yask-ciem-scan",
            DurationSeconds = 900
        }, cancellationToken);

        var c = response.Credentials;
        return new SessionAWSCredentials(c.AccessKeyId, c.SecretAccessKey, c.SessionToken);
    }
}
