using CloudEntitle.Application.Services;
using CloudEntitle.Domain;
using Xunit;

namespace CloudEntitle.Application.Tests;

public class RiskEngineTests
{
    [Fact]
    public async Task RiskEngine_ShouldFlagOwnerPermissionsAsHighRisk()
    {
        var engine = new RiskEngine();

        var findings = await engine.EvaluateAsync(new[]
        {
            new CloudPermission
            {
                Id = Guid.NewGuid(),
                AccountId = Guid.NewGuid(),
                Name = "Owner",
                Action = "*/*",
                Resource = "/subscriptions/demo",
                Scope = "/subscriptions/demo",
                Source = "Azure RBAC"
            }
        }, CloudProviderType.Azure);

        Assert.NotEmpty(findings);
        Assert.Contains(findings, x => x.Severity == RiskSeverity.High && x.Title.Contains("Owner"));
    }

    [Fact]
    public async Task RiskEngine_ShouldKeepNonPrivilegedPermissionsOutOfScope()
    {
        var engine = new RiskEngine();

        var findings = await engine.EvaluateAsync(new[]
        {
            new CloudPermission
            {
                Id = Guid.NewGuid(),
                AccountId = Guid.NewGuid(),
                Name = "Reader",
                Action = "read",
                Resource = "/subscriptions/demo",
                Scope = "/subscriptions/demo",
                Source = "Azure RBAC"
            }
        }, CloudProviderType.Azure);

        Assert.Empty(findings);
    }
}
