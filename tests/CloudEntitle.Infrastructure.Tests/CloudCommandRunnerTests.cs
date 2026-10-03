using CloudEntitle.Infrastructure;
using Xunit;

namespace CloudEntitle.Infrastructure.Tests;

[CollectionDefinition("Cloud CLI process environment", DisableParallelization = true)]
public sealed class CloudCliProcessEnvironmentCollection;

[Collection("Cloud CLI process environment")]
public sealed class CloudCommandRunnerTests
{
    [Fact]
    public async Task ProcessCloudCommandRunner_ShouldExecuteBatchCliFromPathWithSpaces()
    {
        if (!OperatingSystem.IsWindows()) return;

        var directory = Path.Combine(Path.GetTempPath(), $"CloudEntitle CLI {Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var commandPath = Path.Combine(directory, "test-cloud-cli.cmd");
        await File.WriteAllTextAsync(commandPath, "@echo off\r\necho runner-ok\r\n");
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + originalPath);

            var result = await new ProcessCloudCommandRunner().RunAsync("test-cloud-cli", Array.Empty<string>());

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("runner-ok", result.StandardOutput.Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessCloudCommandRunner_ShouldRunGcloudAuthListWhenInstalled()
    {
        if (CloudCliExecutableResolver.Resolve("gcloud") is null) return;

        var result = await new ProcessCloudCommandRunner().RunAsync("gcloud", new[] { "auth", "list", "--format=json" });

        Assert.True(result.IsSuccess, result.StandardError);
        using var document = System.Text.Json.JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, document.RootElement.ValueKind);
    }
}
