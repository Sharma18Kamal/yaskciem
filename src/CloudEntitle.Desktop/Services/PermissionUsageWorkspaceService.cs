using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudEntitle.Analytics.Models;
using CloudEntitle.Analytics.Services;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.EffectiveAccess.Models;
using CloudEntitle.Infrastructure.Persistence;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.Integration.Services;
using CloudEntitle.Normalization.Models;
using CloudEntitle.Recommendations.Abstractions;
using CloudEntitle.Recommendations.Models;
using CloudEntitle.RiskEngine.Abstractions;
using CloudEntitle.RiskEngine.Models;
using Microsoft.EntityFrameworkCore;
using StoredActivityEvent = CloudEntitle.Domain.Entities.CloudActivityEvent;
using StoredUsageRecord = CloudEntitle.Domain.Entities.PermissionUsageRecord;
using ProviderActivityEvent = CloudEntitle.Integration.Contracts.CloudActivityEvent;

namespace CloudEntitle.Desktop.Services;

public sealed record PermissionUsageWorkspaceData(
    CloudAccount Account,
    IReadOnlyList<CloudPrincipal> Principals,
    IReadOnlyList<CloudResource> Resources,
    IReadOnlyList<StoredActivityEvent> Events,
    IReadOnlyList<PermissionObservationWindow> ObservationWindows,
    PermissionUsageReport Usage,
    IReadOnlyList<PermissionUsageTrend> Trends,
    IReadOnlyList<UsageAnalyticsFindingRow> Findings,
    IReadOnlyList<UsageAnalyticsRecommendationRow> Recommendations,
    string SourceNotice);

public interface IPermissionUsageWorkspaceService
{
    Task<IReadOnlyList<CloudAccount>> GetAccountsAsync(CancellationToken token);
    Task<CloudActivitySnapshot> CollectAndSaveAsync(Guid cloudAccountId, CloudActivitySource source,
        string region, string profile, string destination, string container, string prefix, CancellationToken token);
    Task<PermissionUsageWorkspaceData> AnalyzeAsync(Guid cloudAccountId, int observationDays, CancellationToken token);
}

public sealed class PermissionUsageWorkspaceService(
    IDbContextFactory<CloudEntitleDbContext> contextFactory,
    ICloudActivityCollector activityCollector,
    PermissionUsageAnalyticsService usageAnalytics,
    PermissionUsageTrendService trendAnalytics,
    ProviderActionPermissionMapper actionMapper,
    IRiskEngine riskEngine,
    IRecommendationEngine recommendationEngine) : IPermissionUsageWorkspaceService
{
    private const int MaximumSnapshotEvents = 20_000;
    private const int MaximumAnalyticsEvents = 100_000;
    private const int MaximumObservationWindows = 10_000;
    private const string UsageRiskRuleId = "CIEM-USAGE-001";
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new() { MaxDepth = 8 };

    public async Task<IReadOnlyList<CloudAccount>> GetAccountsAsync(CancellationToken token)
    {
        await using var db = await contextFactory.CreateDbContextAsync(token);
        return await db.CloudAccounts.AsNoTracking().Where(item => item.CloudProvider != CloudProviderType.Kubernetes)
            .OrderBy(item => item.Name).ToArrayAsync(token);
    }

    public async Task<CloudActivitySnapshot> CollectAndSaveAsync(Guid cloudAccountId, CloudActivitySource source,
        string region, string profile, string destination, string container, string prefix, CancellationToken token)
    {
        await using var db = await contextFactory.CreateDbContextAsync(token);
        var account = await db.CloudAccounts.AsNoTracking().SingleAsync(item => item.Id == cloudAccountId, token);
        if (account.ExternalAccountId?.StartsWith("demo-", StringComparison.OrdinalIgnoreCase) == true)
            throw new InvalidOperationException("Synthetic Demo Mode accounts are not live collection targets.");
        var provider = ToProvider(account.CloudProvider);
        var externalAccountId = provider switch
        {
            CloudProvider.Azure => account.SubscriptionId ?? account.ExternalAccountId,
            CloudProvider.Gcp => account.ProjectId ?? account.ExternalAccountId,
            _ => account.ExternalAccountId
        };
        if (string.IsNullOrWhiteSpace(externalAccountId))
            throw new InvalidOperationException("The selected saved account has no provider account identifier.");
        var query = new CloudActivityQuery(provider, externalAccountId, region.Trim(), profile.Trim(), DateTimeOffset.UtcNow)
        {
            SourceKind = source,
            Destination = destination.Trim(),
            Container = container.Trim(),
            Prefix = prefix.Trim()
        };
        CloudActivityCollector.Validate(query);
        var snapshot = await activityCollector.CollectAsync(query, token);
        token.ThrowIfCancellationRequested();
        if (snapshot.Events.Count > MaximumSnapshotEvents || snapshot.Provider != provider
            || !string.Equals(snapshot.AccountId, externalAccountId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Activity snapshot exceeds bounds or does not match the selected account.");
        var principals = await db.CloudPrincipals.AsNoTracking().Where(item => item.AccountId == account.Id).ToArrayAsync(token);
        var resources = await db.CloudResources.AsNoTracking().Where(item => item.AccountId == account.Id).ToArrayAsync(token);
        var converted = snapshot.Events.Select(item => ConvertEvent(account, snapshot, item, principals, resources)).ToArray();
        var byKey = converted.GroupBy(item => item.DeduplicationKey, StringComparer.Ordinal).Select(group =>
        {
            var first = group.First();
            if (group.Any(item => item.EventTimestamp != first.EventTimestamp || item.ProviderIdentity != first.ProviderIdentity
                || item.Outcome != first.Outcome || item.Action != first.Action))
                throw new InvalidOperationException("Repeated provider event identifiers carry conflicting evidence; no snapshot was stored.");
            return first;
        }).ToArray();

        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var added = 0;
        foreach (var batch in byKey.Chunk(400))
        {
            var keys = batch.Select(item => item.DeduplicationKey).ToArray();
            var existing = (await db.CloudActivityEvents.AsNoTracking()
                .Where(item => item.CloudAccountId == account.Id && keys.Contains(item.DeduplicationKey))
                .Select(item => new { item.DeduplicationKey, item.EventTimestamp, item.ProviderIdentity, item.Action, item.Outcome })
                .ToArrayAsync(token)).ToDictionary(item => item.DeduplicationKey, StringComparer.Ordinal);
            foreach (var item in batch)
            {
                if (existing.TryGetValue(item.DeduplicationKey, out var saved))
                {
                    if (saved.EventTimestamp != item.EventTimestamp || saved.ProviderIdentity != item.ProviderIdentity
                        || saved.Action != item.Action || saved.Outcome != item.Outcome)
                        throw new InvalidOperationException("A previously stored provider event identifier conflicts with new evidence.");
                    continue;
                }
                db.CloudActivityEvents.Add(item);
                added++;
            }
        }
        var hasWindow = await db.PermissionObservationWindows.AnyAsync(item => item.SnapshotId == snapshot.SnapshotId, token);
        if (!hasWindow)
        {
            db.PermissionObservationWindows.Add(new PermissionObservationWindow
            {
                Id = StableGuid($"observation|{account.Id:D}|{snapshot.SnapshotId:D}"),
                SnapshotId = snapshot.SnapshotId,
                CloudAccountId = account.Id,
                Provider = provider,
                ObservedFromUtc = snapshot.FromUtc.ToUniversalTime(),
                ObservedThroughUtc = snapshot.ThroughUtc.ToUniversalTime(),
                Source = snapshot.Source,
                Status = ActivityCoverageStatus.Partial.ToString(),
                IncludesActions = snapshot.Events.Count > 0 && snapshot.Events.All(item => !string.IsNullOrWhiteSpace(item.PermissionAction)),
                IncludesResourceIds = snapshot.Events.Count > 0 && snapshot.Events.All(item => !string.IsNullOrWhiteSpace(item.Resource)),
                IncludesRoleIds = false,
                Truncated = snapshot.Truncated,
                RecordCount = snapshot.Events.Count,
                Details = Bound(snapshot.CoverageDetails + (snapshot.Truncated ? " Source result was truncated." : ""), 4000),
                Provenance = JsonSerializer.Serialize(snapshot.Provenance, EvidenceJsonOptions),
                CollectedAtUtc = snapshot.CollectedAtUtc.ToUniversalTime()
            });
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return snapshot with
        {
            CoverageDetails = $"{snapshot.CoverageDetails} {added} new normalized events stored; {converted.Length - added} duplicates skipped. Coverage remains partial."
        };
    }

    public async Task<PermissionUsageWorkspaceData> AnalyzeAsync(Guid cloudAccountId, int observationDays, CancellationToken token)
    {
        if (observationDays is not (30 or 60 or 90))
            throw new ArgumentOutOfRangeException(nameof(observationDays), "Choose a 30, 60 or 90-day analysis window.");
        await using var db = await contextFactory.CreateDbContextAsync(token);
        var account = await db.CloudAccounts.AsNoTracking().SingleAsync(item => item.Id == cloudAccountId, token);
        var provider = ToProvider(account.CloudProvider);
        var principals = await db.CloudPrincipals.AsNoTracking().Where(item => item.AccountId == cloudAccountId).ToArrayAsync(token);
        var resources = await db.CloudResources.AsNoTracking().Where(item => item.AccountId == cloudAccountId).ToArrayAsync(token);
        var roles = await db.CloudRoles.AsNoTracking().Where(item => item.AccountId == cloudAccountId).ToArrayAsync(token);
        var policies = await db.CloudPolicies.AsNoTracking().Where(item => item.AccountId == cloudAccountId).ToArrayAsync(token);
        var permissions = await db.CloudPermissions.AsNoTracking().Where(item => item.AccountId == cloudAccountId).ToArrayAsync(token);
        var access = await db.EffectiveAccesses.AsNoTracking().Where(item => item.CloudAccountId == cloudAccountId).ToArrayAsync(token);
        var entitlements = await db.Entitlements.AsNoTracking().Where(item => item.CloudAccountId == cloudAccountId).ToArrayAsync(token);
        var paths = await db.AccessPaths.AsNoTracking().Where(item => item.CloudAccountId == cloudAccountId).ToArrayAsync(token);
        var windows = await db.PermissionObservationWindows.AsNoTracking().Where(item => item.CloudAccountId == cloudAccountId)
            .Take(MaximumObservationWindows + 1).ToArrayAsync(token);
        if (windows.Length > MaximumObservationWindows)
            throw new InvalidOperationException("Saved activity has more than 10,000 observation windows. Archive old evidence before analysis.");
        windows = windows.OrderByDescending(item => item.ObservedThroughUtc).ToArray();
        var storedEvents = await db.CloudActivityEvents.AsNoTracking().Where(item => item.CloudAccountId == cloudAccountId)
            .Take(MaximumAnalyticsEvents + 1).ToArrayAsync(token);
        if (storedEvents.Length > MaximumAnalyticsEvents)
            throw new InvalidOperationException("Saved activity exceeds the 100,000-event analysis limit. Narrow or archive retained evidence before analysis.");
        storedEvents = storedEvents.OrderByDescending(item => item.EventTimestamp).ToArray();
        var legacyActivities = await db.ActivityRecords.AsNoTracking().Where(item => item.CloudAccountId == cloudAccountId
            && (item.Source == null || item.Source != "Synthetic Demo Mode")).ToArrayAsync(token);
        var activities = legacyActivities.Concat(storedEvents.Where(item => item.IdentityId.HasValue).Select(ToActivityRecord))
            .DistinctBy(item => item.Id).ToArray();
        var coverage = windows.Select(item => new ActivityEvidenceCoverage(cloudAccountId, provider,
            item.ObservedFromUtc.UtcDateTime, item.ObservedThroughUtc.UtcDateTime,
            Enum.TryParse<ActivityCoverageStatus>(item.Status, out var status) ? status : ActivityCoverageStatus.Unknown,
            item.IncludesActions, item.IncludesResourceIds, item.IncludesRoleIds, item.RecordCount, item.Details)).ToArray();
        var now = DateTime.UtcNow;
        var report = usageAnalytics.Calculate(access, permissions, activities, coverage, now);
        var trends = trendAnalytics.Calculate(storedEvents);
        var normalizedEntitlements = entitlements.Select(item => Normalize(item, account, permissions, now)).ToArray();
        var riskResult = await riskEngine.EvaluateAsync(new RiskEvaluationRequest(
            new[] { account }, principals, resources, roles, policies, permissions, normalizedEntitlements,
            access, paths, activities, EvaluatedAt: now,
            Options: new RiskRuleOptions(UsageObservationDays: observationDays,
                EnabledRules: new HashSet<string>(StringComparer.Ordinal) { UsageRiskRuleId }),
            ActivityCoverage: coverage), token);
        var recommendations = await recommendationEngine.EvaluateAsync(new RecommendationContext(
            riskResult.Findings, access, paths, normalizedEntitlements, principals, resources, permissions,
            activities, now, coverage, observationDays), token);

        await PersistAnalysisAsync(db, account, provider, report, trends, riskResult.Findings,
            recommendations.Recommendations, now, token);
        var identityById = principals.ToDictionary(item => item.Id);
        var resourceById = resources.ToDictionary(item => item.Id);
        var findingRows = riskResult.Findings.Select(item => new UsageAnalyticsFindingRow(
            item.Id, item.CloudAccountId, item.CloudProvider, item.PrincipalId,
            identityById.GetValueOrDefault(item.PrincipalId)?.Name ?? item.PrincipalId.ToString("D"),
            item.PermissionId ?? Guid.Empty,
            permissions.FirstOrDefault(permission => permission.Id == item.PermissionId)?.Name ?? "Unknown permission",
            item.ResourceId, item.ResourceId.HasValue ? resourceById.GetValueOrDefault(item.ResourceId.Value)?.Name : null,
            item.PriorityScore, item.Severity, observationDays, UsageConfidenceLevel.Medium, item.Evidence.Summary,
            new DateTimeOffset(item.DetectedAt, TimeSpan.Zero))).ToArray();
        var recommendationRows = recommendations.Recommendations.Select(item => new UsageAnalyticsRecommendationRow(
            item.Id, item.CloudAccountId, item.Provider, item.RecommendationType.ToString(), item.Title,
            item.EvaluationState.ToString(), item.Reason, item.Evidence ?? string.Empty, false)).ToArray();
        var notice = account.ExternalAccountId?.StartsWith("demo-", StringComparison.OrdinalIgnoreCase) == true
            ? "Synthetic Demo Mode evidence; not live cloud telemetry."
            : windows.Length == 0 ? "No saved cloud activity observation exists. No permission is classified as unused."
            : "Provider collection is partial unless a full, action-attributed observation window is explicitly recorded. No permission is automatically removed.";
        return new(account, principals, resources, storedEvents, windows, report, trends, findingRows, recommendationRows, notice);
    }

    private async Task PersistAnalysisAsync(CloudEntitleDbContext db, CloudAccount account, CloudProvider provider,
        PermissionUsageReport report, IReadOnlyList<PermissionUsageTrend> trends,
        IReadOnlyList<RiskFindingModel> findings, IReadOnlyList<RecommendationModel> recommendations,
        DateTime evaluatedAt, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var existingRecords = await db.PermissionUsageRecords.Where(item => item.CloudAccountId == account.Id)
            .ToDictionaryAsync(item => item.Id, token);
        var currentRecordIds = new HashSet<Guid>();
        foreach (var usage in report.Permissions)
        {
            var windows = usage.Windows.ToDictionary(item => item.Days);
            var record = new StoredUsageRecord
            {
                Id = StableGuid($"usage|{account.Id:D}|{usage.PrincipalId:D}|{usage.PermissionId:D}|{usage.ResourceId:D}"),
                CloudAccountId = account.Id, Provider = provider, IdentityId = usage.PrincipalId,
                PermissionId = usage.PermissionId, ResourceId = usage.ResourceId, EffectiveAccessId = usage.EffectiveAccessId,
                EffectiveAccessIds = JsonSerializer.Serialize(usage.EffectiveAccessIds),
                Action = Bound(usage.PermissionAction ?? usage.Permission, 500),
                State30Days = windows[30].EvidenceState, State60Days = windows[60].EvidenceState, State90Days = windows[90].EvidenceState,
                FirstObservedAtUtc = usage.FirstUsedAtUtc.HasValue ? new DateTimeOffset(usage.FirstUsedAtUtc.Value, TimeSpan.Zero) : null,
                LastObservedAtUtc = usage.LastUsedAtUtc.HasValue ? new DateTimeOffset(usage.LastUsedAtUtc.Value, TimeSpan.Zero) : null,
                SuccessfulActions30Days = windows[30].SuccessfulActionCount,
                SuccessfulActions60Days = windows[60].SuccessfulActionCount,
                SuccessfulActions90Days = windows[90].SuccessfulActionCount,
                CorrelationStatus = usage.CorrelationStatus.Contains("does not identify which role", StringComparison.OrdinalIgnoreCase)
                    || usage.CorrelationStatus.Contains("may be unresolved", StringComparison.OrdinalIgnoreCase)
                    ? "GrantingPathUnresolved"
                    : "GrantingPathCorrelated",
                CalculatedAtUtc = new DateTimeOffset(evaluatedAt, TimeSpan.Zero),
                Evidence = JsonSerializer.Serialize(usage, EvidenceJsonOptions)
            };
            currentRecordIds.Add(record.Id);
            if (existingRecords.TryGetValue(record.Id, out var existingRecord))
                db.Entry(existingRecord).CurrentValues.SetValues(record);
            else
                db.PermissionUsageRecords.Add(record);
        }
        db.PermissionUsageRecords.RemoveRange(existingRecords.Values.Where(item => !currentRecordIds.Contains(item.Id)));

        var summaryRows = new List<PermissionUsageSummary>();
        foreach (var summary in report.Summary)
        {
            var stateCounts = report.Permissions.SelectMany(item => item.Windows).Where(item => item.Days == summary.Days).ToArray();
            var now = new DateTimeOffset(evaluatedAt, TimeSpan.Zero);
            summaryRows.Add(new PermissionUsageSummary
            {
                Id = StableGuid($"usage-summary|{account.Id:D}|{summary.Days}|{now.UtcTicks}"),
                CloudAccountId = account.Id, Provider = provider, WindowDays = summary.Days,
                BucketType = "Observation", BucketStartUtc = now.AddDays(-summary.Days), BucketEndUtc = now,
                TotalEffectivePermissions = stateCounts.Length,
                Used = stateCounts.Count(item => item.EvidenceState == PermissionUsageEvidenceState.Used),
                NotObserved = stateCounts.Count(item => item.EvidenceState == PermissionUsageEvidenceState.NotObserved),
                InsufficientObservation = stateCounts.Count(item => item.EvidenceState == PermissionUsageEvidenceState.InsufficientObservation),
                UnknownMapping = stateCounts.Count(item => item.EvidenceState == PermissionUsageEvidenceState.UnknownMapping),
                CollectionIncomplete = stateCounts.Count(item => item.EvidenceState == PermissionUsageEvidenceState.CollectionIncomplete),
                CalculatedAtUtc = now
            });
        }
        foreach (var trend in trends)
        {
            var bucket = trend.BucketType switch { "Daily" => 1, "Weekly" => 7, _ => 30 };
            summaryRows.Add(new PermissionUsageSummary
            {
                Id = StableGuid($"usage-trend|{account.Id:D}|{trend.BucketType}|{trend.BucketStartUtc:O}|{trend.IdentityId:D}"),
                CloudAccountId = account.Id, Provider = provider, IdentityId = trend.IdentityId,
                WindowDays = bucket, BucketType = trend.BucketType,
                BucketStartUtc = new DateTimeOffset(trend.BucketStartUtc, TimeSpan.Zero),
                BucketEndUtc = new DateTimeOffset(trend.BucketEndUtc, TimeSpan.Zero),
                TotalEffectivePermissions = 0, ObservedEventCount = trend.ObservedActionCount,
                CandidateActionCount = trend.CandidateActionCount, UnknownMapping = trend.UnknownMappingCount,
                CalculatedAtUtc = new DateTimeOffset(evaluatedAt, TimeSpan.Zero)
            });
        }
        foreach (var batch in summaryRows.Chunk(400))
        {
            var ids = batch.Select(item => item.Id).ToArray();
            var existingSummaries = await db.PermissionUsageSummaries
                .Where(item => item.CloudAccountId == account.Id && ids.Contains(item.Id))
                .ToDictionaryAsync(item => item.Id, token);
            foreach (var summary in batch)
            {
                if (existingSummaries.TryGetValue(summary.Id, out var existing))
                    db.Entry(existing).CurrentValues.SetValues(summary);
                else
                    db.PermissionUsageSummaries.Add(summary);
            }
        }
        await db.SaveChangesAsync(token);

        foreach (var finding in findings)
        {
            var existing = await db.NormalizedRiskFindings.SingleOrDefaultAsync(item => item.Id == finding.Id, token);
            if (existing is null) db.NormalizedRiskFindings.Add(finding);
            else db.Entry(existing).CurrentValues.SetValues(finding);
            var usageFinding = new UsageAnalyticsFinding
            {
                Id = finding.Id, RiskFindingId = finding.Id, CloudAccountId = account.Id, Provider = provider,
                IdentityId = finding.PrincipalId, PermissionId = finding.PermissionId ?? Guid.Empty, ResourceId = finding.ResourceId,
                RiskScore = finding.PriorityScore, Severity = finding.Severity,
                ObservationWindowDays = int.TryParse(finding.Evidence.Details.GetValueOrDefault("observationWindowDays"),
                    System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var days) ? days : 90,
                Confidence = UsageConfidenceLevel.Medium, Evidence = finding.Evidence.Summary,
                ObservedAtUtc = new DateTimeOffset(finding.DetectedAt, TimeSpan.Zero)
            };
            var existingUsageFinding = await db.UsageAnalyticsFindings.SingleOrDefaultAsync(item => item.Id == usageFinding.Id, token);
            if (existingUsageFinding is null) db.UsageAnalyticsFindings.Add(usageFinding);
            else db.Entry(existingUsageFinding).CurrentValues.SetValues(usageFinding);
        }
        foreach (var recommendation in recommendations)
        {
            var existing = await db.NormalizedRecommendations.SingleOrDefaultAsync(item => item.Id == recommendation.Id, token);
            if (existing is null) db.NormalizedRecommendations.Add(recommendation);
            else db.Entry(existing).CurrentValues.SetValues(recommendation);
            var usageRecommendation = new UsageRecommendation
            {
                Id = StableGuid($"usage-recommendation|{recommendation.Id:D}"),
                RecommendationId = recommendation.Id, FindingId = recommendation.RiskFindingId,
                CloudAccountId = account.Id, Provider = provider,
                RecommendationType = recommendation.RecommendationType.ToString(), Status = "ReviewOnly",
                Evidence = recommendation.Evidence ?? string.Empty, CreatedAtUtc = new DateTimeOffset(evaluatedAt, TimeSpan.Zero)
            };
            var existingUsageRecommendation = await db.UsageRecommendations.SingleOrDefaultAsync(item => item.Id == usageRecommendation.Id, token);
            if (existingUsageRecommendation is null) db.UsageRecommendations.Add(usageRecommendation);
            else db.Entry(existingUsageRecommendation).CurrentValues.SetValues(usageRecommendation);
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    private StoredActivityEvent ConvertEvent(CloudAccount account, CloudActivitySnapshot snapshot,
        ProviderActivityEvent item, IReadOnlyList<CloudPrincipal> principals, IReadOnlyList<CloudResource> resources)
    {
        if (string.IsNullOrWhiteSpace(item.EventId) || string.IsNullOrWhiteSpace(item.Principal)
            || string.IsNullOrWhiteSpace(item.Action) || item.EventId.Length > 512 || item.Principal.Length > 512
            || item.Action.Length > 500 || item.PermissionAction?.Length > 500 || item.Resource?.Length > 2048
            || item.Timestamp < snapshot.FromUtc || item.Timestamp > snapshot.ThroughUtc)
            throw new InvalidOperationException("Provider event is missing required identifiers, exceeds field limits, or is outside its snapshot interval.");
        var mapped = actionMapper.Map(snapshot.Provider, item.Action, item.PermissionAction, item.PermissionEvidenceSource);
        var identityMatches = principals.Where(principal =>
            string.Equals(principal.PrincipalId, item.Principal, StringComparison.OrdinalIgnoreCase)
            || string.Equals(principal.Email, item.Principal, StringComparison.OrdinalIgnoreCase)
            || string.Equals(principal.Name, item.Principal, StringComparison.OrdinalIgnoreCase)).ToArray();
        var identityId = identityMatches.Length == 1 ? identityMatches[0].Id : (Guid?)null;
        var resourceMatches = string.IsNullOrWhiteSpace(item.Resource) ? Array.Empty<CloudResource>()
            : resources.Where(resource => string.Equals(resource.ResourceId, item.Resource,
                snapshot.Provider == CloudProvider.Azure ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).ToArray();
        var resourceId = resourceMatches.Length == 1 ? resourceMatches[0].Id : (Guid?)null;
        var sourceEvent = new
        {
            item.EventId, item.PrincipalType, item.Action, item.PermissionAction,
            item.PermissionEvidenceSource, item.Resource, item.Timestamp, item.Success, item.OutcomeKnown
        };
        var canonicalAction = mapped.PermissionAction ?? item.Action;
        var key = StableText($"{snapshot.Provider}|{account.Id:D}|{item.EventId}|{canonicalAction}|{item.Resource}");
        return new StoredActivityEvent
        {
            Id = StableGuid($"cloud-event|{key}"), Provider = snapshot.Provider, CloudAccountId = account.Id,
            TenantOrAccountId = Bound(snapshot.AccountId, 200), IdentityId = identityId,
            ProviderIdentity = Bound(item.Principal, 512), PrincipalType = Bound(
                identityId.HasValue ? identityMatches[0].IdentityType.ToString() : item.PrincipalType, 100),
            Action = Bound(item.Action, 500), PermissionAction = mapped.PermissionAction,
            PermissionMappingStatus = mapped.Status, PermissionMappingEvidence = Bound(mapped.Evidence, 500),
            ResourceReference = item.Resource, ResourceId = resourceId,
            Outcome = item.OutcomeKnown ? item.Success ? "Succeeded" : "Failed" : "Unknown",
            OutcomeKnown = item.OutcomeKnown, SourceEventId = Bound(item.EventId, 512), Source = Bound(snapshot.Source, 256),
            RawEvidence = JsonSerializer.Serialize(sourceEvent, EvidenceJsonOptions),
            EventTimestamp = item.Timestamp.ToUniversalTime(), CollectionTimestamp = snapshot.CollectedAtUtc.ToUniversalTime(),
            DeduplicationKey = key
        };
    }

    private static ActivityRecord ToActivityRecord(StoredActivityEvent item)
        => new()
        {
            Id = item.Id, CloudAccountId = item.CloudAccountId, PrincipalId = item.IdentityId!.Value,
            ResourceId = item.ResourceId, Action = item.Action, PermissionAction = item.PermissionAction,
            PermissionMappingStatus = item.PermissionMappingStatus, PermissionMappingEvidence = item.PermissionMappingEvidence,
            Service = item.Source, Timestamp = item.EventTimestamp.UtcDateTime,
            Source = item.Source, Success = item.OutcomeKnown && item.Outcome == "Succeeded"
        };

    private static NormalizedEntitlement Normalize(Entitlement item, CloudAccount account,
        IReadOnlyList<CloudPermission> permissions, DateTime collectedAt)
    {
        var provider = ToProvider(account.CloudProvider);
        var permission = permissions.FirstOrDefault(permission => permission.Id == item.PermissionId)
            ?? throw new InvalidOperationException("An entitlement references missing permission metadata.");
        if (string.IsNullOrWhiteSpace(item.Scope))
            throw new InvalidOperationException("An entitlement has no scope; correct saved inventory before analyzing usage.");
        var evidence = new ProviderEvidence(provider, "SavedInventory", item.Source ?? item.Id.ToString("D"),
            null, null, null, item.Scope, item.AssignmentType.ToString(), collectedAt, new Dictionary<string, string>());
        return new(item.Id, provider, item.CloudAccountId, item.PrincipalId, item.ResourceId, item.RoleId, item.PolicyId,
            item.PermissionId, new(provider, NormalizedScopeType.Unknown, item.Scope, item.Scope, null,
                item.AssignmentType == AssignmentType.Inherited),
            item.AssignmentType,
            permission.AccessType == AccessType.Deny ? NormalizedAccessType.Deny : NormalizedAccessType.Allow,
            item.AssignmentType is AssignmentType.Direct or AssignmentType.Role or AssignmentType.Policy,
            item.AssignmentType == AssignmentType.Inherited, false, null, NormalizationStatus.Unknown, evidence,
            item.GrantedAt, item.ExpiresAt, permission.Resource, permission.IsExclusion);
    }

    private static CloudProvider ToProvider(CloudProviderType provider)
        => provider switch
        {
            CloudProviderType.Azure => CloudProvider.Azure,
            CloudProviderType.Aws => CloudProvider.Aws,
            CloudProviderType.Gcp => CloudProvider.Gcp,
            _ => throw new NotSupportedException("Usage analytics supports AWS, Azure and GCP cloud accounts.")
        };

    private static string StableText(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static Guid StableGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static string Bound(string value, int maximum)
        => value.Length <= maximum ? value : throw new InvalidOperationException($"Provider evidence exceeds the {maximum}-character field limit.");
}
