using System.Text.Json;
using CloudEntitle.Application.Audit;

namespace CloudEntitle.Application.Connectors;

/// <summary>Every operation is scoped by organization; implementations must never return another tenant's rows.</summary>
public interface IConnectorResultStore
{
    Task SaveAsync(ConnectorResult result, CancellationToken cancellationToken = default);

    Task<ConnectorResult?> GetLatestAsync(Guid organizationId, string provider, string externalId, CancellationToken cancellationToken = default);
}

public sealed class InMemoryConnectorResultStore : IConnectorResultStore
{
    private readonly object _gate = new();
    private readonly List<ConnectorResult> _results = [];

    public Task SaveAsync(ConnectorResult result, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _results.Add(result);
        }

        return Task.CompletedTask;
    }

    public Task<ConnectorResult?> GetLatestAsync(Guid organizationId, string provider, string externalId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_results
                .Where(r => r.Scope.OrganizationId == organizationId && r.Scope.Provider == provider && r.Scope.ExternalId == externalId)
                .OrderByDescending(r => r.CollectedAt)
                .FirstOrDefault());
        }
    }
}

/// <summary>
/// Central scanner: runs the connector for a scope, persists the result, and writes an audit
/// entry before and after the scan so failed or interrupted scans are still evidenced.
/// </summary>
public sealed class CentralScanner
{
    public const string Actor = "central-scanner";

    private readonly IReadOnlyDictionary<string, IConnector> _connectors;
    private readonly IConnectorResultStore _store;
    private readonly IAuditLog _audit;

    public CentralScanner(IEnumerable<IConnector> connectors, IConnectorResultStore store, IAuditLog audit)
    {
        _connectors = connectors.ToDictionary(c => c.Provider, StringComparer.OrdinalIgnoreCase);
        _store = store;
        _audit = audit;
    }

    public async Task<ConnectorResult> ScanAsync(ConnectorScope scope, CancellationToken cancellationToken = default)
    {
        var target = JsonSerializer.Serialize(new { scope.Provider, scope.ExternalId });
        await _audit.AppendAsync(scope.OrganizationId, Actor, "scan.started", target, cancellationToken);

        if (!_connectors.TryGetValue(scope.Provider, out var connector))
        {
            var unsupported = new ConnectorResult(scope, DateTimeOffset.UtcNow, [],
                [new CoverageEntry("connector", CoverageStatus.Unsupported, $"No connector for '{scope.Provider}'.")]);
            await FinishAsync(scope, unsupported, cancellationToken);
            return unsupported;
        }

        ConnectorResult result;
        try
        {
            result = await connector.DiscoverAsync(scope, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _audit.AppendAsync(scope.OrganizationId, Actor, "scan.failed",
                JsonSerializer.Serialize(new { scope.Provider, scope.ExternalId, error = ex.GetType().Name }), cancellationToken);
            throw;
        }

        await FinishAsync(scope, result, cancellationToken);
        return result;
    }

    private async Task FinishAsync(ConnectorScope scope, ConnectorResult result, CancellationToken cancellationToken)
    {
        await _store.SaveAsync(result, cancellationToken);
        var summary = JsonSerializer.Serialize(new
        {
            scope.Provider,
            scope.ExternalId,
            identities = result.Identities.Count,
            coverage = result.Coverage.Select(c => new { c.Area, status = c.Status.ToString() })
        });
        await _audit.AppendAsync(scope.OrganizationId, Actor, "scan.completed", summary, cancellationToken);
    }
}
