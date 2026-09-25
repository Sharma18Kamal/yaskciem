using System.Net;
using System.Net.Http;
using System.Text;
using CloudEntitle.Domain;
using CloudEntitle.Licensing;
using Xunit;

namespace CloudEntitle.Licensing.Tests;

public class RemoteLicenseServiceTests
{
    [Fact]
    public void RemoteLicenseService_ShouldRejectNonHttpsEndpoint()
    {
        using var client = new HttpClient();

        Assert.Throws<ArgumentException>(() =>
            new RemoteLicenseService(client, new Uri("http://license.example.test/validate")));
    }

    [Fact]
    public async Task RemoteLicenseService_ShouldValidateThroughHttpsEndpoint()
    {
        using var client = new HttpClient(new StubHandler());
        var service = new RemoteLicenseService(client, new Uri("https://license.example.test/validate"));

        var result = await service.ValidateAsync("license-key");

        Assert.True(result.IsValid);
        Assert.Equal(LicenseStatus.Active, result.Status);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"isValid\":true,\"status\":\"Active\",\"message\":\"Valid\"}",
                    Encoding.UTF8,
                    "application/json")
            });
    }
}
