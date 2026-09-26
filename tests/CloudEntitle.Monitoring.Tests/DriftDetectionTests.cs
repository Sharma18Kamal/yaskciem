using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Monitoring.Models;
using CloudEntitle.Monitoring.Services;
using CloudEntitle.Normalization.Models;
using Xunit;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;
using DomainAccessPath = CloudEntitle.Domain.Entities.AccessPath;

namespace CloudEntitle.Monitoring.Tests;

public sealed class DriftDetectionTests
{
    [Fact]
    public async Task AddedEffectiveAccess_ShouldBeHighSeverityDrift()
    {
        var before = Snapshot(Guid.NewGuid(), Array.Empty<DomainEffectiveAccess>());
        var access = new DomainEffectiveAccess { Id = Guid.NewGuid(), Provider = CloudProvider.Azure, CloudAccountId = Guid.NewGuid(), PrincipalId = Guid.NewGuid(), PermissionId = Guid.NewGuid(), Decision = AccessDecision.Allow };
        var current = Snapshot(Guid.NewGuid(), new[] { access });

        var report = await new DriftDetectionService().CompareAsync(before, current);

        var drift = Assert.Single(report.Events);
        Assert.Equal(DriftType.Added, drift.Type);
        Assert.Equal(DriftSeverity.High, drift.Severity);
    }

    [Fact]
    public async Task ChangedFindingStatus_ShouldBeReportedAsChanged()
    {
        var finding = new RiskFindingModel { Id = Guid.NewGuid(), Fingerprint = "finding-1", RuleId = Guid.NewGuid(), CloudProvider = CloudProvider.Aws, CloudAccountId = Guid.NewGuid(), PrincipalId = Guid.NewGuid(), Severity = RiskSeverity.High, Status = FindingStatus.Open };
        var currentFinding = new RiskFindingModel { Id = finding.Id, Fingerprint = finding.Fingerprint, RuleId = finding.RuleId, CloudProvider = finding.CloudProvider, CloudAccountId = finding.CloudAccountId, PrincipalId = finding.PrincipalId, Severity = RiskSeverity.High, Status = FindingStatus.Accepted };
        var before = Snapshot(Guid.NewGuid(), Array.Empty<DomainEffectiveAccess>(), new[] { finding });
        var current = Snapshot(Guid.NewGuid(), Array.Empty<DomainEffectiveAccess>(), new[] { currentFinding });

        var report = await new DriftDetectionService().CompareAsync(before, current);

        Assert.Contains(report.Events, item => item.EntityType == "RiskFinding" && item.Type == DriftType.Changed);
    }

    [Fact]
    public async Task FirstSnapshot_ShouldReportAdditionsWithoutPreviousScan()
    {
        var access = new DomainEffectiveAccess { Id = Guid.NewGuid(), Provider = CloudProvider.Gcp, CloudAccountId = Guid.NewGuid(), PrincipalId = Guid.NewGuid(), PermissionId = Guid.NewGuid(), Decision = AccessDecision.Allow };
        var report = await new DriftDetectionService().CompareAsync(null, Snapshot(Guid.NewGuid(), new[] { access }));

        Assert.Equal(1, report.Added);
        Assert.Null(report.PreviousScanId);
    }

    private static MonitoringSnapshot Snapshot(Guid scanId, IReadOnlyList<DomainEffectiveAccess> access, IReadOnlyList<RiskFindingModel>? findings = null) =>
        new(scanId, DateTime.UtcNow, Array.Empty<CloudAccount>(), Array.Empty<CloudPrincipal>(), Array.Empty<NormalizedEntitlement>(), access, Array.Empty<DomainAccessPath>(), findings ?? Array.Empty<RiskFindingModel>());
}
