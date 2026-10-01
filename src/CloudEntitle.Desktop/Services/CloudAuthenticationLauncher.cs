using System.Diagnostics;
using System.Text.Json;
using CloudEntitle.Domain;
using CloudEntitle.Application.Interfaces;

namespace CloudEntitle.Desktop.Services;

public interface ICloudAuthenticationLauncher
{
    void LaunchAuthentication(CloudProviderType provider);
    void LaunchDisconnection(CloudProviderType provider);
    Task<bool> IsConnectedAsync(CloudProviderType provider, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CloudProviderType provider, CancellationToken cancellationToken = default);
}

public sealed class CloudAuthenticationLauncher : ICloudAuthenticationLauncher
{
    private readonly ICloudCommandRunner? _commandRunner;

    public CloudAuthenticationLauncher(ICloudCommandRunner? commandRunner = null)
    {
        _commandRunner = commandRunner;
    }

    public void LaunchAuthentication(CloudProviderType provider)
    {
        var command = provider switch
        {
            CloudProviderType.Azure => "az login",
            CloudProviderType.Aws => "aws configure sso",
            CloudProviderType.Gcp => "gcloud auth login",
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported cloud provider.")
        };

        var commandShell = Environment.GetEnvironmentVariable("ComSpec");
        var startInfo = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(commandShell) ? "cmd.exe" : commandShell,
            Arguments = $"/k {command}",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal
        };

        var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Unable to open a terminal for cloud sign-in.");
        }

        process.Dispose();
    }

    public void LaunchDisconnection(CloudProviderType provider)
    {
        var command = provider switch
        {
            CloudProviderType.Azure => "az logout",
            CloudProviderType.Aws => "aws sso logout",
            CloudProviderType.Gcp => "gcloud auth revoke --all",
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported cloud provider.")
        };

        LaunchInTerminal(command);
    }

    public async Task<bool> IsConnectedAsync(CloudProviderType provider, CancellationToken cancellationToken = default)
    {
        if (_commandRunner is null)
        {
            return false;
        }

        var (command, arguments) = provider switch
        {
            CloudProviderType.Azure => ("az", (IReadOnlyList<string>)new[] { "account", "show", "--output", "json" }),
            CloudProviderType.Aws => ("aws", (IReadOnlyList<string>)new[] { "sts", "get-caller-identity", "--output", "json" }),
            CloudProviderType.Gcp => ("gcloud", (IReadOnlyList<string>)new[] { "auth", "list", "--format=json" }),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported cloud provider.")
        };

        var result = await _commandRunner.RunAsync(command, arguments, cancellationToken);
        if (!result.IsSuccess)
        {
            return false;
        }

        if (provider != CloudProviderType.Gcp)
        {
            return true;
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.ValueKind == JsonValueKind.Array
            && document.RootElement.EnumerateArray().Any(account =>
                account.TryGetProperty("status", out var status)
                && string.Equals(status.GetString(), "ACTIVE", StringComparison.OrdinalIgnoreCase));
    }

    public async Task DisconnectAsync(CloudProviderType provider, CancellationToken cancellationToken = default)
    {
        if (_commandRunner is null)
        {
            throw new InvalidOperationException("Cloud CLI command execution is not configured.");
        }

        var (command, arguments) = provider switch
        {
            CloudProviderType.Azure => ("az", (IReadOnlyList<string>)new[] { "logout" }),
            CloudProviderType.Aws => ("aws", (IReadOnlyList<string>)new[] { "sso", "logout" }),
            CloudProviderType.Gcp => ("gcloud", (IReadOnlyList<string>)new[] { "auth", "revoke", "--all" }),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported cloud provider.")
        };

        var result = await _commandRunner.RunAsync(command, arguments, cancellationToken);
        if (!result.IsSuccess)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError) ? "The provider CLI returned a failure." : result.StandardError.Trim();
            throw new InvalidOperationException(detail);
        }
    }

    private static void LaunchInTerminal(string command)
    {
        var commandShell = Environment.GetEnvironmentVariable("ComSpec");
        var startInfo = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(commandShell) ? "cmd.exe" : commandShell,
            Arguments = $"/k {command}",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal
        };

        var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Unable to open a terminal for the cloud command.");
        }

        process.Dispose();
    }
}
