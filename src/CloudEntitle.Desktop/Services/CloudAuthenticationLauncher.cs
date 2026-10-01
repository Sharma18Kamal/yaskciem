using System.Diagnostics;
using CloudEntitle.Domain;

namespace CloudEntitle.Desktop.Services;

public interface ICloudAuthenticationLauncher
{
    void LaunchAuthentication(CloudProviderType provider);
    void LaunchDisconnection(CloudProviderType provider);
}

public sealed class CloudAuthenticationLauncher : ICloudAuthenticationLauncher
{
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
