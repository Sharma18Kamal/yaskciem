namespace CloudEntitle.Infrastructure;

using System.ComponentModel;
using System.Diagnostics;
using CloudEntitle.Application.Interfaces;

public sealed class ProcessCloudCommandRunner : ICloudCommandRunner
{
    public async Task<CloudCommandResult> RunAsync(
        string command,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var executable = ResolveExecutable(command);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable.IsBatchFile ? "cmd.exe" : executable.Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (executable.IsBatchFile)
        {
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(BuildBatchCommand(executable.Path, arguments));
        }
        else
        {
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return new CloudCommandResult(-1, string.Empty, exception.Message);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return new CloudCommandResult(
            process.ExitCode,
            await outputTask,
            await errorTask);
    }

    private static (string Path, bool IsBatchFile) ResolveExecutable(string command)
    {
        var resolved = (command switch
        {
            "az" => new[]
            {
                "C:\\Program Files\\Microsoft SDKs\\Azure\\CLI2\\wbin\\az.cmd",
                "C:\\Program Files\\Microsoft SDKs\\Azure\\CLI2\\wbin\\az.exe"
            },
            "aws" => new[]
            {
                "C:\\Program Files\\Amazon\\AWSCLIV2\\aws.exe",
                "C:\\Program Files\\Amazon\\AWSCLI\\bin\\aws.exe"
            },
            "gcloud" => new[]
            {
                "C:\\Program Files (x86)\\Google\\Cloud SDK\\google-cloud-sdk\\bin\\gcloud.cmd",
                "C:\\Program Files\\Google\\Cloud SDK\\google-cloud-sdk\\bin\\gcloud.cmd"
            },
            _ => Array.Empty<string>()
        }).FirstOrDefault(File.Exists);

        if (resolved is not null)
        {
            return (resolved, Path.GetExtension(resolved).Equals(".cmd", StringComparison.OrdinalIgnoreCase));
        }

        return (command, false);
    }

    private static string BuildBatchCommand(string path, IReadOnlyList<string> arguments)
        => string.Join(" ", new[] { QuoteCommandArgument(path) }.Concat(arguments.Select(QuoteCommandArgument)));

    private static string QuoteCommandArgument(string argument)
        => $"\"{argument.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
}
