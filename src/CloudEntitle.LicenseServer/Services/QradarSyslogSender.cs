using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace CloudEntitle.LicenseServer.Services;

public interface IQradarSyslogSender
{
    Task<NotificationSendResult> SendAsync(IConfiguration configuration, byte[] message, CancellationToken token);
}

public sealed class QradarSyslogSender : IQradarSyslogSender
{
    public async Task<NotificationSendResult> SendAsync(IConfiguration configuration, byte[] message, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var writeStarted = false;
        try
        {
            using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
                configuration["ClientCertificatePath"]!, configuration["ClientCertificatePassword"],
                // Windows Schannel cannot use ephemeral private keys; default imports are released on disposal.
                OperatingSystem.IsWindows() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
            if (!certificate.HasPrivateKey) return new(false, false, null, "channel_invalid");
            var endpoint = new Uri(configuration["Url"]!);
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(endpoint.Host, endpoint.Port, timeout.Token);
            await using var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = endpoint.Host,
                ClientCertificates = new X509CertificateCollection { certificate },
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.Online
            }, timeout.Token);
            writeStarted = true;
            await tls.WriteAsync(message, timeout.Token);
            await tls.FlushAsync(timeout.Token);
            // Syslog has no receiver acknowledgement: Delivered means a completed TLS write only.
            return new(true, false, null, null);
        }
        catch (CryptographicException) { return new(false, false, null, "channel_invalid"); }
        catch (AuthenticationException) { return new(false, false, null, "channel_invalid"); }
        catch (FileNotFoundException) { return new(false, false, null, "channel_invalid"); }
        catch (DirectoryNotFoundException) { return new(false, false, null, "channel_invalid"); }
        catch (UnauthorizedAccessException) { return new(false, false, null, "channel_invalid"); }
        catch (SocketException)
        { return new(false, !writeStarted, null, writeStarted ? "outcome_unknown" : "network_error"); }
        catch (IOException)
        { return new(false, !writeStarted, null, writeStarted ? "outcome_unknown" : "network_error"); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new(false, !writeStarted, null, writeStarted ? "outcome_unknown" : "timeout"); }
        catch (OperationCanceledException) when (writeStarted)
        { return new(false, false, null, "outcome_unknown"); }
    }
}

public static class QradarEventFormatter
{
    public static byte[] Format(NotificationDeliveryLease lease, string host)
    {
        var severity = lease.Event.Severity switch
        {
            Integration.Contracts.SharedAlertSeverity.Critical => 10,
            Integration.Contracts.SharedAlertSeverity.High => 8,
            Integration.Contracts.SharedAlertSeverity.Medium => 5,
            Integration.Contracts.SharedAlertSeverity.Low => 3,
            _ => 1
        };
        var attributes = new Dictionary<string, string>
        {
            ["cat"] = lease.Event.Kind,
            ["sev"] = severity.ToString(CultureInfo.InvariantCulture),
            ["eventId"] = lease.Event.EventId.ToString("D"),
            ["deliveryId"] = lease.Delivery.DeliveryId.ToString("D"),
            ["tenantId"] = lease.TenantId.ToString("D"),
            ["organizationId"] = lease.OrganizationId.ToString("D"),
            ["provider"] = lease.Event.Provider.ToString(),
            ["accountId"] = lease.Event.ExternalAccountId,
            ["entityKey"] = lease.Event.EntityKey,
            ["change"] = lease.Event.Change
        };
        var leef = $"LEEF:2.0|YASK|CIEM|1.0|{Escape(lease.Event.EventType)}|0x09|"
            + string.Join('\t', attributes.Select(pair => $"{pair.Key}={Escape(pair.Value)}"));
        // Newline framing is compatible with QRadar's single-line TLS syslog input.
        var timestamp = lease.Event.DetectedAtUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
        var message = $"<134>1 {timestamp} {host} YASK-CIEM - - - {leef}\n";
        return Encoding.UTF8.GetBytes(message);
    }

    private static string Escape(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
}
