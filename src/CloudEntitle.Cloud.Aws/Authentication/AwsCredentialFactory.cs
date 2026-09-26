using Amazon.Runtime;

namespace CloudEntitle.Cloud.Aws.Authentication;

public sealed class AwsCredentialFactory
{
    public AWSCredentials CreateDefault() => FallbackCredentialsFactory.GetCredentials();
}
