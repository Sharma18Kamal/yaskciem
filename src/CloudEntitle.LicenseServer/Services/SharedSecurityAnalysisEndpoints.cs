using System.Security.Claims;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.Integration.Services;
using CloudEntitle.OrganizationManagement.Models;

namespace CloudEntitle.LicenseServer.Services;

public static partial class SharedWorkspaceEndpoints
{
    private static void MapSecurityAnalysisEndpoints(this WebApplication app, SharedWorkspaceStore store)
    {
        app.MapPost("/api/v1/organizations/{organizationId:guid}/security-analysis", (ClaimsPrincipal principal, Guid organizationId,
            SecurityAnalysisRequest request, IConfiguration configuration, CancellationToken token) =>
            WithGovernanceAccess(principal, organizationId, store, true, async (tenant, actor) =>
            {
                if (request is null || !Enum.IsDefined(request.Provider) || string.IsNullOrWhiteSpace(request.AccountId)
                    || request.AccountId.Length > 128 || request.ActivitySnapshotId is null && request.EntitlementSnapshotId is null)
                    throw new ArgumentException("Select at least one saved activity/document snapshot for a bound account.");
                var service = CreateService(store, actor);
                if (!service.Authorize(organizationId, actor, OrganizationAction.ViewInventory).Allowed
                    || !service.Authorize(organizationId, actor, OrganizationAction.ViewFindings).Allowed
                    || !service.GetTenantBindings(organizationId, actor).Any(binding => binding.Provider == request.Provider
                        && binding.ExternalTenantId == request.AccountId)) return Results.Forbid();
                var anomalies = new List<IdentityBehaviorAnomaly>();
                var changes = new List<ObservedEntitlementChange>();
                var notices = new List<string> { "Saved partial observations only. No live endpoint was contacted; no cloud permission change or automatic removal is authorized." };
                if (request.ActivitySnapshotId is { } activityId)
                {
                    var current = await store.GetCloudActivitySnapshotAsync(tenant, organizationId, request.Provider, request.AccountId, activityId, token)
                        ?? throw new KeyNotFoundException();
                    var history = await store.GetCloudActivityHistoryAsync(tenant, organizationId, request.Provider, request.AccountId, 50, null, token);
                    var baseline = new List<CloudActivitySnapshot>();
                    foreach (var item in history.Snapshots.Where(item => item.SnapshotId != activityId && item.ThroughUtc < current.ThroughUtc).Take(5))
                        baseline.Add(await store.GetCloudActivitySnapshotAsync(tenant, organizationId, request.Provider, request.AccountId, item.SnapshotId, token)
                            ?? throw new InvalidOperationException("Saved baseline disappeared."));
                    var section = configuration.GetSection($"BehaviorAnalysis:Organizations:{tenant:D}:{organizationId:D}:{request.Provider}");
                    if (section.GetValue<bool>("Enabled"))
                    {
                        var policy = new IdentityBehaviorPolicy(section.GetSection("PrivilegedActions").GetChildren().Select(item => item.Value ?? "").ToArray(),
                            section["TimeZoneId"] ?? throw new ArgumentException("Configure the business-hours time zone."),
                            section.GetValue("BusinessStartHour", 9), section.GetValue("BusinessEndHour", 18),
                            section.GetValue("MinimumBaselineDays", 7), section.GetValue("MinimumBaselineEvents", 20))
                        {
                            PermissionChangeActions = section.GetSection("PermissionChangeActions").GetChildren().Select(item => item.Value ?? "").ToArray(),
                            PermissionChangeBurstThreshold = section.GetValue("PermissionChangeBurstThreshold", 5)
                        };
                        anomalies.AddRange(new IdentityBehaviorAnalysisService().Analyze(current, baseline, policy));
                    }
                    else notices.Add("Behavior analysis is disabled until the client configures exact privileged actions, baseline thresholds and business hours.");
                    notices.Add("At most five older activity snapshots from the newest 50 summaries are used. Future events are excluded and overlapping records are deduplicated; this is not first-ever or complete-history proof. Country requires source-supplied enrichment; IP geolocation is not performed.");
                }
                if (request.EntitlementSnapshotId is { } documentId)
                {
                    var current = await store.GetEntitlementEvidenceAsync(tenant, organizationId, request.Provider, request.AccountId, documentId, token)
                        ?? throw new KeyNotFoundException();
                    var history = await store.GetEntitlementEvidenceHistoryAsync(tenant, organizationId, request.Provider, request.AccountId, token);
                    EntitlementEvidenceSnapshot? previous = null;
                    foreach (var item in history.Where(item => item.SnapshotId != documentId
                        && item.CollectedAtUtc <= current.StartedAtUtc.AddTicks(10)).Take(50))
                    {
                        var candidate = await store.GetEntitlementEvidenceAsync(tenant, organizationId, request.Provider, request.AccountId, item.SnapshotId, token)
                            ?? throw new InvalidOperationException("Saved document baseline disappeared.");
                        // PostgreSQL summaries have microsecond precision; compare the original payload timestamps.
                        if (candidate.CollectedAtUtc < current.StartedAtUtc
                            && (previous is null || candidate.CollectedAtUtc > previous.CollectedAtUtc))
                            previous = candidate;
                    }
                    changes.AddRange(new ObservedEntitlementChangeService().Compare(previous, current));
                    notices.Add(previous is null ? "No nonoverlapping prior document observation is available; the first snapshot initializes a baseline."
                        : "Document changes are observed additions/disappearances or statement/digest differences, not confirmed deletions, provider change times or malicious escalation. Trust relationships and credential changes are not collected by this document source.");
                }
                var report = new SecurityAnalysisReport(anomalies, changes, notices);
                await store.RecordSecurityAnalysisAsync(tenant, organizationId, request, report, token);
                return Results.Ok(Envelope(report));
            })).RequireAuthorization("CiemApiAccess").WithName("AnalyzeSavedSecurityEvidence")
            .WithSummary("Analyze selected saved evidence and publish deduplicated potential review events through existing SIEM/ticketing rules; no live polling.")
            .Produces<ApiEnvelope<SecurityAnalysisReport>>().Produces(400).Produces(403).Produces(404).Produces(409);
    }
}
