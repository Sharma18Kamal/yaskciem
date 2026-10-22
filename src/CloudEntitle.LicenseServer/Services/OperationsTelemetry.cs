using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http;

namespace CloudEntitle.LicenseServer.Services;

public sealed record OperationsSnapshot(
    long ActiveScanJobs,
    long QueuedScanJobs,
    double OldestQueuedScanSeconds,
    double? IngestionLagSeconds,
    double? LastScanCompletionAgeSeconds,
    long SuccessfulScans24Hours,
    long PartialScans24Hours,
    long FailedScans24Hours,
    long PendingDeliveries,
    long DeadLetterDeliveries,
    long ConnectorFailures24Hours,
    DateTime CollectedAtUtc);

public sealed record WorkerHeartbeat(DateTime LastHeartbeatUtc, long Failures, DateTime? LastSuccessUtc);

public sealed class WorkerHealthRegistry
{
    private readonly ConcurrentDictionary<string, WorkerHeartbeat> _workers = new(StringComparer.Ordinal);

    public void Heartbeat(string name, bool success = false, bool failed = false)
        => _workers.AddOrUpdate(name,
            _ => new(DateTime.UtcNow, failed ? 1 : 0, success ? DateTime.UtcNow : null),
            (_, previous) => new(DateTime.UtcNow, previous.Failures + (failed ? 1 : 0),
                success ? DateTime.UtcNow : previous.LastSuccessUtc));

    public IReadOnlyDictionary<string, WorkerHeartbeat> Snapshot()
        => new Dictionary<string, WorkerHeartbeat>(_workers, StringComparer.Ordinal);
}

public sealed class OperationsMetrics : IDisposable
{
    private readonly Meter _meter = new("YASK.CIEM.Operations", "1.3.0");
    private readonly Counter<long> _requestCount;
    private readonly Histogram<double> _requestDuration;
    private readonly Counter<long> _scanOutcomes;
    private readonly Counter<long> _connectorFailures;
    private readonly ConcurrentDictionary<string, (long Count, double Sum, long Failures)> _routes = new(StringComparer.Ordinal);
    private OperationsSnapshot? _snapshot;

    public OperationsMetrics()
    {
        _requestCount = _meter.CreateCounter<long>("yask.api.requests", "requests");
        _requestDuration = _meter.CreateHistogram<double>("yask.api.request.duration", "ms");
        _scanOutcomes = _meter.CreateCounter<long>("yask.scan.jobs.completed", "jobs");
        _connectorFailures = _meter.CreateCounter<long>("yask.connector.delivery.failures", "deliveries");
        _meter.CreateObservableGauge("yask.scan.queue.depth", () => _snapshot?.QueuedScanJobs ?? 0, "jobs");
        _meter.CreateObservableGauge("yask.scan.jobs.active", () => _snapshot?.ActiveScanJobs ?? 0, "jobs");
        _meter.CreateObservableGauge("yask.scan.queue.oldest.age", () => _snapshot?.OldestQueuedScanSeconds ?? 0, "s");
        _meter.CreateObservableGauge("yask.scan.ingestion.lag", () => _snapshot?.IngestionLagSeconds ?? 0, "s");
        _meter.CreateObservableGauge("yask.connector.deliveries.dead_letter", () => _snapshot?.DeadLetterDeliveries ?? 0, "deliveries");
    }

    public void RecordApiRequest(string method, string route, int statusCode, double elapsedMilliseconds)
    {
        var boundedMethod = method is "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS" ? method : "OTHER";
        var normalized = $"{boundedMethod} {route}";
        var failed = statusCode >= 500 ? 1L : 0L;
        _routes.AddOrUpdate(normalized, (1, elapsedMilliseconds, failed),
            (_, previous) => (previous.Count + 1, previous.Sum + elapsedMilliseconds, previous.Failures + failed));
        var tags = new TagList { { "http.request.method", boundedMethod }, { "http.route", route }, { "http.response.status_code", statusCode } };
        _requestCount.Add(1, tags);
        _requestDuration.Record(elapsedMilliseconds, tags);
    }

    public void RecordScanOutcome(string provider, string outcome)
        => _scanOutcomes.Add(1, new TagList { { "cloud.provider", provider }, { "scan.outcome", outcome } });

    public void RecordConnectorFailure(string connector, string? failureCode)
        => _connectorFailures.Add(1, new TagList { { "connector.kind", connector }, { "failure.code", failureCode ?? "unknown" } });

    public void SetSnapshot(OperationsSnapshot snapshot) => Volatile.Write(ref _snapshot, snapshot);
    public OperationsSnapshot? Current => Volatile.Read(ref _snapshot);

    public string ToPrometheusText()
    {
        var builder = new StringBuilder();
        builder.AppendLine("# HELP yask_api_requests_total API requests by bounded route template.")
            .AppendLine("# TYPE yask_api_requests_total counter");
        foreach (var route in _routes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var labels = route.Key.Split(' ', 2);
            var escaped = labels[1].Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
            builder.Append("yask_api_requests_total{method=\"").Append(labels[0]).Append("\",route=\"")
                .Append(escaped).Append("\"} ").AppendLine(route.Value.Count.ToString(CultureInfo.InvariantCulture));
            builder.Append("yask_api_request_duration_ms_sum{method=\"").Append(labels[0]).Append("\",route=\"")
                .Append(escaped).Append("\"} ").AppendLine(route.Value.Sum.ToString("R", CultureInfo.InvariantCulture));
            builder.Append("yask_api_request_failures_total{method=\"").Append(labels[0]).Append("\",route=\"")
                .Append(escaped).Append("\"} ").AppendLine(route.Value.Failures.ToString(CultureInfo.InvariantCulture));
        }
        var snapshot = Current;
        builder.AppendLine("# TYPE yask_scan_queue_depth gauge")
            .Append("yask_scan_queue_depth ").AppendLine((snapshot?.QueuedScanJobs ?? 0).ToString(CultureInfo.InvariantCulture))
            .AppendLine("# TYPE yask_scan_queue_active gauge")
            .Append("yask_scan_queue_active ").AppendLine((snapshot?.ActiveScanJobs ?? 0).ToString(CultureInfo.InvariantCulture))
            .AppendLine("# TYPE yask_scan_queue_oldest_age_seconds gauge")
            .Append("yask_scan_queue_oldest_age_seconds ").AppendLine((snapshot?.OldestQueuedScanSeconds ?? 0).ToString("R", CultureInfo.InvariantCulture))
            .AppendLine("# TYPE yask_scan_ingestion_lag_seconds gauge")
            .Append("yask_scan_ingestion_lag_seconds ").AppendLine((snapshot?.IngestionLagSeconds ?? 0).ToString("R", CultureInfo.InvariantCulture))
            .AppendLine("# TYPE yask_scan_last_ingested_age_seconds gauge")
            .Append("yask_scan_last_ingested_age_seconds ").AppendLine((snapshot?.LastScanCompletionAgeSeconds ?? 0).ToString("R", CultureInfo.InvariantCulture))
            .AppendLine("# TYPE yask_scan_success_rate_24h gauge");
        var completed = (snapshot?.SuccessfulScans24Hours ?? 0) + (snapshot?.PartialScans24Hours ?? 0) + (snapshot?.FailedScans24Hours ?? 0);
        var rate = completed == 0 ? 0d : (double)snapshot!.SuccessfulScans24Hours / completed;
        builder.Append("yask_scan_jobs_completed_24h ").AppendLine(completed.ToString(CultureInfo.InvariantCulture))
            .Append("yask_scan_success_rate_24h ").AppendLine(rate.ToString("R", CultureInfo.InvariantCulture))
            .AppendLine("# TYPE yask_connector_delivery_failures_24h gauge")
            .Append("yask_connector_delivery_failures_24h ").AppendLine((snapshot?.ConnectorFailures24Hours ?? 0).ToString(CultureInfo.InvariantCulture))
            .AppendLine("# TYPE yask_connector_dead_letters gauge")
            .Append("yask_connector_dead_letters ").AppendLine((snapshot?.DeadLetterDeliveries ?? 0).ToString(CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    public void Dispose() => _meter.Dispose();
}

public sealed class ApiRequestMetricsMiddleware(RequestDelegate next, OperationsMetrics metrics)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api")) { await next(context); return; }
        var started = Stopwatch.GetTimestamp();
        try { await next(context); }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var endpoint = context.GetEndpoint() as RouteEndpoint;
            var route = endpoint?.RoutePattern.RawText ?? "/api/{unmatched}";
            metrics.RecordApiRequest(context.Request.Method, route, context.Response.StatusCode, elapsed);
        }
    }
}
