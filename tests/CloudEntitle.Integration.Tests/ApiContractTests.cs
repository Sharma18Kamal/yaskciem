using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Clients;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.OrganizationManagement.Models;
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

    [Fact]
    public async Task SharedOrganizationClient_ShouldSendBearerTokenAndReadEnvelope()
    {
        var organization = new OrganizationProfile(Guid.NewGuid(), "Shared Org", "shared-org", OrganizationStatus.Active, DateTime.UtcNow)
        {
            EntraTenantId = Guid.NewGuid().ToString("D")
        };
        var handler = new RecordingHandler(new ApiEnvelope<IReadOnlyList<OrganizationProfile>>(new[] { organization }, "correlation", ApiContractVersion.Current));
        var client = new CiemApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.example.invalid/") });

        var response = await client.GetOrganizationsAsync("access-token");

        Assert.Equal(organization.Id, Assert.Single(response!.Data).Id);
        Assert.Equal("GET", handler.Method);
        Assert.Equal("api/v1/organizations", handler.Path);
        Assert.Equal("Bearer access-token", handler.Authorization);
    }

    private sealed class RecordingHandler(ApiEnvelope<IReadOnlyList<OrganizationProfile>> payload) : HttpMessageHandler
    {
        public string? Method { get; private set; }
        public string? Path { get; private set; }
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method.Method;
            Path = request.RequestUri?.PathAndQuery.TrimStart('/');
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload) });
        }
    }
}
