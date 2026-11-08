using System.Security.Claims;
using System.Text.Json;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.OrganizationManagement.Models;

namespace CloudEntitle.LicenseServer.Services;

public static partial class SharedWorkspaceEndpoints
{
    private static void MapEntitlementEvidenceEndpoints(this WebApplication app, SharedWorkspaceStore store)
    {
        const string prefix = "/api/v1/organizations/{organizationId:guid}/entitlement-evidence";
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
                if (!httpRequest.HasJsonContentType()) return Results.Problem(statusCode: 415, title: "Entitlement evidence requires JSON");
                var request = await ReadBoundedEvidenceAsync<EntitlementEvidenceSnapshot>(httpRequest, 8 * 1024 * 1024, token);
                SharedWorkspaceStore.ValidateEntitlementEvidence(request);
                if (!service.GetTenantBindings(organizationId, identity).Any(binding =>
                    binding.Provider == request.Provider && binding.ExternalTenantId == request.AccountId)) return Results.Forbid();
                await store.SaveEntitlementEvidenceAsync(Guid.Parse(identity.TenantId), organizationId, identity.UserId, request, token);
                return Results.Created($"{prefix.Replace("{organizationId:guid}", organizationId.ToString("D"))}/{request.SnapshotId:D}?provider={request.Provider}&accountId={Uri.EscapeDataString(request.AccountId)}", Envelope(request));
            }
            catch (ArgumentException)
            {
                loggerFactory.CreateLogger("EntitlementEvidence").LogWarning("Invalid entitlement evidence for organization {Organization}", organizationId);
                return Results.Problem(statusCode: 400, title: "Invalid bounded entitlement evidence");
            }
            catch (InvalidOperationException)
            {
                loggerFactory.CreateLogger("EntitlementEvidence").LogWarning("Conflicting entitlement evidence for organization {Organization}", organizationId);
                return Results.Problem(statusCode: 409, title: "Entitlement evidence identifier conflict");
            }
            catch (JsonException)
            {
                loggerFactory.CreateLogger("EntitlementEvidence").LogWarning("Invalid entitlement evidence JSON for organization {Organization}", organizationId);
                return Results.Problem(statusCode: 400, title: "Invalid entitlement evidence JSON");
            }
            catch (BadHttpRequestException exception) when (exception.StatusCode == 413)
            {
                loggerFactory.CreateLogger("EntitlementEvidence").LogWarning("Oversized entitlement evidence for organization {Organization}", organizationId);
                return Results.Problem(statusCode: 413, title: "Entitlement evidence exceeds 8 MiB");
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        }).RequireAuthorization("CiemApiAccess").WithName("UploadEntitlementEvidence")
            .Accepts<EntitlementEvidenceSnapshot>("application/json")
            .WithSummary("Save explicitly collected provider permission document observations; never authorizes a cloud write.")
            .Produces<ApiEnvelope<EntitlementEvidenceSnapshot>>(201).Produces(400).Produces(403).Produces(404).Produces(409).Produces(413).Produces(415);
        app.MapGet(prefix, (ClaimsPrincipal principal, Guid organizationId, CloudProvider provider, string accountId, CancellationToken token) =>
            WithMonitoringAccess(principal, organizationId, store, false, async tenant =>
            {
                if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
                if (!CreateService(store, identity).GetTenantBindings(organizationId, identity).Any(binding =>
                    binding.Provider == provider && binding.ExternalTenantId == accountId)) return Results.Forbid();
                return Results.Ok(Envelope(await store.GetEntitlementEvidenceHistoryAsync(tenant, organizationId, provider, accountId, token)));
            })).RequireAuthorization("CiemApiAccess").WithName("GetEntitlementEvidenceHistory")
            .WithSummary("Read the latest 50 saved permission document observation summaries for a bound account; no live endpoint call.")
            .Produces<ApiEnvelope<IReadOnlyList<EntitlementEvidenceHistoryEntry>>>().Produces(403).Produces(404);
        app.MapGet(prefix + "/{snapshotId:guid}", (ClaimsPrincipal principal, Guid organizationId, CloudProvider provider,
            string accountId, Guid snapshotId, CancellationToken token) =>
            WithMonitoringAccess(principal, organizationId, store, false, async tenant =>
            {
                if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
                if (!CreateService(store, identity).GetTenantBindings(organizationId, identity).Any(binding =>
                    binding.Provider == provider && binding.ExternalTenantId == accountId)) return Results.Forbid();
                var snapshot = await store.GetEntitlementEvidenceAsync(tenant, organizationId, provider, accountId, snapshotId, token);
                return snapshot is null ? Results.NotFound() : Results.Ok(Envelope(snapshot));
            })).RequireAuthorization("CiemApiAccess").WithName("GetEntitlementEvidence")
            .WithSummary("Read saved permission statements and limitations without initiating live cloud calls.")
            .Produces<ApiEnvelope<EntitlementEvidenceSnapshot>>().Produces(403).Produces(404);
    }
}
