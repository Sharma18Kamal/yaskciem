using CloudEntitle.Security.Services;
using Xunit;

namespace CloudEntitle.Security.Tests;

public sealed class SecurityTests
{
    [Fact]
    public void MetadataRedactor_ShouldRedactSecretKeysAndPreserveSafeMetadata()
    {
        var output = SensitiveDataRedactor.RedactMetadata(new Dictionary<string, string> { ["token"] = "secret-value", ["environment"] = "test" });

        Assert.Equal("[REDACTED]", output["token"]);
        Assert.Equal("test", output["environment"]);
    }

    [Fact]
    public void TextRedactor_ShouldRedactBearerAndAssignments()
    {
        var output = SensitiveDataRedactor.RedactText("Authorization: Bearer abc123 password=hello");

        Assert.DoesNotContain("abc123", output);
        Assert.DoesNotContain("hello", output);
        Assert.Contains("[REDACTED]", output);
    }

    [Fact]
    public void ProductionConfiguration_ShouldRequireHttpsAndHsts()
    {
        var configuration = SecurityConfiguration.Production;

        Assert.True(configuration.RequireHttps);
        Assert.True(configuration.UseHsts);
        Assert.True(configuration.RedactSensitiveLogs);
    }
}
