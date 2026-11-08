using CloudEntitle.Integration.Contracts;
using Npgsql;

namespace CloudEntitle.LicenseServer.Services;

public sealed partial class SharedWorkspaceStore
{
    public async Task RecordSecurityAnalysisAsync(Guid tenant, Guid organization, SecurityAnalysisRequest request,
        SecurityAnalysisReport report, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        var rules = await ReadRulesAsync(connection, transaction, tenant, organization, token);
        foreach (var anomaly in report.Anomalies)
        {
            var id = OperationalId($"behavior|{tenant}|{organization}|{request.Provider}|{request.AccountId}|{anomaly.EventId}|{anomaly.Principal}|{anomaly.Kind}");
            await RecordOperationalEventAsync(connection, transaction, tenant, organization,
                new(id, request.ActivitySnapshotId ?? Guid.Empty, Guid.Empty, request.Provider, request.AccountId, "behavior", "Added",
                    $"{anomaly.Kind}|{id:D}", SharedAlertSeverity.Medium, null, null, anomaly.AtUtc.UtcDateTime)
                    { EventType = "behavior.anomaly", SubjectId = request.ActivitySnapshotId }, token, rules);
        }
        foreach (var change in report.Changes)
        {
            var id = OperationalId($"documents|{tenant}|{organization}|{request.EntitlementSnapshotId}|{change.Principal}|{change.SourceId}|{change.Kind}|{change.Before}|{change.After}");
            await RecordOperationalEventAsync(connection, transaction, tenant, organization,
                new(id, request.EntitlementSnapshotId ?? Guid.Empty, Guid.Empty, request.Provider, request.AccountId, "permission-document", "Changed",
                    $"{change.Kind}|{id:D}", SharedAlertSeverity.Medium, null, null, DateTime.UtcNow)
                    { EventType = "documents.observedchange", SubjectId = request.EntitlementSnapshotId }, token, rules);
        }
        await transaction.CommitAsync(token);
    }
}
