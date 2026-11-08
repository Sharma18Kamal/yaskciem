using System.Text.Json;
using CloudEntitle.Integration.Contracts;
using Npgsql;

namespace CloudEntitle.LicenseServer.Services;

public sealed partial class SharedWorkspaceStore
{
    public async Task<IReadOnlyList<BreakGlassReport>> GetBreakGlassReportsAsync(Guid tenant, Guid organization,
        CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT r.PayloadJson,v.PayloadJson FROM SharedBreakGlassReports r LEFT JOIN SharedBreakGlassReviews v
              USING (TenantId,OrganizationId,ReportId)
            WHERE r.TenantId=@tenant AND r.OrganizationId=@organization
            ORDER BY r.ReportedAtUtc DESC,r.ReportId LIMIT 100;
            """, connection);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("organization", organization);
        var reports = new List<BreakGlassReport>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            reports.Add(ReadBreakGlassReport(reader.GetString(0)) with
            { Review = reader.IsDBNull(1) ? null : ReadBreakGlassReview(reader.GetString(1)) });
        return reports;
    }

    public async Task<BreakGlassReport?> GetBreakGlassReportAsync(Guid tenant, Guid organization, Guid reportId,
        CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT r.PayloadJson,v.PayloadJson FROM SharedBreakGlassReports r LEFT JOIN SharedBreakGlassReviews v
              USING (TenantId,OrganizationId,ReportId)
            WHERE r.TenantId=@tenant AND r.OrganizationId=@organization AND r.ReportId=@report;
            """, connection);
        AddBreakGlassScope(command, tenant, organization, reportId);
        await using var reader = await command.ExecuteReaderAsync(token);
        return !await reader.ReadAsync(token) ? null : ReadBreakGlassReport(reader.GetString(0)) with
        { Review = reader.IsDBNull(1) ? null : ReadBreakGlassReview(reader.GetString(1)) };
    }

    public async Task<BreakGlassReport> SaveBreakGlassReportAsync(Guid tenant, Guid organization,
        CreateBreakGlassReportRequest request, BreakGlassReport report, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        var requestJson = JsonSerializer.Serialize(request, JsonOptions);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO SharedBreakGlassReports(TenantId,OrganizationId,ReportId,ReportedAtUtc,RequestJson,PayloadJson)
            VALUES(@tenant,@organization,@report,@now,@request,@payload)
            ON CONFLICT (TenantId,OrganizationId,ReportId) DO NOTHING;
            """, connection, transaction);
        AddBreakGlassScope(insert, tenant, organization, report.ReportId);
        insert.Parameters.AddWithValue("now", report.ReportedAtUtc);
        insert.Parameters.AddWithValue("request", requestJson);
        insert.Parameters.AddWithValue("payload", JsonSerializer.Serialize(report, JsonOptions));
        var inserted = await insert.ExecuteNonQueryAsync(token) == 1;
        await using var select = new NpgsqlCommand("""
            SELECT r.RequestJson,r.PayloadJson,v.PayloadJson FROM SharedBreakGlassReports r
            LEFT JOIN SharedBreakGlassReviews v USING (TenantId,OrganizationId,ReportId)
            WHERE r.TenantId=@tenant AND r.OrganizationId=@organization AND r.ReportId=@report;
            """, connection, transaction);
        AddBreakGlassScope(select, tenant, organization, report.ReportId);
        BreakGlassReport saved;
        await using (var reader = await select.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token)) throw new InvalidOperationException("Emergency report was not persisted.");
            saved = ReadBreakGlassReport(reader.GetString(1)) with
            { Review = reader.IsDBNull(2) ? null : ReadBreakGlassReview(reader.GetString(2)) };
            if (reader.GetString(0) != requestJson || saved.ReportedBy != report.ReportedBy)
                throw new InvalidOperationException("Report ID already contains different evidence or reporter.");
        }
        if (inserted)
            await RecordOperationalEventAsync(connection, transaction, tenant, organization,
                new(OperationalId($"breakglass|{tenant}|{organization}|{report.ReportId}|reported"), Guid.Empty, Guid.Empty,
                    report.Provider, report.AccountId, "emergency-account", "Reported", report.ReportId.ToString("D"),
                    SharedAlertSeverity.High, null, null, report.ReportedAtUtc.UtcDateTime)
                { EventType = "breakglass.reported", SubjectId = report.ReportId }, token);
        await transaction.CommitAsync(token);
        return saved;
    }

    public async Task<BreakGlassReport> ReviewBreakGlassReportAsync(Guid tenant, Guid organization, Guid reportId,
        Guid reviewer, ReviewBreakGlassReportRequest request, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using var select = new NpgsqlCommand("""
            SELECT PayloadJson FROM SharedBreakGlassReports
            WHERE TenantId=@tenant AND OrganizationId=@organization AND ReportId=@report;
            """, connection, transaction);
        AddBreakGlassScope(select, tenant, organization, reportId);
        var report = ReadBreakGlassReport(await select.ExecuteScalarAsync(token) as string ?? throw new KeyNotFoundException());
        if (report.ReportedBy == reviewer) throw new UnauthorizedAccessException("Emergency reports require independent review.");
        var review = new BreakGlassReview(reviewer, DateTimeOffset.UtcNow, request.Reason, request.ClientClosureReference,
            request.ClientVerifiedClosure);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO SharedBreakGlassReviews(TenantId,OrganizationId,ReportId,PayloadJson)
            VALUES(@tenant,@organization,@report,@payload)
            ON CONFLICT (TenantId,OrganizationId,ReportId) DO NOTHING;
            """, connection, transaction);
        AddBreakGlassScope(insert, tenant, organization, reportId);
        insert.Parameters.AddWithValue("payload", JsonSerializer.Serialize(review, JsonOptions));
        if (await insert.ExecuteNonQueryAsync(token) != 1)
            throw new InvalidOperationException("This incident already has an immutable security review. File a new linked report for corrections.");
        await RecordOperationalEventAsync(connection, transaction, tenant, organization,
            new(OperationalId($"breakglass|{tenant}|{organization}|{reportId}|reviewed"), Guid.Empty, Guid.Empty,
                report.Provider, report.AccountId, "emergency-account", "Reviewed", reportId.ToString("D"),
                request.ClientVerifiedClosure ? SharedAlertSeverity.Medium : SharedAlertSeverity.High,
                null, null, review.ReviewedAtUtc.UtcDateTime)
            { EventType = request.ClientVerifiedClosure ? "breakglass.clientclosurereviewed" : "breakglass.followuprequired", SubjectId = reportId }, token);
        await transaction.CommitAsync(token);
        return report with { Review = review };
    }

    private static BreakGlassReport ReadBreakGlassReport(string json)
        => JsonSerializer.Deserialize<BreakGlassReport>(json, JsonOptions) ?? throw new InvalidOperationException("Stored emergency report is invalid.");
    private static BreakGlassReview ReadBreakGlassReview(string json)
        => JsonSerializer.Deserialize<BreakGlassReview>(json, JsonOptions) ?? throw new InvalidOperationException("Stored emergency review is invalid.");
    private static void AddBreakGlassScope(NpgsqlCommand command, Guid tenant, Guid organization, Guid report)
    {
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("organization", organization);
        command.Parameters.AddWithValue("report", report);
    }
}
