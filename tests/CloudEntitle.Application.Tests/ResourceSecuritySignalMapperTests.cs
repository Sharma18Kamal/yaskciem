using CloudEntitle.Application.Services;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using Xunit;

namespace CloudEntitle.Application.Tests;

public sealed class ResourceSecuritySignalMapperTests
{
    [Fact]
    public void MapTags_ShouldEmitUnconfirmedSecuritySignalsAndPartialCoverage()
    {
        var resource = new CloudResource
        {
            Id = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            Name = "customer-records",
            Type = "storage bucket",
            Tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PubliclyAccessible"] = "true",
                ["DataClassification"] = "confidential",
                ["BusinessCriticality"] = "crown-jewel",
                ["CveId"] = "CVE-2026-1234",
                ["CvssScore"] = "9.2",
                ["Exploitable"] = "true",
                ["VulnerabilitySeverity"] = "Critical"
            }
        };
        var observedAt = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

        var mapped = ResourceSecuritySignalMapper.MapTags(resource, CloudProviderType.Aws, observedAt);

        Assert.Equal(4, mapped.Signals.Count);
        Assert.Contains(mapped.Signals, signal => signal.Type == ResourceSecuritySignalType.PublicExposure && !signal.IsConfirmed);
        Assert.Contains(mapped.Signals, signal => signal.Type == ResourceSecuritySignalType.SensitiveData);
        Assert.Contains(mapped.Signals, signal => signal.Type == ResourceSecuritySignalType.CriticalAsset);
        var vulnerability = Assert.Single(mapped.Signals, signal => signal.Type == ResourceSecuritySignalType.WorkloadVulnerability);
        Assert.Equal(9.2, vulnerability.CvssScore);
        Assert.True(vulnerability.IsExploitable);
        Assert.All(mapped.Signals, signal => Assert.Equal(observedAt, signal.ObservedAtUtc));
        Assert.Equal(Enum.GetValues<ResourceSecuritySignalType>().Length - 1, mapped.Coverage.Count);
        Assert.All(mapped.Coverage, item => Assert.Equal(SecurityEvidenceCoverageStatus.Partial, item.Status));
    }

    [Fact]
    public void MapTags_ShouldNotInventSignalsWhenNoEvidenceTagsExist()
    {
        var mapping = ResourceSecuritySignalMapper.MapTags(new CloudResource { Id = Guid.NewGuid() }, CloudProviderType.Azure);

        Assert.Empty(mapping.Signals);
        Assert.Equal(Enum.GetValues<ResourceSecuritySignalType>().Length - 1, mapping.Coverage.Count);
    }

    [Fact]
    public void MapWorkloadTrust_ShouldEmitConfirmedSignalOnlyForMismatch()
    {
        var resource = new CloudResource { Id = Guid.NewGuid(), AccountId = Guid.NewGuid(), Name = "api", Type = "Kubernetes Deployment" };
        var mismatch = new KubernetesWorkloadTrace(CloudProviderType.Aws, "cluster-a", "prod", "Deployment", "api", "api-sa", "arn:aws:iam::123:role/api",
            new[] { "cluster-admin" }, true, new WorkloadIdentityVerification(WorkloadTrustStatus.Mismatch, "OIDC audience does not match."));
        var verified = mismatch with { Trust = new WorkloadIdentityVerification(WorkloadTrustStatus.Verified, "Issuer, subject, and audience match.") };

        var mismatchMapping = ResourceSecuritySignalMapper.MapWorkloadTrust(mismatch, resource);
        var verifiedMapping = ResourceSecuritySignalMapper.MapWorkloadTrust(verified, resource);

        var signal = Assert.Single(mismatchMapping.Signals);
        Assert.Equal(ResourceSecuritySignalType.WorkloadIdentityRisk, signal.Type);
        Assert.True(signal.IsConfirmed);
        Assert.Equal(SecurityEvidenceCoverageStatus.Complete, Assert.Single(mismatchMapping.Coverage).Status);
        Assert.Empty(verifiedMapping.Signals);
        Assert.Equal(SecurityEvidenceCoverageStatus.Complete, Assert.Single(verifiedMapping.Coverage).Status);
    }
}
