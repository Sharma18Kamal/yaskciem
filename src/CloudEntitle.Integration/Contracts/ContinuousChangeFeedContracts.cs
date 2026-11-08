using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CloudEntitle.Domain;

namespace CloudEntitle.Integration.Contracts;

/// <summary>Explicit opt-in for one server-configured bridge alias. ResetSource starts a new epoch, retaining old evidence.</summary>
public sealed record ConfigureContinuousChangeFeedRequest(bool Enabled, bool Connected, bool ResetSource = false);

/// <summary>Coverage claimed by a bridge, not independently verified provider completeness.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChangeFeedCategoryCoverage(string Category, string State, bool SourceReportedComplete,
    DateTimeOffset? CoveredFromUtc, DateTimeOffset? CoveredThroughUtc, string? GapCode)
{
    public bool VerifiedComplete => false;
}

/// <summary>Normalized immutable change metadata. Credential values, policy bodies and raw events are forbidden.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NormalizedCloudChange(string SourceEventId, string RevisionId, string Category, string Operation,
    string PrincipalId, string ResourceId, DateTimeOffset SourceEventAtUtc, DateTimeOffset ProviderObservedAtUtc,
    string? BeforeSha256, string? AfterSha256, string? CredentialMetadataId = null,
    DateTimeOffset? ValidFromUtc = null, DateTimeOffset? ValidUntilUtc = null);

/// <summary>A bounded bridge response whose evidence, receipt and cursor are committed in one transaction.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ContinuousChangeBatch(Guid BatchId, Guid SourceEpoch, string SourceId, CloudProvider Provider,
    string AccountId, string? PreviousCursor, string Cursor, DateTimeOffset SourceWatermarkUtc,
    IReadOnlyList<ChangeFeedCategoryCoverage> Coverage, IReadOnlyList<NormalizedCloudChange> Changes);

/// <summary>Receipt for exact replay; accepted count excludes overlapping revisions already committed.</summary>
public sealed record ContinuousChangeReceipt(Guid BatchId, Guid SourceEpoch, string Cursor, int AcceptedCount,
    DateTimeOffset ReceivedAtUtc);

/// <summary>Safe server feed status. No endpoint, authentication secret or transport payload is exposed.</summary>
public sealed record ContinuousChangeFeedStatus(string Alias, CloudProvider Provider, string AccountId, string SourceId,
    Guid SourceEpoch, bool Enabled, bool Connected, string? Cursor, string Health, int ConsecutiveFailures,
    DateTimeOffset? LastSuccessAtUtc, DateTimeOffset? NextPollAtUtc, string? FailureCode,
    IReadOnlyList<ChangeFeedCategoryCoverage> Coverage, DateTimeOffset? SourceWatermarkUtc = null);

/// <summary>One stored normalized revision, including source identity, epoch and server receipt time.</summary>
public sealed record StoredContinuousChange(string Alias, string SourceId, Guid SourceEpoch,
    DateTimeOffset ReceivedAtUtc, NormalizedCloudChange Change);

public static partial class ContinuousChangeFeedValidation
{
    public const int MaximumBatchBytes = 2 * 1024 * 1024;
    public const int MaximumChanges = 500;
    public static IReadOnlyList<string> Categories { get; } =
        ["IamPolicy", "RoleAssignment", "WorkloadTrust", "ServiceAccountPermission", "CredentialMetadata", "ServiceAccountKeyMetadata"];

    public static IReadOnlyList<ChangeFeedCategoryCoverage> UnknownCoverage()
        => Categories.Select(category => new ChangeFeedCategoryCoverage(category, "Unknown", false, null, null, "not_validated")).ToArray();

    public static bool SafeId(string? value, int maximum = 256)
        => value is { Length: > 0 } && value.Length <= maximum && Identifier().IsMatch(value);

    public static bool SafeMetadataId(string? value, int maximum = 256)
        => SafeId(value, maximum)
            && !value!.Contains('=')
            && !value.StartsWith("eyJ", StringComparison.Ordinal)
            && !value.StartsWith("AKIA", StringComparison.Ordinal)
            && !value.StartsWith("ASIA", StringComparison.Ordinal)
            && !value.StartsWith("ya29.", StringComparison.Ordinal)
            && !value.StartsWith("ghp_", StringComparison.Ordinal)
            && !value.Contains("PRIVATEKEY", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("secretText", StringComparison.OrdinalIgnoreCase);

    public static void Validate(ContinuousChangeBatch batch, ContinuousChangeFeedStatus scope)
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(1);
        if (batch is null || batch.BatchId == Guid.Empty || batch.SourceEpoch != scope.SourceEpoch
            || batch.SourceId != scope.SourceId || batch.Provider != scope.Provider || batch.AccountId != scope.AccountId
            || !SafeId(batch.SourceId, 128) || !SafeId(batch.Cursor, 512)
            || batch.PreviousCursor is not null && !SafeId(batch.PreviousCursor, 512)
            || batch.Cursor == batch.PreviousCursor
            || batch.SourceWatermarkUtc < DateTimeOffset.UnixEpoch || batch.SourceWatermarkUtc > now
            || batch.Coverage is null || batch.Coverage.Count != Categories.Count
            || batch.Changes is null || batch.Changes.Count > MaximumChanges)
            throw new ArgumentException("Invalid normalized change batch scope or bounds.");
        if (!batch.Coverage.Select(c => c.Category).Order().SequenceEqual(Categories.Order()))
            throw new ArgumentException("Every supported category must have explicit coverage, including unsupported categories.");
        foreach (var coverage in batch.Coverage)
        {
            if (coverage.State is not ("Unknown" or "Current" or "Backfill" or "Gap" or "Unavailable")
                || coverage.SourceReportedComplete && coverage.State != "Current"
                || coverage.GapCode is not null && !SafeId(coverage.GapCode, 64)
                || coverage.State is "Gap" or "Unavailable" && coverage.GapCode is null
                || coverage.CoveredFromUtc.HasValue != coverage.CoveredThroughUtc.HasValue
                || coverage.CoveredFromUtc < DateTimeOffset.UnixEpoch
                || coverage.CoveredThroughUtc > batch.SourceWatermarkUtc
                || coverage.CoveredThroughUtc < coverage.CoveredFromUtc
                || coverage.SourceReportedComplete && coverage.CoveredFromUtc is null)
                throw new ArgumentException("Invalid coverage interval or gap state.");
        }
        var revisions = new HashSet<(string, string)>();
        foreach (var change in batch.Changes)
        {
            if (change is null || !SafeMetadataId(change.SourceEventId) || !SafeMetadataId(change.RevisionId, 128)
                || !Categories.Contains(change.Category) || change.Operation is not ("Added" or "Changed" or "Removed")
                || !SafeMetadataId(change.PrincipalId) || !SafeMetadataId(change.ResourceId)
                || change.CredentialMetadataId is not null && !SafeMetadataId(change.CredentialMetadataId, 128)
                || change.SourceEventAtUtc < DateTimeOffset.UnixEpoch || change.SourceEventAtUtc > now
                || change.ProviderObservedAtUtc < change.SourceEventAtUtc || change.ProviderObservedAtUtc > now
                || change.ProviderObservedAtUtc > batch.SourceWatermarkUtc
                || !Digest(change.BeforeSha256) || !Digest(change.AfterSha256)
                || change.Operation == "Added" && (change.BeforeSha256 is not null || change.AfterSha256 is null)
                || change.Operation == "Removed" && (change.AfterSha256 is not null || change.BeforeSha256 is null)
                || change.Operation == "Changed" && (change.BeforeSha256 is null || change.AfterSha256 is null)
                || change.ValidFromUtc < DateTimeOffset.UnixEpoch || change.ValidUntilUtc < change.ValidFromUtc
                || !revisions.Add((change.SourceEventId, change.RevisionId)))
                throw new ArgumentException("Invalid safe change metadata or duplicate revision.");
        }
    }

    private static bool Digest(string? value) => value is null || value.Length == 64 && value.All(Uri.IsHexDigit);
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:@/+=,-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
}
