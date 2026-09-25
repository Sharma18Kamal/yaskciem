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
        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
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
}
