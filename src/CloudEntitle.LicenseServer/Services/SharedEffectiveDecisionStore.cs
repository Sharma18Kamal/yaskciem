using System.Text;
using System.Text.Json;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.Integration.Services;
using Npgsql;

namespace CloudEntitle.LicenseServer.Services;

public sealed partial class SharedWorkspaceStore
{
    internal async Task SaveNativeEffectiveDecisionAsync(Guid tenant, Guid organization, Guid user,
        NativeEffectiveDecisionCapture capture, CancellationToken token)
    {
        if (!VerifyNativeEffectiveDecision(capture))
            throw new ArgumentException("The directly collected native evidence projection is invalid.");
        var snapshot = capture.Snapshot;
        var payload = JsonSerializer.Serialize(capture, JsonOptions);
        if (Encoding.UTF8.GetByteCount(payload) > 2 * 1024 * 1024)
            throw new ArgumentException("Native decision evidence exceeds the bounded store limit.");
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            INSERT INTO SharedEffectiveDecisionSnapshots
              (TenantId,OrganizationId,SnapshotId,SourceId,AccountId,StartedAtUtc,CompletedAtUtc,CollectedBy,PayloadJson)
            VALUES (@tenant,@organization,@snapshot,@source,@account,@started,@completed,@user,@payload)
            ON CONFLICT DO NOTHING;
            SELECT PayloadJson FROM SharedEffectiveDecisionSnapshots
            WHERE TenantId=@tenant AND OrganizationId=@organization AND SnapshotId=@snapshot;
            """, connection);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("organization", organization);
        command.Parameters.AddWithValue("snapshot", snapshot.SnapshotId);
        command.Parameters.AddWithValue("source", snapshot.SourceId);
        command.Parameters.AddWithValue("account", snapshot.AccountId);
        command.Parameters.AddWithValue("started", snapshot.StartedAtUtc.ToUniversalTime());
        command.Parameters.AddWithValue("completed", snapshot.CompletedAtUtc.ToUniversalTime());
        command.Parameters.AddWithValue("user", user);
        command.Parameters.AddWithValue("payload", payload);
        if (await command.ExecuteScalarAsync(token) as string != payload)
            throw new InvalidOperationException("Native decision evidence conflicts with immutable stored evidence.");
    }

    internal async Task<NativeEffectiveDecisionCapture?> GetNativeEffectiveDecisionAsync(Guid tenant, Guid organization,
        string source, Guid snapshot, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT PayloadJson FROM SharedEffectiveDecisionSnapshots
            WHERE TenantId=@tenant AND OrganizationId=@organization AND SourceId=@source AND SnapshotId=@snapshot;
            """, connection);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("organization", organization);
        command.Parameters.AddWithValue("source", source);
        command.Parameters.AddWithValue("snapshot", snapshot);
        var payload = await command.ExecuteScalarAsync(token) as string;
        return payload is null ? null : JsonSerializer.Deserialize<NativeEffectiveDecisionCapture>(payload, JsonOptions);
    }

    internal async Task<IReadOnlyList<EffectiveDecisionSnapshot>> GetEffectiveDecisionHistoryAsync(Guid tenant,
        Guid organization, string source, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT PayloadJson FROM SharedEffectiveDecisionSnapshots
            WHERE TenantId=@tenant AND OrganizationId=@organization AND SourceId=@source
            ORDER BY CompletedAtUtc DESC,SnapshotId DESC LIMIT 50;
            """, connection);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("organization", organization);
        command.Parameters.AddWithValue("source", source);
        var result = new List<EffectiveDecisionSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var capture = JsonSerializer.Deserialize<NativeEffectiveDecisionCapture>(reader.GetString(0), JsonOptions)
                ?? throw new InvalidOperationException("Stored native decision is invalid.");
            result.Add(capture.Snapshot);
        }
        return result;
    }

    internal async Task<DecisionEscalationReport> SaveDecisionEscalationAsync(Guid tenant, Guid organization,
        string source, EffectiveDecisionSnapshot after, DecisionEscalationReport report, CancellationToken token)
    {
        report = report with { AnalysisId = OperationalId($"native-decision-analysis|{tenant}|{organization}|{source}|{report.BeforeSnapshotId}|{report.AfterSnapshotId}") };
        await using var connection = await OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using var command = new NpgsqlCommand("""
            INSERT INTO SharedDecisionEscalationAnalyses
              (TenantId,OrganizationId,AnalysisId,SourceId,BeforeSnapshotId,AfterSnapshotId,AnalyzedAtUtc,PayloadJson)
            VALUES (@tenant,@organization,@analysis,@source,@before,@after,@analyzed,@payload)
            ON CONFLICT DO NOTHING RETURNING 1;
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("organization", organization);
        command.Parameters.AddWithValue("analysis", report.AnalysisId);
        command.Parameters.AddWithValue("source", source);
        command.Parameters.AddWithValue("before", report.BeforeSnapshotId);
        command.Parameters.AddWithValue("after", report.AfterSnapshotId);
        command.Parameters.AddWithValue("analyzed", report.AnalyzedAtUtc.ToUniversalTime());
        command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(report, JsonOptions));
        var inserted = await command.ExecuteScalarAsync(token) is not null;
        if (!inserted)
        {
            await using var existing = new NpgsqlCommand("""
                SELECT PayloadJson FROM SharedDecisionEscalationAnalyses
                WHERE TenantId=@tenant AND OrganizationId=@organization AND SourceId=@source
                  AND BeforeSnapshotId=@before AND AfterSnapshotId=@after;
                """, connection, transaction);
            existing.Parameters.AddWithValue("tenant", tenant);
            existing.Parameters.AddWithValue("organization", organization);
            existing.Parameters.AddWithValue("source", source);
            existing.Parameters.AddWithValue("before", report.BeforeSnapshotId);
            existing.Parameters.AddWithValue("after", report.AfterSnapshotId);
            var saved = JsonSerializer.Deserialize<DecisionEscalationReport>((string)(await existing.ExecuteScalarAsync(token))!, JsonOptions)!;
            if (saved.Status == "ConfirmedDecisionEscalation" && report.Status != saved.Status)
                throw new InvalidOperationException("The saved analysis cannot be reissued as a fresh confirmation under changed comparison conditions.");
            report = saved;
        }
        else if (report.Status == "ConfirmedDecisionEscalation")
        {
            var rules = await ReadRulesAsync(connection, transaction, tenant, organization, token);
            var id = OperationalId($"native-decision-event|{tenant}|{organization}|{report.AnalysisId}");
            await RecordOperationalEventAsync(connection, transaction, tenant, organization,
                new(id, after.SnapshotId, report.BeforeSnapshotId, after.Provider, after.AccountId,
                    "effective-access", "Added", $"ConfirmedDecisionEscalation|{id:D}",
                    SharedAlertSeverity.High, null, after.ResponseSha256, report.AnalyzedAtUtc.UtcDateTime)
                {
                    EventType = "privilege-escalation.confirmeddecision",
                    SubjectId = report.AnalysisId
                }, token, rules);
        }
        await transaction.CommitAsync(token);
        return report;
    }

    internal async Task<DecisionEscalationReport?> GetDecisionEscalationAsync(Guid tenant, Guid organization,
        string source, Guid analysis, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT PayloadJson FROM SharedDecisionEscalationAnalyses
            WHERE TenantId=@tenant AND OrganizationId=@organization AND SourceId=@source AND AnalysisId=@analysis;
            """, connection);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("organization", organization);
        command.Parameters.AddWithValue("source", source);
        command.Parameters.AddWithValue("analysis", analysis);
        var payload = await command.ExecuteScalarAsync(token) as string;
        return payload is null ? null : JsonSerializer.Deserialize<DecisionEscalationReport>(payload, JsonOptions);
    }

    internal static bool VerifyNativeEffectiveDecision(NativeEffectiveDecisionCapture capture)
    {
        try
        {
            var s = capture.Snapshot;
            if (s.SnapshotId == Guid.Empty || s.VerificationProfile != GcpEffectiveDecisionEvaluator.VerificationProfile
                || s.StartedAtUtc < DateTimeOffset.UnixEpoch || s.CompletedAtUtc < s.StartedAtUtc
                || s.CompletedAtUtc - s.StartedAtUtc > TimeSpan.FromSeconds(30)
                || s.ContextObservedAtUtc < s.StartedAtUtc || s.ContextObservedAtUtc > s.CompletedAtUtc
                || GcpEffectiveDecisionEvaluator.Sha256(capture.NativeResponseJson) != s.ResponseSha256
                || capture.ResourceHierarchy.Count is < 1 or > 12)
                return false;
            var evaluation = GcpEffectiveDecisionEvaluator.Evaluate(capture.NativeResponseJson,
                new(s.SourceId, s.Principal, s.Permission, s.FullResourceName, false), capture.ResourceHierarchy);
            return s.Decision == evaluation.Decision && s.Coverage == evaluation.Coverage
                && s.ContextSha256 == evaluation.ContextSha256
                && s.PolicyVersions.SequenceEqual(evaluation.PolicyVersions)
                && s.Blockers.SequenceEqual(evaluation.Blockers);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        { return false; }
    }
}
