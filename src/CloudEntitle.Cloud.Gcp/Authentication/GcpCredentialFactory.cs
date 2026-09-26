using Google.Apis.Auth.OAuth2;

namespace CloudEntitle.Cloud.Gcp.Authentication;

public sealed class GcpCredentialFactory
{
    public Task<GoogleCredential> CreateDefaultAsync(CancellationToken cancellationToken = default) =>
        GoogleCredential.GetApplicationDefaultAsync(cancellationToken);
}
