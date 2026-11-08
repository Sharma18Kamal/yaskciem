using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;

namespace CloudEntitle.Integration.Services;

public sealed class DecisionEscalationAnalysisService
{
    public DecisionEscalationReport Analyze(EffectiveDecisionSource source, EffectiveDecisionSnapshot before,
        EffectiveDecisionSnapshot after, bool storedProvenanceVerified, DateTimeOffset now)
    {
        var blockers = new HashSet<string>(StringComparer.Ordinal);
        if (!storedProvenanceVerified) blockers.Add("native_stored_provenance_not_verified");
        if (!source.Enabled || source.Provider != CloudProvider.Gcp) blockers.Add("provider_source_unsupported");
        if (before.SnapshotId == after.SnapshotId || before.CompletedAtUtc >= after.StartedAtUtc
            || before.StartedAtUtc > before.CompletedAtUtc || after.StartedAtUtc > after.CompletedAtUtc)
            blockers.Add("observation_intervals_overlap_or_unordered");
        if (before.StartedAtUtc < DateTimeOffset.UnixEpoch || after.CompletedAtUtc > now
            || now - after.CompletedAtUtc > TimeSpan.FromMinutes(5)
            || after.CompletedAtUtc - before.StartedAtUtc > TimeSpan.FromHours(24)
            || before.CompletedAtUtc - before.StartedAtUtc > TimeSpan.FromSeconds(30)
            || after.CompletedAtUtc - after.StartedAtUtc > TimeSpan.FromSeconds(30))
            blockers.Add("observations_not_fresh_or_bounded");
        foreach (var item in new[] { before, after })
        {
            if (item.Provider != source.Provider || item.SourceId != source.SourceId
                || item.AccountId != source.AccountId || item.CollectorIdentity != source.CollectorIdentity
                || item.ConfigurationSha256 != source.ConfigurationSha256)
                blockers.Add("configured_source_identity_or_revision_mismatch");
            if (item.VerificationProfile != GcpEffectiveDecisionEvaluator.VerificationProfile
                || item.Coverage != "CompleteVerified" || item.Blockers.Count > 0
                || item.Decision is not ("GRANTED" or "NOT_GRANTED"))
                blockers.Add("provider_evaluation_unknown_or_partial");
            if (item.ContextObservedAtUtc < item.StartedAtUtc || item.ContextObservedAtUtc > item.CompletedAtUtc)
                blockers.Add("context_not_observed_during_native_read");
            if (!source.PrivilegedPermissions.Contains(item.Permission, StringComparer.Ordinal)
                || !source.Principals.Contains(item.Principal, StringComparer.Ordinal)
                || item.FullResourceName != source.FullResourceName)
                blockers.Add("tuple_outside_configured_privileged_sample");
            if (item.PolicyVersions.Count == 0 || item.ResponseSha256.Length != 64 || item.ContextSha256.Length != 64)
                blockers.Add("native_policy_versions_or_hashes_missing");
        }
        if (before.Principal != after.Principal || before.Permission != after.Permission
            || before.FullResourceName != after.FullResourceName || before.ContextSha256 != after.ContextSha256)
            blockers.Add("principal_permission_resource_or_full_context_mismatch");
        if (before.PolicyVersions.SequenceEqual(after.PolicyVersions, StringComparer.Ordinal))
            blockers.Add("provider_policy_versions_unchanged");
        var transition = before.Decision == "NOT_GRANTED" && after.Decision == "GRANTED";
        var status = blockers.Count == 0 && transition ? "ConfirmedDecisionEscalation"
            : source.Provider != CloudProvider.Gcp ? "Unsupported"
            : transition ? "Potential" : blockers.Count > 0 ? "Inconclusive" : "NoEscalation";
        return new(Guid.NewGuid(), before.SnapshotId, after.SnapshotId, status, after.Principal, after.Permission,
            after.FullResourceName, after.ContextSha256, now, blockers.Order(StringComparer.Ordinal).ToArray(),
            status == "ConfirmedDecisionEscalation"
                ? "The provider changed NOT_GRANTED to GRANTED for this exact unconditional IAM tuple and captured context. This is not proof of service execution, abuse, all-action escalation, or historical access between observations."
                : "Confirmation requires two complete server-collected native decisions for the identical configured tuple/context, changed policy versions and nonoverlapping fresh observations. Policy keywords, simulation and activity bursts cannot satisfy this gate.");
    }
}
