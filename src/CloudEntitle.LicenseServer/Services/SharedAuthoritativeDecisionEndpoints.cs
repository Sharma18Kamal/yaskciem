using System.Security.Claims;
using System.Text.Json;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.OrganizationManagement.Models;
using Microsoft.AspNetCore.Mvc;

namespace CloudEntitle.LicenseServer.Services;

public static partial class SharedWorkspaceEndpoints
{
    public static void MapAuthoritativeDecisionEndpoints(this WebApplication app, SharedWorkspaceStore store)
    {
        const string prefix = "/api/v1/organizations/{organizationId:guid}/effective-decisions";
        app.MapGet(prefix + "/sources", (ClaimsPrincipal principal, Guid organizationId,
            [FromServices] IEffectiveDecisionService decisions, CancellationToken token) => WithDecisionAccess(principal, organizationId,
                store, decisions, null, false, (tenant, _) =>
                {
                    if (!TryGetIdentity(principal, out var identity)) return Task.FromResult<IResult>(Results.Forbid());
                    var organizations = CreateService(store, identity);
                    IReadOnlyList<EffectiveDecisionSource> sources = decisions.Sources(tenant, organizationId)
                        .Where(s => HasBinding(organizations, organizationId, identity, s.Provider, s.AccountId)).ToArray();
                    return Task.FromResult<IResult>(Results.Ok(Envelope(sources)));
                }))
            .RequireAuthorization("CiemApiAccess").WithName("GetEffectiveDecisionSources")
            .WithSummary("List explicitly configured provider decision connections without acquiring credentials or calling a provider.")
            .Produces<ApiEnvelope<IReadOnlyList<EffectiveDecisionSource>>>().Produces(403).Produces(404);

        app.MapPost(prefix + "/capture", async (ClaimsPrincipal principal, Guid organizationId,
            HttpRequest httpRequest, [FromServices] IEffectiveDecisionService decisions, CancellationToken token) =>
        {
            if (!httpRequest.HasJsonContentType()) return Results.Problem(statusCode: 415, title: "A JSON sample request is required.");
            try
            {
                var request = await ReadBoundedEvidenceAsync<CaptureEffectiveDecisionRequest>(httpRequest, 8192, token);
                if (request is null || string.IsNullOrEmpty(request.SourceId) || request.SourceId.Length > 64
                    || request.Principal is null || request.Principal.Length > 254
                    || request.Permission is null || request.Permission.Length > 128
                    || request.FullResourceName is null || request.FullResourceName.Length > 512)
                    return Results.Problem(statusCode: 400, title: "Invalid bounded sample request.");
                return await WithDecisionAccess(principal, organizationId, store, decisions, request.SourceId, true, async (tenant, actor) =>
                {
                    var snapshot = await decisions.CaptureAsync(tenant, organizationId, actor, request, token);
                    return Results.Created($"/api/v1/organizations/{organizationId:D}/effective-decisions/{snapshot.SnapshotId:D}?sourceId={Uri.EscapeDataString(snapshot.SourceId)}",
                        Envelope(snapshot));
                });
            }
            catch (JsonException) { return Results.Problem(statusCode: 400, title: "Invalid sample request JSON."); }
            catch (BadHttpRequestException exception) when (exception.StatusCode == 413)
            { return Results.Problem(statusCode: 413, title: "Sample request exceeds 8 KiB."); }
        }).RequireAuthorization("CiemApiAccess").WithName("CaptureEffectiveDecision")
            .Accepts<CaptureEffectiveDecisionRequest>("application/json")
            .WithSummary("Explicitly collect one read-only exact native GCP IAM decision. No uploaded evidence, mutations or simulations.")
            .Produces<ApiEnvelope<EffectiveDecisionSnapshot>>(201).Produces(400).Produces(403).Produces(404)
            .Produces(409).Produces(413).Produces(415).Produces(422).Produces(502).Produces(504);

        app.MapGet(prefix, (ClaimsPrincipal principal, Guid organizationId, string sourceId,
            [FromServices] IEffectiveDecisionService decisions, CancellationToken token) =>
                WithDecisionAccess(principal, organizationId, store, decisions, sourceId, false, async (tenant, _) =>
                    Results.Ok(Envelope(await decisions.HistoryAsync(tenant, organizationId, sourceId, token)))))
            .RequireAuthorization("CiemApiAccess").WithName("GetEffectiveDecisionHistory")
            .WithSummary("Read at most 50 saved server-origin decision observations without contacting a provider.")
            .Produces<ApiEnvelope<IReadOnlyList<EffectiveDecisionSnapshot>>>().Produces(403).Produces(404);

        app.MapGet(prefix + "/{snapshotId:guid}", (ClaimsPrincipal principal, Guid organizationId, string sourceId,
            Guid snapshotId, [FromServices] IEffectiveDecisionService decisions, CancellationToken token) =>
                WithDecisionAccess(principal, organizationId, store, decisions, sourceId, false, async (tenant, _) =>
                {
                    var snapshot = await decisions.SnapshotAsync(tenant, organizationId, sourceId, snapshotId, token);
                    return snapshot is null ? Results.NotFound() : Results.Ok(Envelope(snapshot));
                }))
            .RequireAuthorization("CiemApiAccess").WithName("GetEffectiveDecisionSnapshot")
            .WithSummary("Read a saved native decision projection; OAuth credentials and raw policy bodies are not returned.")
            .Produces<ApiEnvelope<EffectiveDecisionSnapshot>>().Produces(403).Produces(404);

        app.MapPost(prefix + "/analyze", (ClaimsPrincipal principal, Guid organizationId,
            AnalyzeDecisionEscalationRequest request, [FromServices] IEffectiveDecisionService decisions, CancellationToken token) =>
                WithDecisionAccess(principal, organizationId, store, decisions, request?.SourceId, true, async (tenant, _) =>
                {
                    if (request is null) return Results.Problem(statusCode: 400, title: "Two saved observations are required.");
                    var report = await decisions.AnalyzeAsync(tenant, organizationId, request, token);
                    return Results.Created($"/api/v1/organizations/{organizationId:D}/effective-decisions/analyses/{report.AnalysisId:D}?sourceId={Uri.EscapeDataString(request.SourceId)}",
                        Envelope(report));
                }))
            .RequireAuthorization("CiemApiAccess").WithName("AnalyzeDecisionEscalation")
            .WithSummary("Persist a fail-closed exact IAM-decision transition analysis; broad operational escalation remains unproven.")
            .Produces<ApiEnvelope<DecisionEscalationReport>>(201).Produces(400).Produces(403).Produces(404).Produces(409);

        app.MapGet(prefix + "/analyses/{analysisId:guid}", (ClaimsPrincipal principal, Guid organizationId, string sourceId,
            Guid analysisId, [FromServices] IEffectiveDecisionService decisions, CancellationToken token) =>
                WithDecisionAccess(principal, organizationId, store, decisions, sourceId, false, async (tenant, _) =>
                {
                    var report = await decisions.AnalysisAsync(tenant, organizationId, sourceId, analysisId, token);
                    return report is null ? Results.NotFound() : Results.Ok(Envelope(report));
                }))
            .RequireAuthorization("CiemApiAccess").WithName("GetDecisionEscalationAnalysis")
            .WithSummary("Read the immutable saved exact IAM-decision transition analysis without a live provider call.")
            .Produces<ApiEnvelope<DecisionEscalationReport>>().Produces(403).Produces(404);
    }

    private static async Task<IResult> WithDecisionAccess(ClaimsPrincipal principal, Guid organization,
        SharedWorkspaceStore store, IEffectiveDecisionService decisions, string? source, bool manage,
        Func<Guid, Guid, Task<IResult>> operation)
    {
        if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
        try
        {
            var service = CreateService(store, identity);
            if (!service.Authorize(organization, identity, OrganizationAction.ViewInventory).Allowed
                || !service.Authorize(organization, identity, OrganizationAction.ViewFindings).Allowed
                || manage && !service.Authorize(organization, identity, OrganizationAction.ManagePolicies).Allowed)
                return Results.Forbid();
            var tenant = Guid.Parse(identity.TenantId);
            if (source is not null)
            {
                var connection = decisions.Source(tenant, organization, source);
                if (!HasBinding(service, organization, identity, connection.Provider, connection.AccountId))
                    return Results.Forbid();
            }
            return await operation(tenant, identity.UserId);
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "Explicit consent and the exact configured sample are required."); }
        catch (NotSupportedException) { return Results.Problem(statusCode: 422, title: "Complete native provider authorization evaluation is unsupported for this source."); }
        catch (InvalidOperationException) { return Results.Problem(statusCode: 409, title: "The configured native connection or bounded evidence is unavailable."); }
        catch (HttpRequestException) { return Results.Problem(statusCode: 502, title: "Native read failed; no confirmed decision was produced."); }
        catch (JsonException) { return Results.Problem(statusCode: 502, title: "Native decision response was invalid; no confirmation was produced."); }
        catch (OperationCanceledException) { return Results.Problem(statusCode: 504, title: "Native decision read was cancelled or timed out; no confirmation was produced."); }
    }
}
