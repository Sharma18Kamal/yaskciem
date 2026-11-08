using System.Security.Claims;
using System.Text.Json;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.OrganizationManagement.Models;

namespace CloudEntitle.LicenseServer.Services;

public static partial class SharedWorkspaceEndpoints
{
    private static void MapSecurityAlertEndpoints(this WebApplication app, SharedWorkspaceStore store)
    {
        const string prefix = "/api/v1/organizations/{organizationId:guid}/security-alerts/{product}/{channel}";
        app.MapPost(prefix + "/batches", async (ClaimsPrincipal principal, Guid organizationId, string product, string channel,
            HttpRequest httpRequest, IConfiguration configuration, ILoggerFactory loggerFactory, CancellationToken token) =>
        {
            if (!TryGetIdentity(principal, out var identity)) return Results.Forbid();
            try
            {
                var service = CreateService(store, identity);
                if (!service.Authorize(organizationId, identity, OrganizationAction.ManagePolicies).Allowed
                    || !service.Authorize(organizationId, identity, OrganizationAction.ViewFindings).Allowed
                    || !service.Authorize(organizationId, identity, OrganizationAction.ViewInventory).Allowed)
                    return Results.Forbid();
                if (!InboundSecurityProducts.Supported.Contains(product) || !InboundSecurityProducts.IsChannel(channel))
                    throw new ArgumentException("Invalid connector scope.");
                var tenant = Guid.Parse(identity.TenantId);
                var section = configuration.GetSection($"InboundSecurity:Organizations:{tenant:D}:{organizationId:D}:Channels:{channel}");
                if (!section.GetValue<bool>("Enabled") || section["Product"] != product) return Results.Forbid();
                if (!httpRequest.HasJsonContentType())
                    return Results.Problem(statusCode: 415, title: "Security alert batches require JSON");
                var request = await ReadBoundedEvidenceAsync<ImportSecurityAlertsRequest>(httpRequest, InboundSecurityProducts.MaximumBatchBytes, token);
                InboundSecurityProducts.Validate(product, channel, request);
                var receipt = await store.ImportSecurityAlertsAsync(tenant, organizationId,
                    identity.UserId, product, channel, request, token);
                return Results.Created($"/api/v1/organizations/{organizationId:D}/security-alerts/{product}/{channel}", Envelope(receipt));
            }
            catch (ArgumentException)
            {
                loggerFactory.CreateLogger("SecurityAlertImport").LogWarning("Rejected invalid security alert batch for organization {Organization}", organizationId);
                return Results.Problem(statusCode: 400, title: "Invalid security alert batch");
            }
            catch (JsonException)
            {
                loggerFactory.CreateLogger("SecurityAlertImport").LogWarning("Rejected malformed security alert JSON for organization {Organization}", organizationId);
                return Results.Problem(statusCode: 400, title: "Invalid security alert JSON");
            }
            catch (BadHttpRequestException exception) when (exception.StatusCode == 413)
            {
                loggerFactory.CreateLogger("SecurityAlertImport").LogWarning("Rejected oversized security alert batch for organization {Organization}", organizationId);
                return Results.Problem(statusCode: 413, title: "Security alert batch exceeds 2 MiB");
            }
            catch (InvalidOperationException)
            {
                loggerFactory.CreateLogger("SecurityAlertImport").LogWarning("Security alert evidence/cursor conflict for organization {Organization}", organizationId);
                return Results.Problem(statusCode: 409, title: "Security alert batch or cursor conflict");
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        }).RequireAuthorization("CiemApiAccess").WithName("ImportSecurityAlerts")
            .WithSummary("Import an explicitly enabled client SIEM bridge batch. Alerts never authorize or execute response actions.")
            .Accepts<ImportSecurityAlertsRequest>("application/json")
            .Produces<ApiEnvelope<SecurityAlertImportReceipt>>(201).Produces(400).Produces(403).Produces(404).Produces(409).Produces(413).Produces(415);
        app.MapGet(prefix, (ClaimsPrincipal principal, Guid organizationId, string product, string channel, int? limit, CancellationToken token) =>
            WithMonitoringAccess(principal, organizationId, store, false, async tenant =>
                Results.Ok(Envelope(await store.GetSecurityAlertsAsync(tenant, organizationId, product, channel, limit ?? 100, token)))))
            .RequireAuthorization("CiemApiAccess").WithName("GetSecurityAlerts").WithSummary("Read latest organization-scoped imported alert revisions.")
            .Produces<ApiEnvelope<IReadOnlyList<StoredSecurityAlert>>>().Produces(400).Produces(403).Produces(404);
        app.MapGet(prefix + "/cursor", (ClaimsPrincipal principal, Guid organizationId, string product, string channel, CancellationToken token) =>
            WithMonitoringAccess(principal, organizationId, store, false, async tenant =>
                Results.Ok(Envelope(await store.GetSecurityAlertCursorAsync(tenant, organizationId, product, channel, token)))))
            .RequireAuthorization("CiemApiAccess").WithName("GetSecurityAlertCursor").WithSummary("Read committed connector cursor without contacting a client SIEM.")
            .Produces<ApiEnvelope<SecurityAlertCursor>>().Produces(400).Produces(403).Produces(404);
    }

    private static async Task<T> ReadBoundedEvidenceAsync<T>(HttpRequest request, int maximumBytes, CancellationToken token) where T : class
    {
        if (request.ContentLength > maximumBytes)
            throw new BadHttpRequestException("Evidence request exceeds the size limit.", 413);
        var buffer = new byte[maximumBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await request.Body.ReadAsync(buffer.AsMemory(length), token);
            if (read == 0) break;
            length += read;
        }
        if (length > maximumBytes)
            throw new BadHttpRequestException("Evidence request exceeds the size limit.", 413);
        return JsonSerializer.Deserialize<T>(buffer.AsSpan(0, length), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new ArgumentException("Evidence payload is required.");
    }
}
