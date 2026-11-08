using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CloudEntitle.LicenseServer.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed class QradarTlsTests
{
    [Fact]
    public async Task Sender_RejectsAnUntrustedServerCertificateWithoutChangingMachineTrust()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var path = Path.Combine(Path.GetTempPath(), $"yask-tls-test-{Guid.NewGuid():N}.pfx");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            await File.WriteAllBytesAsync(path, certificate.Export(X509ContentType.Pkcs12, "fixture-only"), timeout.Token);
            using var serverCertificate = X509CertificateLoader.LoadPkcs12FromFile(path, "fixture-only");
            listener.Start();
            var server = ObserveHandshakeAsync(listener, serverCertificate, timeout.Token);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Url"] = $"tls://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}",
                ["ClientCertificatePath"] = path,
                ["ClientCertificatePassword"] = "fixture-only"
            }).Build();
            var result = await new QradarSyslogSender().SendAsync(configuration, [10], timeout.Token);
            var serverFailure = await server;
            Assert.False(result.Delivered);
            Assert.True(result.FailureCode == "channel_invalid", $"Client: {result.FailureCode}; local TLS fixture: {serverFailure}");
            Assert.False(result.Retryable);
        }
        finally
        {
            listener.Stop();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Sender_ReportsMissingClientCertificateAsPermanentConfigurationFailure()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Url"] = "tls://127.0.0.1:6514",
            ["ClientCertificatePath"] = Path.Combine(Path.GetTempPath(), $"yask-missing-{Guid.NewGuid():N}.pfx")
        }).Build();
        var result = await new QradarSyslogSender().SendAsync(configuration, [10], default);
        Assert.False(result.Delivered);
        Assert.False(result.Retryable);
        Assert.Equal("channel_invalid", result.FailureCode);
    }

    private static async Task<string?> ObserveHandshakeAsync(TcpListener listener, X509Certificate2 certificate, CancellationToken token)
    {
        using var client = await listener.AcceptTcpClientAsync(token);
        await using var tls = new SslStream(client.GetStream());
        try
        {
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }, token);
            var read = await tls.ReadAsync(new byte[1], token);
            return read == 0 ? "Peer closed without application data" : "Peer sent application data";
        }
        catch (AuthenticationException exception) { return exception.ToString(); }
        catch (IOException exception) { return exception.ToString(); }
    }
}
