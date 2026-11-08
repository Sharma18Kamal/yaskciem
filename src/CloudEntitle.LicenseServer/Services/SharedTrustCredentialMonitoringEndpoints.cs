using System.Security.Claims;
using System.Text.Json;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.Integration.Services;
using CloudEntitle.OrganizationManagement.Models;
using CloudEntitle.OrganizationManagement.Services;

namespace CloudEntitle.LicenseServer.Services;

public static partial class SharedWorkspaceEndpoints
{
    public static void MapTrustCredentialMonitoringEndpoints(this WebApplication app, SharedWorkspaceStore store)
    {
        const string prefix = "/api/v1/organizations/{organizationId:guid}/trust-credential-evidence";
        app.MapPost(prefix, async (ClaimsPrincipal principal, Guid organizationId, HttpRequest httpRequest,
            ILoggerFactory loggerFactory, CancellationToken token) =>
        {
            if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
            try
            {
                var service = CreateService(store, identity);
                if (!service.Authorize(organizationId, identity, OrganizationAction.ManagePolicies).Allowed
                    || !service.Authorize(organizationId, identity, OrganizationAction.ViewInventory).Allowed
                    || !service.Authorize(organizationId, identity, OrganizationAction.ViewFindings).Allowed)
                    return Results.Forbid();
                if (!httpRequest.HasJsonContentType())
                    return Results.Problem(statusCode: 415, title: "Trust/credential evidence requires JSON");
                var snapshot = await ReadBoundedEvidenceAsync<TrustCredentialSnapshot>(httpRequest, 8 * 1024 * 1024, token);
                SharedWorkspaceStore.ValidateTrustCredentialSnapshot(snapshot);
                if (!HasBinding(service, organizationId, identity, snapshot.Provider, snapshot.AccountId)) return Results.Forbid();
                var tenant = Guid.Parse(identity.TenantId);
                await store.SaveTrustCredentialSnapshotAsync(tenant, organizationId, identity.UserId, snapshot, token);
                return Results.Created($"/api/v1/organizations/{organizationId:D}/trust-credential-evidence/{snapshot.SnapshotId:D}?provider={snapshot.Provider}&accountId={Uri.EscapeDataString(snapshot.AccountId)}",
                    Envelope(snapshot));
            }
            catch (ArgumentException)
            {
                loggerFactory.CreateLogger("TrustCredentialEvidence").LogWarning("Invalid trust/credential evidence for organization {Organization}", organizationId);
                return Results.Problem(statusCode: 400, title: "Invalid bounded trust/credential evidence");
            }
            catch (InvalidOperationException)
            {
                loggerFactory.CreateLogger("TrustCredentialEvidence").LogWarning("Conflicting trust/credential evidence for organization {Organization}", organizationId);
                return Results.Problem(statusCode: 409, title: "Trust/credential evidence identifier conflict");
            }
            catch (JsonException)
            {
                loggerFactory.CreateLogger("TrustCredentialEvidence").LogWarning("Invalid trust/credential evidence JSON for organization {Organization}", organizationId);
                return Results.Problem(statusCode: 400, title: "Invalid trust/credential evidence JSON");
            }
            catch (BadHttpRequestException exception) when (exception.StatusCode == 413)
            {
                loggerFactory.CreateLogger("TrustCredentialEvidence").LogWarning("Oversized trust/credential evidence for organization {Organization}", organizationId);
                return Results.Problem(statusCode: 413, title: "Trust/credential evidence exceeds 8 MiB");
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        }).RequireAuthorization("CiemApiAccess").WithName("UploadTrustCredentialEvidence")
            .Accepts<TrustCredentialSnapshot>("application/json")
            .WithSummary("Save explicitly collected, partial Azure/AWS/GCP trust and credential metadata; no secret values or cloud writes.")
            .Produces<ApiEnvelope<TrustCredentialSnapshot>>(201).Produces(400).Produces(403).Produces(404).Produces(409).Produces(413).Produces(415);

        app.MapGet(prefix, (ClaimsPrincipal principal, Guid organizationId, string accountId, CloudProvider? provider, CancellationToken token) =>
            WithMonitoringAccess(principal, organizationId, store, false, async tenant =>
            {
                if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
                var selectedProvider = provider ?? CloudProvider.Azure;
                if (selectedProvider is not (CloudProvider.Azure or CloudProvider.Aws or CloudProvider.Gcp)) return Results.BadRequest();
                var service = CreateService(store, identity);
                if (!HasBinding(service, organizationId, identity, selectedProvider, accountId)) return Results.Forbid();
                return Results.Ok(Envelope(await store.GetTrustCredentialHistoryAsync(tenant, organizationId,
                    selectedProvider, accountId, token)));
            })).RequireAuthorization("CiemApiAccess").WithName("GetTrustCredentialHistory")
            .WithSummary("Read up to 50 immutable trust/credential observations for one bound Azure, AWS or GCP account; no live source is contacted.")
            .Produces<ApiEnvelope<IReadOnlyList<TrustCredentialSnapshotSummary>>>().Produces(403).Produces(404);

        app.MapGet(prefix + "/{snapshotId:guid}", (ClaimsPrincipal principal, Guid organizationId, string accountId,
            CloudProvider? provider,
            Guid snapshotId, CancellationToken token) =>
            WithMonitoringAccess(principal, organizationId, store, false, async tenant =>
            {
                if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
                var selectedProvider = provider ?? CloudProvider.Azure;
                if (selectedProvider is not (CloudProvider.Azure or CloudProvider.Aws or CloudProvider.Gcp)) return Results.BadRequest();
                var service = CreateService(store, identity);
                if (!HasBinding(service, organizationId, identity, selectedProvider, accountId)) return Results.Forbid();
                var snapshot = await store.GetTrustCredentialSnapshotAsync(tenant, organizationId, selectedProvider, accountId, snapshotId, token);
                return snapshot is null ? Results.NotFound() : Results.Ok(Envelope(snapshot));
            })).RequireAuthorization("CiemApiAccess").WithName("GetTrustCredentialSnapshot")
            .WithSummary("Read one saved trust/credential snapshot without initiating a live cloud or Graph request.")
            .Produces<ApiEnvelope<TrustCredentialSnapshot>>().Produces(403).Produces(404);

        app.MapPost("/api/v1/organizations/{organizationId:guid}/trust-credential-monitoring/analyze",
            async (ClaimsPrincipal principal, Guid organizationId, TrustCredentialMonitoringRequest request, CancellationToken token) =>
            {
                if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
                try
                {
                    var service = CreateService(store, identity);
                    if (!service.Authorize(organizationId, identity, OrganizationAction.ManagePolicies).Allowed
                        || !service.Authorize(organizationId, identity, OrganizationAction.ViewInventory).Allowed
                        || !service.Authorize(organizationId, identity, OrganizationAction.ViewFindings).Allowed)
                        return Results.Forbid();
                    if (request is null || request.Provider is not (CloudProvider.Azure or CloudProvider.Aws or CloudProvider.Gcp)
                        || string.IsNullOrWhiteSpace(request.AccountId) || request.AccountId.Length > 128
                        || request.TrustCredentialSnapshotId is null && request.BaselineScanId is null && request.CurrentScanId is null
                        || (request.BaselineScanId is null) != (request.CurrentScanId is null))
                        return BadRequest("invalid_monitoring_analysis", "Select saved evidence or a comparable scan pair for the bound account.");
                    if (!HasBinding(service, organizationId, identity, request.Provider, request.AccountId)) return Results.Forbid();
                    var tenant = Guid.Parse(identity.TenantId);
                    var changes = new List<TrustCredentialChange>();
                    var escalations = new List<EffectivePrivilegeEscalationSignal>();
                    var notices = new List<string>
                    {
                        "Analysis uses immutable saved observations only; source coverage is provider-specific and partial, no live source was queried, no cloud write was performed, and missing evidence never proves absence."
                    };
                    if (request.TrustCredentialSnapshotId is { } snapshotId)
                    {
                        var current = await store.GetTrustCredentialSnapshotAsync(tenant, organizationId, request.Provider,
                            request.AccountId, snapshotId, token) ?? throw new KeyNotFoundException();
                        var history = await store.GetTrustCredentialHistoryAsync(tenant, organizationId, request.Provider, request.AccountId, token);
                        var candidates = new List<TrustCredentialSnapshot>();
                        foreach (var item in history.Where(item => item.SnapshotId != snapshotId
                            && item.CollectedAtUtc <= current.StartedAtUtc.AddTicks(10)).Take(50))
                        {
                            var candidate = await store.GetTrustCredentialSnapshotAsync(tenant, organizationId, request.Provider,
                                request.AccountId, item.SnapshotId, token) ?? throw new InvalidOperationException("Saved trust/credential baseline disappeared.");
                            candidates.Add(candidate);
                        }
                        var analysis = new TrustCredentialMonitoringAnalysisService();
                        var previous = analysis.SelectBaseline(current, candidates);
                        changes.AddRange(analysis.Compare(previous, current));
                        notices.Add(previous is null
                            ? "No nonoverlapping prior trust/credential observation exists. This snapshot initializes a baseline."
                            : "Trust and credential differences are metadata observations. Partial source coverage suppresses removal claims; no effective privilege escalation is proven.");
                    }
                    if (request.CurrentScanId is { } currentScan && request.BaselineScanId is { } baselineScan)
                    {
                        var summaries = await store.GetScanSummariesAsync(tenant, organizationId, 100, token);
                        var baseline = summaries.SingleOrDefault(item => item.ScanId == baselineScan) ?? throw new KeyNotFoundException();
                        var current = summaries.SingleOrDefault(item => item.ScanId == currentScan) ?? throw new KeyNotFoundException();
                        if (!ContainsAccount(baseline, request.Provider, request.AccountId)
                            || !ContainsAccount(current, request.Provider, request.AccountId)
                            || baseline.CompletedAtUtc >= current.CompletedAtUtc)
                            return BadRequest("incomparable_scans", "Selected scans must be chronological observations of the exact bound account.");
                        var beforeRecords = await store.GetCiemScanRecordsAsync(tenant, organizationId, baselineScan,
                            "effective-access", request.AccountId, 5000, token);
                        var afterRecords = await store.GetCiemScanRecordsAsync(tenant, organizationId, currentScan,
                            "effective-access", request.AccountId, 5000, token);
                        var beforeComplete = EffectiveAccessComplete(baseline, request.Provider, request.AccountId) && beforeRecords.Count < 5000;
                        var afterComplete = EffectiveAccessComplete(current, request.Provider, request.AccountId) && afterRecords.Count < 5000;
                        escalations.AddRange(new TrustCredentialMonitoringAnalysisService().CompareObservedAssignments(
                            request.Provider, request.AccountId, baseline.CompletedAtUtc, current.CompletedAtUtc,
                            beforeComplete, afterComplete, beforeRecords, afterRecords));
                        notices.Add("Added or newly privileged assignments are Potential signals only. Saved scans contain normalized role/assignment observations, not authoritative comparable provider authorization decisions; Verified escalation is never inferred from this source.");
                    }
                    var report = new TrustCredentialMonitoringReport(changes, escalations, notices);
                    await store.RecordTrustCredentialMonitoringAsync(tenant, organizationId, request, report, token);
                    return Results.Ok(Envelope(report));
                }
                catch (ArgumentException) { return BadRequest("invalid_monitoring_analysis", "Saved evidence is outside the bounded comparable scope."); }
                catch (UnauthorizedAccessException) { return Results.Forbid(); }
                catch (KeyNotFoundException) { return Results.NotFound(); }
            }).RequireAuthorization("CiemApiAccess").WithName("AnalyzeTrustCredentialMonitoring")
            .WithSummary("Compare selected saved trust/credential and effective-access evidence; role assignments without authoritative decisions remain Potential.")
            .Produces<ApiEnvelope<TrustCredentialMonitoringReport>>().Produces(400).Produces(403).Produces(404).Produces(409);
    }

    private static bool HasBinding(OrganizationManagementService service, Guid organizationId,
        OrganizationIdentity identity, CloudProvider provider, string accountId)
        => service.GetTenantBindings(organizationId, identity).Any(binding => binding.Provider == provider
            && binding.ExternalTenantId.Equals(accountId, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsAccount(SharedScanSummaryDto summary, CloudProvider provider, string accountId)
        => summary.Accounts.Any(item => item.Provider == provider
            && item.ExternalAccountId.Equals(accountId, StringComparison.OrdinalIgnoreCase));

    private static bool EffectiveAccessComplete(SharedScanSummaryDto summary, CloudProvider provider, string accountId)
    {
        var scopes = summary.MonitoringScopes.Where(item => item.Provider == provider
            && item.ExternalAccountId.Equals(accountId, StringComparison.OrdinalIgnoreCase)
            && item.Kind == "effective-access" && item.Profile == "effective-access-v1").ToArray();
        var coverage = summary.Coverage.Where(item => item.Provider.Equals(provider.ToString(), StringComparison.OrdinalIgnoreCase)
            && item.DataSource.Equals("Effective access", StringComparison.OrdinalIgnoreCase)).ToArray();
        return scopes.Length == 1 && coverage.Length > 0
            && coverage.All(item => item.Status is "Collected" or "NoData");
    }
}
