using Azure.Core;
using Azure.Identity;

namespace CloudEntitle.Cloud.Azure.Authentication;

public sealed class AzureCredentialFactory
{
    public TokenCredential CreateDefault() => new DefaultAzureCredential(new DefaultAzureCredentialOptions
    {
        ExcludeInteractiveBrowserCredential = false
    });
}
