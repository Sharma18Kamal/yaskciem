using CloudEntitle.Domain;
using CloudEntitle.IdentityGovernance.Models;
using CloudEntitle.IdentityGovernance.Services;
using CloudEntitle.LicenseServer.Services;
using Npgsql;
using System.Text.Json;
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed class CentralGovernanceTests
{
    [PostgreSqlFact]
    public async Task DeadlineMigration_ShouldBackfillLegacyWorkAndLimitClaimsToFiftyCampaigns()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var legacy = new NpgsqlCommand("""
                CREATE TABLE SharedAccessReviewCampaigns (
                    TenantId uuid NOT NULL, OrganizationId uuid NOT NULL, CampaignId uuid NOT NULL,
                    CreatedAtUtc timestamptz NOT NULL, PayloadJson text NOT NULL,
                    PRIMARY KEY (TenantId, OrganizationId, CampaignId));
                """, connection);
            await legacy.ExecuteNonQueryAsync();
            for (var index = 0; index < 51; index++)
            {
                var campaign = new IdentityGovernanceService().CreateCampaign("Legacy", new[] { Candidate("review", "Platform") },
                    TimeSpan.FromDays(2), asOf: DateTime.UtcNow.AddDays(-3));
                await using var insert = new NpgsqlCommand("""
                    INSERT INTO SharedAccessReviewCampaigns VALUES (@tenant, @organization, @campaign, @created, @payload);
                    """, connection);
                insert.Parameters.AddWithValue("tenant", Guid.NewGuid());
                insert.Parameters.AddWithValue("organization", Guid.NewGuid());
                insert.Parameters.AddWithValue("campaign", campaign.Id);
                insert.Parameters.AddWithValue("created", campaign.CreatedAt);
                insert.Parameters.AddWithValue("payload", JsonSerializer.Serialize(campaign));
                await insert.ExecuteNonQueryAsync();
            }
        }
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        await store.InitializeAsync();
        var now = DateTime.UtcNow.AddSeconds(1);
        Assert.Equal(50, await store.ProcessGovernanceDeadlinesAsync(now));
        Assert.Equal(1, await store.ProcessGovernanceDeadlinesAsync(now));
        Assert.Equal(0, await store.ProcessGovernanceDeadlinesAsync(now));
    }

    [PostgreSqlFact]
    public async Task DeadlineQueue_ShouldEvaluateWithoutReadsDeduplicateAcrossWorkersAndRetainHistory()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        using var otherWorker = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        Assert.True(await store.IsReadyAsync());
        var tenant = Guid.NewGuid();
        var organization = Guid.NewGuid();
        var now = DateTime.UtcNow.AddMinutes(5);
        var governance = new IdentityGovernanceService();
        var campaign = governance.CreateCampaign("Deadlines", new[]
        {
            Candidate("review", "Platform"),
            Candidate("exception", "Platform"),
            Candidate("overdue", "Security")
        }, TimeSpan.FromDays(10), asOf: now.AddDays(-8));
        var pending = campaign.Items[0];
        var exception = campaign.Items[1] with { Decision = ReviewDecision.ExceptionGranted, DecidedAt = now.AddDays(-8),
            ExceptionExpiresAt = now.AddDays(2) };
        var overdue = campaign.Items[2] with { Decision = ReviewDecision.NeedsInvestigation, DueAt = now.AddSeconds(-1) };
        campaign = campaign with { Items = new[] { pending, exception, overdue } };
        await store.SaveAccessReviewAsync(tenant, organization, campaign);
        var claimed = await Task.WhenAll(store.ProcessGovernanceDeadlinesAsync(now), otherWorker.ProcessGovernanceDeadlinesAsync(now));
        Assert.Equal(1, claimed.Sum());
        var evaluated = Assert.Single(await store.GetAccessReviewsAsync(tenant, organization));
        Assert.Equal(1, evaluated.Items[0].ReminderCount);
        Assert.Equal(1, evaluated.Items[1].ExceptionReminderCount);
        Assert.Equal(CampaignStatus.Escalated, evaluated.Status);
        Assert.NotNull(evaluated.Items[2].EscalatedAt);
        var audit = await GetAuditAsync(database.ConnectionString, tenant, organization);
        Assert.All(audit, entry => { Assert.NotEqual(Guid.Empty, entry.EventId); Assert.NotEqual(Guid.Empty, entry.CorrelationId); });
        Assert.Equal(0, await store.ProcessGovernanceDeadlinesAsync(now));
        Assert.Equal(1, await store.ProcessGovernanceDeadlinesAsync(now.AddMinutes(2)));
        Assert.Equal(audit.Count, (await GetAuditAsync(database.ConnectionString, tenant, organization)).Count);
        Assert.Equal(1, await store.ProcessGovernanceDeadlinesAsync(now.AddDays(2)));
        evaluated = Assert.Single(await store.GetAccessReviewsAsync(tenant, organization));
        Assert.NotNull(evaluated.Items[1].ExceptionExpiredAt);
        Assert.NotNull(evaluated.Items[1].EscalatedAt);
        Assert.Contains(await GetAuditAsync(database.ConnectionString, tenant, organization),
            entry => entry.ItemId == exception.Id && entry.Action == "ExceptionExpired");
        var count = (await GetAuditAsync(database.ConnectionString, tenant, organization)).Count;
        await store.ProcessGovernanceDeadlinesAsync(now.AddDays(2).AddMinutes(2));
        Assert.Equal(count, (await GetAuditAsync(database.ConnectionString, tenant, organization)).Count);
        await store.UpdateAccessReviewAsync(tenant, organization, campaign.Id, current => current! with { Status = CampaignStatus.Cancelled });
        Assert.Equal(0, await store.ProcessGovernanceDeadlinesAsync(now.AddDays(3)));
    }

    [PostgreSqlFact]
    public async Task Report_ShouldGroupAccountTeamsAndMeasureExactCompletionExpiryAndAgeBoundaries()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        var tenant = Guid.NewGuid();
        var organization = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var campaign = new IdentityGovernanceService().CreateCampaign("Report",
            Enumerable.Range(0, 7).Select(index => Candidate(index.ToString(), "Platform")).ToArray(), TimeSpan.FromDays(5), asOf: now);
        var items = campaign.Items.ToArray();
        items[0] = items[0] with { Decision = ReviewDecision.Certified };
        items[1] = items[1] with { Decision = ReviewDecision.NeedsInvestigation, DueAt = now };
        items[2] = items[2] with { Decision = ReviewDecision.ExceptionGranted, DecidedAt = now.AddDays(-29), ExceptionExpiresAt = now.AddTicks(1) };
        items[3] = items[3] with { Decision = ReviewDecision.ExceptionGranted, DecidedAt = now.AddDays(-30), ExceptionExpiresAt = now };
        items[4] = items[4] with { Decision = ReviewDecision.ExceptionGranted, DecidedAt = now.AddDays(-89), ExceptionExpiresAt = now.AddDays(1) };
        items[5] = items[5] with { Decision = ReviewDecision.ExceptionGranted, DecidedAt = now.AddDays(-90), ExceptionExpiresAt = now.AddDays(-1) };
        items[6] = items[6] with { ExternalAccountId = null, Team = null };
        await store.SaveAccessReviewAsync(tenant, organization, campaign with { Items = items });
        await store.SaveAccessReviewAsync(tenant, organization, campaign with { Id = Guid.NewGuid(), Status = CampaignStatus.Cancelled });
        var report = await store.GetGovernanceReportAsync(tenant, organization, now);
        var group = Assert.Single(report.Groups, entry => entry.Account == "123456789012");
        Assert.Equal(6, group.Total);
        Assert.Equal(3, group.Completed);
        Assert.Equal(3, group.Unresolved);
        Assert.Equal(3, group.Overdue);
        Assert.Equal(50m, group.CompletionPercent);
        Assert.Equal(2, group.ActiveExceptions);
        Assert.Equal(2, group.ExpiredExceptions);
        Assert.Equal(1, group.ExceptionsUnder30Days);
        Assert.Equal(2, group.Exceptions30To89Days);
        Assert.Equal(1, group.Exceptions90DaysOrOlder);
        Assert.Equal(90, group.OldestExceptionDays);
        Assert.Equal("Unassigned", Assert.Single(report.Groups, entry => entry.Account == "Unknown account").Team);
        Assert.Empty((await store.GetGovernanceReportAsync(Guid.NewGuid(), organization, now)).Items);
        Assert.Empty((await store.GetGovernanceReportAsync(tenant, Guid.NewGuid(), now)).Items);
    }

    private static AccessReviewCandidate Candidate(string name, string team)
        => new(Guid.NewGuid(), Guid.NewGuid(), null, CloudProvider.Aws, name, "Immutable evidence")
            { ExternalAccountId = "123456789012", Team = team };

    private static async Task<IReadOnlyList<AccessReviewAuditEntry>> GetAuditAsync(string connectionString, Guid tenant, Guid organization)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT EventId, CorrelationId, ItemId, Action FROM SharedAccessReviewAuditEvents
            WHERE TenantId=@tenant AND OrganizationId=@organization;
            """, connection);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("organization", organization);
        await using var reader = await command.ExecuteReaderAsync();
        var events = new List<AccessReviewAuditEntry>();
        while (await reader.ReadAsync())
            events.Add(new AccessReviewAuditEntry(DateTime.UtcNow, "test", reader.GetString(3), "")
            { EventId = reader.GetGuid(0), CorrelationId = reader.GetGuid(1), ItemId = reader.IsDBNull(2) ? null : reader.GetGuid(2) });
        return events;
    }
}
