using CloudEntitle.Integration.Clients;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.Infrastructure.Persistence;
using CloudEntitle.OrganizationManagement.Models;
using CloudEntitle.OrganizationManagement.Services;
using Microsoft.EntityFrameworkCore;

namespace CloudEntitle.Desktop.Services;

public interface ICentralAuditSyncService
{
    Task<int> SyncOrganizationAsync(Guid organizationId, CancellationToken cancellationToken = default);
}

public sealed class CentralAuditSyncService(
    IDbContextFactory<CloudEntitleDbContext> contextFactory,
    CiemApiClient apiClient,
    IApiAccessTokenProvider tokenProvider,
    OrganizationManagementService organizationService,
    AuthenticatedOrganizationContext authenticatedContext) : ICentralAuditSyncService
{
    public async Task<int> SyncOrganizationAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var identity = authenticatedContext.Identity;
        if (identity is null || authenticatedContext.OrganizationId != organizationId)
            throw new UnauthorizedAccessException("Sign in to the selected organization before syncing its audit events.");
        if (!organizationService.Authorize(organizationId, identity, OrganizationAction.ExportReports).Allowed)
            throw new UnauthorizedAccessException("Your organization role does not allow audit export.");

        var accessToken = await tokenProvider.GetApiAccessTokenAsync(cancellationToken);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var events = await context.AuditEvents.AsNoTracking()
            .Where(entry => entry.OrganizationId == organizationId)
            .OrderBy(entry => entry.Timestamp)
            .ThenBy(entry => entry.Id)
            .ToListAsync(cancellationToken);

        foreach (var entry in events)
        {
            var metadata = new Dictionary<string, string>(entry.Metadata, StringComparer.Ordinal)
            {
                ["sourceOrganizationId"] = organizationId.ToString("D"),
                ["sourceLocalEventId"] = entry.Id.ToString("D"),
                ["sourceUserId"] = entry.UserId?.ToString("D") ?? string.Empty
            };
            await apiClient.AppendAuditEventAsync(new CentralAuditAppendRequest(
                entry.EventType, entry.Description, entry.Timestamp, metadata, entry.Id), accessToken, cancellationToken);
        }

        return events.Count;
    }
}
