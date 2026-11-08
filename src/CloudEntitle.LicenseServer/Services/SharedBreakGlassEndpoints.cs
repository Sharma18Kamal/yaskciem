using System.Security.Claims;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.OrganizationManagement.Models;

namespace CloudEntitle.LicenseServer.Services;

public static partial class SharedWorkspaceEndpoints
{
    private static void MapBreakGlassEndpoints(this WebApplication app, SharedWorkspaceStore store)
    {
        const string prefix = "/api/v1/organizations/{organizationId:guid}/break-glass-reports";
        app.MapGet(prefix, (ClaimsPrincipal principal, Guid organizationId, CancellationToken token) =>
            WithGovernanceAccess(principal, organizationId, store, false, async (tenant, actor) =>
            {
                if (!CanReadBreakGlass(store, organizationId, actor)) return Results.Forbid();
                return Results.Ok(Envelope(await store.GetBreakGlassReportsAsync(tenant, organizationId, token)));
            })).RequireAuthorization("CiemApiAccess").WithName("ListBreakGlassReports")
            .WithSummary("Read the newest 100 retrospective client emergency-account reports; no cloud call.")
            .Produces<ApiEnvelope<IReadOnlyList<BreakGlassReport>>>().Produces(403);
        app.MapGet(prefix + "/{reportId:guid}", (ClaimsPrincipal principal, Guid organizationId, Guid reportId, CancellationToken token) =>
            WithGovernanceAccess(principal, organizationId, store, false, async (tenant, actor) =>
            {
                if (!CanReadBreakGlass(store, organizationId, actor)) return Results.Forbid();
                var report = await store.GetBreakGlassReportAsync(tenant, organizationId, reportId, token);
                return report is null ? Results.NotFound() : Results.Ok(Envelope(report));
            })).RequireAuthorization("CiemApiAccess").WithName("GetBreakGlassReport")
            .WithSummary("Read an immutable scoped emergency incident and its independent client-closure review.")
            .Produces<ApiEnvelope<BreakGlassReport>>().Produces(403).Produces(404);
        app.MapPost(prefix, (ClaimsPrincipal principal, Guid organizationId, CreateBreakGlassReportRequest request, CancellationToken token) =>
            WithGovernanceAccess(principal, organizationId, store, true, async (tenant, actor) =>
            {
                if (!CanReadBreakGlass(store, organizationId, actor) || !HasEmergencyDelegation(principal)) return Results.Forbid();
                ValidateBreakGlassRequest(request);
                var service = CreateService(store, actor);
                if (!service.GetTenantBindings(organizationId, actor).Any(binding => binding.Provider == request.Provider
                    && binding.ExternalTenantId == request.AccountId)) return Results.Forbid();
                var matches = Array.Empty<string>();
                if (request.ActivitySnapshotId is { } snapshotId)
                {
                    var evidence = await store.GetCloudActivitySnapshotAsync(tenant, organizationId, request.Provider, request.AccountId, snapshotId, token)
                        ?? throw new KeyNotFoundException();
                    matches = evidence.Events.Where(item => item.Principal == request.ExternalPrincipal
                            && item.OutcomeKnown && item.Success && item.Timestamp >= request.StartedAtUtc && item.Timestamp <= request.EndedAtUtc)
                        .Select(item => item.EventId).Distinct(StringComparer.Ordinal).ToArray();
                    if (matches.Length > 2000) throw new ArgumentException("Emergency reconciliation exceeds 2,000 matching events; narrow the incident interval.");
                }
                var report = new BreakGlassReport(request.ReportId, request.Provider, request.AccountId, request.ExternalPrincipal,
                    request.StartedAtUtc, request.EndedAtUtc, request.Reason, request.ClientIncidentReference, actor.UserId,
                    DateTimeOffset.UtcNow, request.ActivitySnapshotId, matches,
                    matches.Length == 0 ? "ClientAttestationOnly" : "PartialActivityObservedNotRevocationProof");
                var saved = await store.SaveBreakGlassReportAsync(tenant, organizationId, request, report, token);
                return Results.Created($"/api/v1/organizations/{organizationId:D}/break-glass-reports/{saved.ReportId:D}", Envelope(saved));
            })).RequireAuthorization("CiemApiAccess").WithName("ReportClientBreakGlass")
            .WithSummary("Report client-owned emergency access after connectivity returns; never issue credentials or bypass JIT.")
            .Produces<ApiEnvelope<BreakGlassReport>>(201).Produces(400).Produces(403).Produces(404).Produces(409);
        app.MapPost(prefix + "/{reportId:guid}/review", (ClaimsPrincipal principal, Guid organizationId, Guid reportId,
            ReviewBreakGlassReportRequest request, IConfiguration configuration, CancellationToken token) =>
            WithGovernanceAccess(principal, organizationId, store, true, async (tenant, actor) =>
            {
                if (!CanReadBreakGlass(store, organizationId, actor) || !HasEmergencyDelegation(principal)) return Results.Forbid();
                if (request is null) throw new ArgumentException("A reasoned independent review is required.");
                ValidateEmergencyText(request.Reason, 2000, "Review reason");
                ValidateEmergencyReference(request.ClientClosureReference);
                var configured = configuration.GetSection($"EmergencyAccessReview:Organizations:{tenant:D}:{organizationId:D}:ReviewerIds")
                    .GetChildren().Select(item => item.Value).ToArray();
                if (configured.Length is < 1 or > 10 || configured.Any(value => !Guid.TryParse(value, out var id) || id == Guid.Empty))
                    throw new ArgumentException("Configure one through ten explicit emergency security reviewer object IDs.");
                if (!configured.Any(value => Guid.Parse(value!) == actor.UserId)) return Results.Forbid();
                var report = await store.GetBreakGlassReportAsync(tenant, organizationId, reportId, token) ?? throw new KeyNotFoundException();
                if (!CreateService(store, actor).GetTenantBindings(organizationId, actor).Any(binding => binding.Provider == report.Provider
                    && binding.ExternalTenantId == report.AccountId)) return Results.Forbid();
                return Results.Ok(Envelope(await store.ReviewBreakGlassReportAsync(tenant, organizationId, reportId, actor.UserId, request, token)));
            })).RequireAuthorization("CiemApiAccess").WithName("ReviewClientBreakGlass")
            .WithSummary("Append an independent security attestation of client cleanup/rotation; not provider-verified revocation.")
            .Produces<ApiEnvelope<BreakGlassReport>>().Produces(400).Produces(403).Produces(404).Produces(409);
    }

    private static bool CanReadBreakGlass(SharedWorkspaceStore store, Guid organization, OrganizationIdentity actor)
        => CreateService(store, actor).Authorize(organization, actor, OrganizationAction.ViewInventory).Allowed
            && CreateService(store, actor).Authorize(organization, actor, OrganizationAction.ViewFindings).Allowed;
    private static bool HasEmergencyDelegation(ClaimsPrincipal principal)
        => (principal.FindFirstValue("scp") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("access_as_user", StringComparer.Ordinal);
    private static void ValidateBreakGlassRequest(CreateBreakGlassReportRequest request)
    {
        if (request is null || request.ReportId == Guid.Empty || request.Provider is not (CloudProvider.Azure or CloudProvider.Aws or CloudProvider.Gcp)
            || request.ActivitySnapshotId == Guid.Empty || request.EndedAtUtc > DateTimeOffset.UtcNow.AddMinutes(1)
            || request.StartedAtUtc >= request.EndedAtUtc || request.EndedAtUtc - request.StartedAtUtc > TimeSpan.FromDays(7))
            throw new ArgumentException("Use a nonempty report ID, supported bound cloud account and a completed incident interval of at most seven days.");
        ValidateEmergencyText(request.AccountId, 128, "Account");
        ValidateEmergencyText(request.ExternalPrincipal, 2048, "External principal");
        ValidateEmergencyText(request.Reason, 2000, "Incident reason");
        ValidateEmergencyReference(request.ClientIncidentReference);
    }
    private static void ValidateEmergencyText(string value, int maximum, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl) || value != value.Trim())
            throw new ArgumentException($"{label} must be bounded single-line text without surrounding whitespace.");
    }
    private static void ValidateEmergencyReference(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':')))
            throw new ArgumentException("Use an opaque client incident/closure reference of at most 128 characters, never a credential, token or signed URL.");
    }
}
