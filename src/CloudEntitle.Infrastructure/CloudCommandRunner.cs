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
        var executable = CloudCliExecutableResolver.Resolve(command) ?? (command, false);
        var isAzureBatchLauncher = command == "az" && executable.IsBatchFile;
        var startInfo = new ProcessStartInfo
        {
            FileName = isAzureBatchLauncher
                ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(executable.Path)!, "..", "python.exe"))
                : executable.IsBatchFile ? "cmd.exe" : executable.Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (isAzureBatchLauncher)
        {
            startInfo.ArgumentList.Add("-IBm");
            startInfo.ArgumentList.Add("azure.cli");
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }
        else if (executable.IsBatchFile)
        {
            startInfo.Arguments = BuildBatchCommand(executable.Path, arguments);
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

    private static string BuildBatchCommand(string path, IReadOnlyList<string> arguments)
        => $"/d /s /c \"\"{path}\" {string.Join(" ", arguments.Select(QuoteBatchArgument))}\"";

    private static string QuoteBatchArgument(string argument)
        => argument.Contains(' ') || argument.Contains('"')
            ? $"\"{argument.Replace("\"", "\"\"")}\""
            : argument;
}

public static class CloudCliExecutableResolver
{
    public static (string Path, bool IsBatchFile)? Resolve(string command)
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var candidates = (command switch
        {
            "az" => new[]
            {
                Path.Combine(programFiles, "Microsoft SDKs\\Azure\\CLI2\\wbin\\az.cmd"),
                Path.Combine(programFiles, "Microsoft SDKs\\Azure\\CLI2\\wbin\\az.exe"),
                Path.Combine(localApplicationData, "Programs\\Azure CLI\\wbin\\az.cmd")
            },
            "aws" => new[]
            {
                Path.Combine(programFiles, "Amazon\\AWSCLIV2\\aws.exe"),
                Path.Combine(programFiles, "Amazon\\AWSCLI\\bin\\aws.exe"),
                Path.Combine(localApplicationData, "Programs\\Amazon\\AWSCLIV2\\aws.exe")
            },
            "gcloud" => new[]
            {
                Path.Combine(programFilesX86, "Google\\Cloud SDK\\google-cloud-sdk\\bin\\gcloud.cmd"),
                Path.Combine(programFiles, "Google\\Cloud SDK\\google-cloud-sdk\\bin\\gcloud.cmd"),
                Path.Combine(localApplicationData, "Google\\Cloud SDK\\google-cloud-sdk\\bin\\gcloud.cmd")
            },
            _ => Array.Empty<string>()
        });

        var resolved = candidates.FirstOrDefault(File.Exists);
        if (resolved is null)
        {
            var extensions = command.Equals("gcloud", StringComparison.OrdinalIgnoreCase) || command.Equals("az", StringComparison.OrdinalIgnoreCase)
                ? new[] { ".cmd", ".exe", ".bat" }
                : new[] { ".exe", ".cmd", ".bat" };
            resolved = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SelectMany(directory => extensions.Select(extension => Path.Combine(directory.Trim('"'), command + extension)))
                .FirstOrDefault(File.Exists);
        }

        return resolved is null
            ? null
            : (resolved, Path.GetExtension(resolved).Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(resolved).Equals(".bat", StringComparison.OrdinalIgnoreCase));
    }
}
