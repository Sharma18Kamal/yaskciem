using CloudEntitle.Alerting.Models;
using CloudEntitle.Alerting.Services;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Monitoring.Models;
using Xunit;

namespace CloudEntitle.Alerting.Tests;

public sealed class AlertingTests
{
    [Fact]
    public void HighRiskFinding_ShouldCreateAlertAboveThreshold()
    {
        var finding = new RiskFindingModel { Id = Guid.NewGuid(), Fingerprint = "risk-1", RuleId = Guid.NewGuid(), CloudProvider = CloudProvider.Azure, CloudAccountId = Guid.NewGuid(), PrincipalId = Guid.NewGuid(), Severity = RiskSeverity.High, Status = FindingStatus.Open, Title = "Privileged access", Description = "Review access", Evidence = new("Evidence") };

        var result = new AlertingService().Evaluate(new[] { finding });

        Assert.Equal(1, result.Created);
    }

    [Fact]
    public void DuplicateDriftEvents_ShouldBeDeduplicated()
    {
        var scan = Guid.NewGuid();
        var drift = new DriftReport(scan, null, new[]
        {
            new DriftEvent(Guid.NewGuid(), scan, null, DriftType.Added, DriftSeverity.High, CloudProvider.Aws, "EffectiveAccess", "access-1", "New access", "evidence", DateTime.UtcNow),
            new DriftEvent(Guid.NewGuid(), scan, null, DriftType.Added, DriftSeverity.High, CloudProvider.Aws, "EffectiveAccess", "access-1", "New access", "evidence", DateTime.UtcNow)
        }, 2, 0, 0, DateTime.UtcNow);

        var result = new AlertingService().Evaluate(Array.Empty<RiskFindingModel>(), drift);

        Assert.Equal(1, result.Created);
        Assert.Equal(1, result.Suppressed);
    }

    [Fact]
    public void Notification_ShouldContainEvidenceWithoutCredentials()
    {
        var finding = new RiskFindingModel { Id = Guid.NewGuid(), Fingerprint = "risk-2", RuleId = Guid.NewGuid(), CloudProvider = CloudProvider.Gcp, CloudAccountId = Guid.NewGuid(), PrincipalId = Guid.NewGuid(), Severity = RiskSeverity.Critical, Status = FindingStatus.Open, Title = "Critical", Description = "Review", Evidence = new("Provider evidence") };
        var service = new AlertingService();
        var report = service.Evaluate(new[] { finding });
        var alert = new Alert(Guid.NewGuid(), "risk-2", AlertSource.RiskFinding, AlertSeverity.Critical, AlertStatus.New, CloudProvider.Gcp, "Critical", "Review", new Dictionary<string, string> { ["Evidence"] = "Provider evidence" }, DateTime.UtcNow, null, null);

        var notification = service.CreateNotification(alert, NotificationChannelType.InProcess);

        Assert.Contains("Provider evidence", notification.Body);
        Assert.DoesNotContain("token", notification.Body, StringComparison.OrdinalIgnoreCase);
    }
}
