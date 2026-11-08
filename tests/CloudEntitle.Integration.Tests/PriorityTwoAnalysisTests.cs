using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.Integration.Services;
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed class PriorityTwoAnalysisTests
{
    [Fact]
    public void Behavior_RequiresPriorPrincipalBaselineAndExactConfiguredActionAndSourceCountry()
    {
        var now = new DateTimeOffset(2026, 11, 8, 3, 0, 0, TimeSpan.Zero);
        var history = Enumerable.Range(0, 20).Select(index => new CloudActivityEvent($"old-{index}", "user", "read", null, null,
            now.AddDays(-20).AddHours(index), true) { CountryCode = "IN" }).ToArray();
        var current = Snapshot(now, [new("first", "user", "iam.amazonaws.com/CreateUser", null, null, now, true) { CountryCode = "US" }]);
        var policy = new IdentityBehaviorPolicy(["iam.amazonaws.com/CreateUser"], "UTC");
        var baseline = Snapshot(now.AddDays(-1), history);
        var anomalies = new IdentityBehaviorAnalysisService().Analyze(current, [baseline], policy);
        Assert.Equal(3, anomalies.Count);
        Assert.Contains(anomalies, item => item.Kind == "FirstObservedPrivilegedAction");
        Assert.Contains(anomalies, item => item.Kind == "UnusualObservedCountry");
        Assert.Contains(anomalies, item => item.Kind == "PrivilegedActionOutsideBusinessHours");
        Assert.All(anomalies, item => Assert.Contains("Potential review", item.Evidence));
        Assert.Single(new IdentityBehaviorAnalysisService().Analyze(current, [], policy));
        Assert.DoesNotContain(new IdentityBehaviorAnalysisService().Analyze(current, [baseline], policy with { PrivilegedActions = [] }),
            item => item.Kind != "UnusualObservedCountry");
        var unknown = current with { Events = [current.Events[0] with { CountryCode = null, OutcomeKnown = false, Success = false }] };
        Assert.Empty(new IdentityBehaviorAnalysisService().Analyze(unknown, [baseline], policy));
        var other = current with { Events = [current.Events[0] with { Principal = "other" }] };
        Assert.Single(new IdentityBehaviorAnalysisService().Analyze(other, [baseline], policy));
        Assert.Throws<ArgumentException>(() => new IdentityBehaviorAnalysisService().Analyze(current, [baseline with { AccountId = "foreign" }], policy));
        Assert.Throws<ArgumentException>(() => new IdentityBehaviorAnalysisService().Analyze(current, [baseline with { ThroughUtc = now.AddSeconds(1) }], policy));
    }

    [Fact]
    public void DocumentChanges_AreStatementSensitiveNonoverlappingAndNeverAssertDeletionOrEscalation()
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        var grant = new ObservedPermissionGrant("principal", "group", "role", "policy", "v1", new string('A', 64), "Read",
            "scope", "read", "*", "Allow", false, false, false);
        var before = new EntitlementEvidenceSnapshot(Guid.NewGuid(), CloudProvider.Aws, "123456789012", now.AddMinutes(-3), now.AddMinutes(-2), [grant], ["Partial"]);
        var after = before with { SnapshotId = Guid.NewGuid(), StartedAtUtc = now, CollectedAtUtc = now.AddSeconds(1), Grants = [grant with { HasCondition = true }] };
        var service = new ObservedEntitlementChangeService();
        Assert.Equal("ObservedPolicyOrAssignmentChange", Assert.Single(service.Compare(before, after)).Kind);
        Assert.Equal("NoLongerObservedGrant", Assert.Single(service.Compare(before, after with { Grants = [] })).Kind);
        Assert.Empty(service.Compare(null, after));
        Assert.Throws<ArgumentException>(() => service.Compare(before, after with { Provider = CloudProvider.Gcp }));
        Assert.Throws<ArgumentException>(() => service.Compare(before, after with { StartedAtUtc = before.CollectedAtUtc }));
    }
    private static CloudActivitySnapshot Snapshot(DateTimeOffset now, IReadOnlyList<CloudActivityEvent> events)
        => new(Guid.NewGuid(), CloudProvider.Aws, "123456789012", now.AddDays(-90), now, "audit", "Partial", false, events);

    [Fact]
    public void Behavior_PermissionChangeBurstCountsDistinctCallsAndSuppressesUnattributedActors()
    {
        var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        var old = Enumerable.Range(0, 20).Select(index => new CloudActivityEvent($"old-{index}", "user", "read", null, null,
            now.AddDays(-20).AddHours(index), true)).ToArray();
        var recent = Enumerable.Range(0, 5).Select(index => new CloudActivityEvent($"change-{index}", "user", "change", null, null,
            now.AddMinutes(-5 + index), true)).ToArray();
        var current = Snapshot(now, recent.Concat(recent).ToArray());
        var policy = new IdentityBehaviorPolicy([], "UTC") { PermissionChangeActions = ["change"] };
        var result = new IdentityBehaviorAnalysisService().Analyze(current, [Snapshot(now.AddDays(-1), old)], policy);
        Assert.Equal("ObservedPermissionChangeBurst", Assert.Single(result).Kind);
        var unknown = current with { Events = recent.Select(item => item with { Principal = "Unattributed" }).ToArray() };
        Assert.Empty(new IdentityBehaviorAnalysisService().Analyze(unknown, [], policy with { PrivilegedActions = ["change"] }));
    }
}
