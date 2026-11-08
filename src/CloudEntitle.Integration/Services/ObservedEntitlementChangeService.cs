using CloudEntitle.Integration.Contracts;

namespace CloudEntitle.Integration.Services;

public sealed class ObservedEntitlementChangeService
{
    public IReadOnlyList<ObservedEntitlementChange> Compare(EntitlementEvidenceSnapshot? before, EntitlementEvidenceSnapshot after)
    {
        if (before is null) return [];
        if (before.Provider != after.Provider || before.AccountId != after.AccountId || before.CollectedAtUtc >= after.StartedAtUtc)
            throw new ArgumentException("Document change comparison requires nonoverlapping prior observations of the exact account.");
        var old = before.Grants.GroupBy(item => (item.Principal, item.SourceId)).ToDictionary(group => group.Key, group => group.ToArray());
        var current = after.Grants.GroupBy(item => (item.Principal, item.SourceId)).ToDictionary(group => group.Key, group => group.ToArray());
        var changes = new List<ObservedEntitlementChange>();
        foreach (var key in old.Keys.Union(current.Keys))
        {
            var prior = old.GetValueOrDefault(key) ?? [];
            var next = current.GetValueOrDefault(key) ?? [];
            var priorDocuments = string.Join(",", prior.Select(item => item.SourceSha256).Distinct().Order());
            var nextDocuments = string.Join(",", next.Select(item => item.SourceSha256).Distinct().Order());
            if (priorDocuments == nextDocuments && prior.Select(Statement).Order().SequenceEqual(next.Select(Statement).Order())) continue;
            if (changes.Count >= 2000) throw new InvalidOperationException("Document analysis exceeds 2,000 review signals. Narrow saved evidence; no truncated successful report was produced.");
            changes.Add(new(key.Principal, key.SourceId, prior.Length == 0 ? "NewlyObservedGrant"
                : next.Length == 0 ? "NoLongerObservedGrant" : "ObservedPolicyOrAssignmentChange", priorDocuments, nextDocuments));
        }
        return changes;
    }
    private static string Statement(ObservedPermissionGrant item)
        => $"{item.Action}|{item.ResourcePattern}|{item.Scope}|{item.Effect}|{item.IsExclusion}|{item.HasCondition}|{item.SourceVersion}|{item.SourcePrincipal}";
}
