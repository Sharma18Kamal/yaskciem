using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed class SharedMonitoringProjectionTests
{
    [Fact]
    public void EffectiveAccess_ShouldIgnoreIdsAndActivityTimestampsButDetectPrivilegeChanges()
    {
        var first = Record("effective-access", """{"Principal":"user","Role":"Operator","Scope":"/","Source":"IAM","IsPrivileged":false,"AssignmentType":"Assigned","LastUsedAt":"2026-01-01"}""");
        var second = Record("effective-access", """{"principal":"user","role":"Operator","scope":"/","source":"IAM","isPrivileged":false,"assignmentType":"Assigned","lastUsedAt":"2026-02-01"}""");
        Assert.Equal(Assert.Single(SharedMonitoringProjection.Project(new[] { first }, first.Kind)),
            Assert.Single(SharedMonitoringProjection.Project(new[] { second }, second.Kind)));
        var elevated = second with { PayloadJson = second.PayloadJson.Replace("\"isPrivileged\":false", "\"isPrivileged\":true") };
        var change = Assert.Single(SharedMonitoringProjection.Project(new[] { elevated }, elevated.Kind));
        Assert.Equal(SharedAlertSeverity.High, change.Severity);
        Assert.NotEqual(Assert.Single(SharedMonitoringProjection.Project(new[] { first }, first.Kind)).StateHash, change.StateHash);
    }

    [Fact]
    public void ResourceFindings_ShouldUseFingerprintAndDetectLifecycleChanges()
    {
        var first = Record("resource-finding", """{"RuleId":"rule","Title":"Risk","Fingerprint":"stable-resource-fingerprint","Severity":0,"Status":0,"CreatedAt":"2026-01-01"}""");
        var second = Record("resource-finding", """{"ruleId":"rule","title":"Risk","fingerprint":"stable-resource-fingerprint","severity":0,"status":1,"createdAt":"2026-02-01"}""");
        var before = Assert.Single(SharedMonitoringProjection.Project(new[] { first }, first.Kind));
        var after = Assert.Single(SharedMonitoringProjection.Project(new[] { second }, second.Kind));
        Assert.Equal(before.Key, after.Key);
        Assert.NotEqual(before.StateHash, after.StateHash);
        Assert.Equal(SharedAlertSeverity.Critical, after.Severity);
    }

    [Fact]
    public void Projection_ShouldRejectAmbiguousOrMalformedState()
    {
        Assert.Throws<ArgumentException>(() => SharedMonitoringProjection.Project(new[] { Record("finding", "[]") }, "finding"));
        var first = Record("finding", """{"ruleId":"rule","title":"Risk","severity":"High"}""");
        var second = Record("finding", """{"ruleId":"rule","title":"Risk","severity":"Critical"}""");
        Assert.Throws<ArgumentException>(() => SharedMonitoringProjection.Project(new[] { first, second }, "finding"));
        Assert.Throws<ArgumentException>(() => SharedMonitoringProjection.Project(new[] { first with { PayloadJson = """{"ruleId":"rule","title":"Risk","severity":"not-a-severity"}""" } }, "finding"));
    }

    private static SharedCiemRecordDto Record(string kind, string payload)
        => new(CloudProvider.Aws, kind, Guid.NewGuid().ToString(), "account", payload);
}
