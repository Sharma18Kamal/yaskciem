using System.Text.Json;
using CloudEntitle.IdentityGovernance.Models;
using CloudEntitle.IdentityGovernance.Services;
using CloudEntitle.Integration.Contracts;
using Npgsql;

namespace CloudEntitle.LicenseServer.Services;

public sealed class GovernanceDeadlineWorker(SharedWorkspaceStore store, ILogger<GovernanceDeadlineWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await store.ProcessGovernanceDeadlinesAsync(DateTime.UtcNow, stoppingToken);
                if (processed < 50) await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Central governance deadline evaluation failed; work remains queued.");
                try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }
}

public sealed partial class SharedWorkspaceStore
{
    public async Task<int> ProcessGovernanceDeadlinesAsync(DateTime now, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var due = new List<(Guid Tenant, Guid Organization, AccessReviewCampaign Campaign)>();
        await using (var read = new NpgsqlCommand("""
            SELECT TenantId, OrganizationId, PayloadJson FROM SharedAccessReviewCampaigns
            WHERE NextGovernanceCheckAtUtc <= @now
            ORDER BY NextGovernanceCheckAtUtc, CampaignId FOR UPDATE SKIP LOCKED LIMIT 50;
            """, connection, transaction))
        {
            read.Parameters.AddWithValue("now", now);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                due.Add((reader.GetGuid(0), reader.GetGuid(1),
                    JsonSerializer.Deserialize<AccessReviewCampaign>(reader.GetString(2), JsonOptions)
                        ?? throw new InvalidOperationException("The stored governance campaign is invalid.")));
        }
        foreach (var entry in due)
        {
            var updated = SharedWorkspaceEndpoints.EnsureAuditMetadata(
                new IdentityGovernanceService().ReevaluateCampaign(entry.Campaign, now), Guid.NewGuid());
            await PersistAccessReviewAsync(connection, transaction, entry.Tenant, entry.Organization, updated, cancellationToken);
            // Use the evaluation clock so delayed/batched work is not immediately reclaimed.
            await using var next = new NpgsqlCommand("""
                UPDATE SharedAccessReviewCampaigns SET NextGovernanceCheckAtUtc=@next
                WHERE TenantId=@tenant AND OrganizationId=@organization AND CampaignId=@campaign
                  AND NextGovernanceCheckAtUtc IS NOT NULL;
                """, connection, transaction);
            next.Parameters.AddWithValue("next", now.AddMinutes(1));
            next.Parameters.AddWithValue("tenant", entry.Tenant);
            next.Parameters.AddWithValue("organization", entry.Organization);
            next.Parameters.AddWithValue("campaign", updated.Id);
            await next.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return due.Count;
    }

    public async Task<GovernanceReportDto> GetGovernanceReportAsync(Guid tenantId, Guid organizationId,
        DateTime? asOf = null, CancellationToken cancellationToken = default)
    {
        var now = asOf ?? DateTime.UtcNow;
        var campaigns = await GetAccessReviewsAsync(tenantId, organizationId, cancellationToken);
        var items = campaigns.Where(campaign => campaign.Status is not (CampaignStatus.Cancelled or CampaignStatus.Draft))
            .SelectMany(campaign => campaign.Items.Select(item =>
            {
                var unresolved = IdentityGovernanceService.IsUnresolved(item, now);
                var deadline = IdentityGovernanceService.EffectiveDeadline(item, campaign);
                var exception = item.Decision == ReviewDecision.ExceptionGranted;
                int? age = exception && item.DecidedAt.HasValue ? Math.Max(0, (int)(now - item.DecidedAt.Value).TotalDays) : null;
                return new GovernanceWorkItemDto(campaign.Id, campaign.Name, item.Id, item.Provider,
                    string.IsNullOrWhiteSpace(item.ExternalAccountId) ? "Unknown account" : item.ExternalAccountId,
                    string.IsNullOrWhiteSpace(item.Team) ? "Unassigned" : item.Team,
                    item.AssignedOwner, item.AssignedReviewer, item.Decision, deadline, !unresolved,
                    unresolved && deadline <= now, exception ? item.ExceptionExpiresAt : null,
                    exception && (!item.ExceptionExpiresAt.HasValue || item.ExceptionExpiresAt <= now || item.ExceptionExpiredAt.HasValue),
                    age, item.ReminderCount, item.ExceptionReminderCount, item.EscalatedAt);
            })).OrderByDescending(item => item.Overdue).ThenBy(item => item.DueAtUtc).ThenBy(item => item.ItemId).ToArray();
        var groups = items.GroupBy(item => (item.Provider, Account: item.Account.ToUpperInvariant(), Team: item.Team.ToUpperInvariant()))
            .Select(group =>
            {
                var first = group.First();
                var exceptions = group.Where(item => item.Decision == ReviewDecision.ExceptionGranted).ToArray();
                var completed = group.Count(item => item.Completed);
                return new GovernanceGroupDto(first.Provider, first.Account, first.Team, group.Count(), completed,
                    group.Count() - completed, group.Count(item => item.Overdue),
                    Math.Round(100m * completed / group.Count(), 2),
                    exceptions.Count(item => !item.ExceptionExpired), exceptions.Count(item => item.ExceptionExpired),
                    exceptions.Count(item => item.ExceptionAgeDays is < 30),
                    exceptions.Count(item => item.ExceptionAgeDays is >= 30 and < 90),
                    exceptions.Count(item => item.ExceptionAgeDays is >= 90), exceptions.Max(item => item.ExceptionAgeDays));
            }).OrderBy(group => group.Provider).ThenBy(group => group.Account).ThenBy(group => group.Team).ToArray();
        var history = campaigns.SelectMany(campaign => campaign.AuditTrail.Concat(campaign.Items.SelectMany(item => item.AuditTrail))
            .DistinctBy(entry => entry.EventId == Guid.Empty ? $"{entry.Timestamp:O}|{entry.ItemId}|{entry.Action}|{entry.Details}" : entry.EventId.ToString())
            .Select(entry => new GovernanceHistoryDto(campaign.Id, campaign.Name, entry.ItemId, entry)))
            .OrderByDescending(entry => entry.Event.Timestamp).Take(500).ToArray();
        return new GovernanceReportDto(now, items, groups, history);
    }
}
