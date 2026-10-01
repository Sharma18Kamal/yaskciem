using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed class ApiContractTests
{
    [Fact]
    public void ProviderCapabilities_ShouldDeclareReadOnlyModes()
    {
        var providers = new[]
        {
            new ProviderCapabilityDto(CloudProvider.Azure, true, true, "Azure CLI"),
            new ProviderCapabilityDto(CloudProvider.Aws, true, true, "AWS CLI"),
            new ProviderCapabilityDto(CloudProvider.Gcp, true, true, "gcloud")
        };

        Assert.Equal(3, providers.Length);
        Assert.All(providers, provider => Assert.True(provider.ReadOnly));
        Assert.Equal("v1", ApiContractVersion.Current);
    }

    [Fact]
    public void Envelope_ShouldCarryCorrelationAndContractVersion()
    {
        var envelope = new ApiEnvelope<string>("ok", "correlation-1", ApiContractVersion.Current);

        Assert.Equal("ok", envelope.Data);
        Assert.Equal("correlation-1", envelope.CorrelationId);
        Assert.Equal("v1", envelope.Version);
    }
}
