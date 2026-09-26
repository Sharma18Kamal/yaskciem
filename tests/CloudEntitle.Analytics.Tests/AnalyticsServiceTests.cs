using CloudEntitle.Analytics.Services;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using Xunit;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;

namespace CloudEntitle.Analytics.Tests;

public sealed class AnalyticsServiceTests
{
    [Fact]
    public void Calculate_ShouldProduceExecutiveProviderRiskAndAccessBreakdowns()
    {
        var azure = new CloudAccount { Id = Guid.NewGuid(), Name = "Azure", CloudProvider = CloudProviderType.Azure, ExternalAccountId = "azure" };
        var aws = new CloudAccount { Id = Guid.NewGuid(), Name = "AWS", CloudProvider = CloudProviderType.Aws, ExternalAccountId = "aws" };
        var principal = new CloudPrincipal { Id = Guid.NewGuid(), AccountId = azure.Id, Name = "Admin", IdentityType = IdentityType.User, IsPrivileged = true, IsExternal = true, LastActivityAt = DateTime.UtcNow.AddDays(-120) };
        var finding = new RiskFindingModel { Id = Guid.NewGuid(), RuleId = Guid.NewGuid(), CloudProvider = CloudProvider.Azure, CloudAccountId = azure.Id, PrincipalId = principal.Id, Severity = RiskSeverity.Critical };
        var access = new DomainEffectiveAccess { Id = Guid.NewGuid(), Provider = CloudProvider.Azure, CloudAccountId = azure.Id, PrincipalId = principal.Id, Decision = AccessDecision.Allow, IsPrivileged = true };
        var snapshot = new DemoSnapshot(new[] { azure, aws }, new[] { principal }, Array.Empty<CloudResource>(), Array.Empty<Entitlement>(), new[] { access }, new[] { finding }, Array.Empty<RecommendationModel>(), new[] { new Scan { Id = Guid.NewGuid(), CloudAccountId = azure.Id, CompletedAt = DateTime.UtcNow, Status = ScanStatus.Completed } });

        var analytics = new AnalyticsService().Calculate(snapshot, DateTime.UtcNow);

        Assert.Equal(2, analytics.CloudAccounts);
        Assert.Equal(1, analytics.PrivilegedIdentities);
        Assert.Equal(1, analytics.DormantIdentities);
        Assert.Equal(1, analytics.ExternalIdentities);
        Assert.Contains(analytics.AccountsByProvider, item => item.Label == "Azure" && item.Value == 1);
        Assert.Contains(analytics.FindingsBySeverity, item => item.Label == "Critical" && item.Value == 1);
        Assert.Contains(analytics.AccessByDecision, item => item.Label == "Allow" && item.Value == 1);
    }
}
