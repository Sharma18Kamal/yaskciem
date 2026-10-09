namespace CloudEntitle.Desktop.Services;

using System.Diagnostics;
using CloudEntitle.Application.Interfaces;
using Serilog;

public sealed class LoggingCloudCommandRunner(ICloudCommandRunner inner) : ICloudCommandRunner
{
    public async Task<CloudCommandResult> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        var description = $"{command} {string.Join(' ', arguments.Take(4))}";
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await inner.RunAsync(command, arguments, cancellationToken);
            Log.Information("CLI {Command} finished in {Seconds:n1}s with exit code {ExitCode}", description, stopwatch.Elapsed.TotalSeconds, result.ExitCode);
            return result;
        }
        catch (OperationCanceledException)
        {
            Log.Information("CLI {Command} cancelled after {Seconds:n1}s", description, stopwatch.Elapsed.TotalSeconds);
            throw;
        }
    }
}
