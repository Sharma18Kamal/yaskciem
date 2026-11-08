using System.Text.Json;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;

namespace CloudEntitle.Integration.Services;

public sealed class TrustCredentialMonitoringAnalysisService
{
    public TrustCredentialSnapshot? SelectBaseline(TrustCredentialSnapshot current,
        IReadOnlyList<TrustCredentialSnapshot> candidates)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count > 50)
            throw new ArgumentException("Trust/credential baseline selection exceeds 50 saved observations.");
        return candidates.Where(candidate => IsComparableBaseline(candidate, current))
            .OrderByDescending(candidate => candidate.CollectedAtUtc)
            .ThenByDescending(candidate => candidate.SnapshotId).FirstOrDefault();
    }

    public IReadOnlyList<TrustCredentialChange> Compare(TrustCredentialSnapshot? before, TrustCredentialSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(after);
        if (before is null) return [];
        if (!IsComparableBaseline(before, after))
            throw new ArgumentException("Trust and credential comparison requires nonoverlapping observations from the exact account and source.");
        var old = Index(before);
        var current = Index(after);
        var changes = new List<TrustCredentialChange>();
        foreach (var key in old.Keys.Union(current.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            old.TryGetValue(key, out var previous);
            current.TryGetValue(key, out var next);
            if (previous?.MetadataSha256 == next?.MetadataSha256) continue;
            var kind = next?.Kind ?? previous!.Kind;
            var complete = IsKindComplete(before, kind) && IsKindComplete(after, kind);
            if (next is null && !complete) continue;
            if (changes.Count >= 2_000)
                throw new InvalidOperationException("Trust/credential analysis exceeds 2,000 review signals. Narrow saved evidence; no truncated report was produced.");
            var change = previous is null ? complete ? "Added" : "PotentiallyObservedAddition"
                : next is null ? "NoLongerObserved"
                : complete ? "Changed" : "PotentiallyChanged";
            var evidence = complete
                ? "The same bounded source was completely enumerated in both observations. This is a metadata difference, not a provider-attested change time, verified effective escalation or proof of credential use."
                : "At least one component inventory is incomplete. This is a potential observation only; missing entries do not prove removal or change.";
            changes.Add(new(next?.PrincipalId ?? previous!.PrincipalId, next?.PrincipalName ?? previous!.PrincipalName,
                kind, next?.EvidenceId ?? previous!.EvidenceId, change, previous?.MetadataSha256, next?.MetadataSha256, evidence));
        }
        return changes;
    }

    private static bool IsComparableBaseline(TrustCredentialSnapshot before, TrustCredentialSnapshot after)
        => before.SnapshotId != after.SnapshotId && before.Provider == after.Provider
            && before.AccountId == after.AccountId && before.Source == after.Source
            && before.CollectedAtUtc < after.StartedAtUtc;

    public IReadOnlyList<EffectivePrivilegeEscalationSignal> CompareObservedAssignments(
        CloudProvider provider,
        string accountId,
        DateTimeOffset beforeCompletedAtUtc,
        DateTimeOffset afterCompletedAtUtc,
        bool beforeCoverageComplete,
        bool afterCoverageComplete,
        IReadOnlyList<SharedCiemRecordDto> beforeRecords,
        IReadOnlyList<SharedCiemRecordDto> afterRecords)
    {
        if (provider is not (CloudProvider.Azure or CloudProvider.Aws or CloudProvider.Gcp)
            || string.IsNullOrWhiteSpace(accountId) || beforeCompletedAtUtc >= afterCompletedAtUtc
            || beforeRecords is null || afterRecords is null
            || beforeRecords.Count > 5_000 || afterRecords.Count > 5_000)
            throw new ArgumentException("Privilege comparison requires bounded, chronological scans for one supported account.");
        var before = IndexAssignments(provider, accountId, beforeRecords);
        var after = IndexAssignments(provider, accountId, afterRecords);
        var signals = new List<EffectivePrivilegeEscalationSignal>();
        foreach (var (key, current) in after.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (!current.IsPrivileged) continue;
            var isNew = !before.TryGetValue(key, out var previous);
            var isElevated = isNew || previous?.IsPrivileged != true;
            if (!isElevated) continue;
            if (signals.Count >= 2_000)
                throw new InvalidOperationException("Effective-access comparison exceeds 2,000 potential findings. Narrow the selected scans.");
            var coverage = beforeCoverageComplete && afterCoverageComplete
                ? "Both source scans report complete effective-access collection for the selected account."
                : "At least one source scan has partial or unavailable effective-access coverage.";
            signals.Add(new(current.Principal, current.Role, current.Scope, current.Source, "Potential",
                $"{(isNew ? "A privileged assignment appeared" : "A previously observed assignment changed to privileged")} between the selected scans. {coverage} The scanner records assignment/role observations, not authoritative comparable before/after provider authorization decisions; effective privilege escalation is therefore Potential, not Verified."));
        }
        return signals;
    }

    private static Dictionary<string, TrustCredentialObservation> Index(TrustCredentialSnapshot snapshot)
    {
        if (snapshot.Observations is null || snapshot.Observations.Count > 20_000)
            throw new ArgumentException("Trust/credential snapshot exceeds the observation bound.");
        var result = new Dictionary<string, TrustCredentialObservation>(StringComparer.Ordinal);
        foreach (var item in snapshot.Observations)
        {
            var key = $"{item.Kind}\0{item.PrincipalId}\0{item.EvidenceId}";
            if (!result.TryAdd(key, item))
                throw new ArgumentException("Trust/credential snapshot contains duplicate stable identities.");
        }
        return result;
    }

    private static bool IsKindComplete(TrustCredentialSnapshot snapshot, string kind)
        => kind is "FederatedIdentityCredential" or "IamRoleTrustPolicy"
            or "GcpProjectIamBinding" or "GcpServiceAccountIamBinding"
            ? snapshot.TrustInventoryComplete
            : snapshot.CredentialInventoryComplete;

    private static Dictionary<string, EffectiveAssignment> IndexAssignments(CloudProvider provider, string accountId,
        IReadOnlyList<SharedCiemRecordDto> records)
    {
        var result = new Dictionary<string, EffectiveAssignment>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (record.Kind != "effective-access" || record.Provider != provider
                || !record.ExternalAccountId.Equals(accountId, StringComparison.OrdinalIgnoreCase)) continue;
            using var document = JsonDocument.Parse(record.PayloadJson);
            var payload = document.RootElement;
            var principal = Read(payload, "principal");
            var role = Read(payload, "role");
            var scope = Read(payload, "scope");
            var source = Read(payload, "source");
            var privilegedValue = Read(payload, "isPrivileged");
            if (principal.Length == 0 || role.Length == 0 || scope.Length == 0 || source.Length == 0
                || !bool.TryParse(privilegedValue, out var privileged))
                throw new ArgumentException("Effective-access scan record lacks a stable principal, role, scope, source or privilege classification.");
            var key = $"{principal}\0{role}\0{scope}\0{source}";
            if (!result.TryAdd(key, new(principal, role, scope, source, privileged)))
                throw new ArgumentException("Effective-access scan contains duplicate assignment identities.");
        }
        return result;
    }

    private static string Read(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return "";
        foreach (var item in element.EnumerateObject())
            if (item.Name.Equals(property, StringComparison.OrdinalIgnoreCase))
                return item.Value.ValueKind == JsonValueKind.String ? item.Value.GetString() ?? "" : item.Value.ToString();
        return "";
    }

    private sealed record EffectiveAssignment(string Principal, string Role, string Scope, string Source, bool IsPrivileged);
}
