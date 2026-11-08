namespace CloudEntitle.Integration.Contracts;

public static class OperationalEventTypes
{
    public static readonly IReadOnlyList<string> All = Array.AsReadOnly(new[]
    {
        "finding.created", "finding.resolved", "finding.updated", "access.added", "access.removed",
        "access.changed", "review.overdue", "scan.failed", "jit.requested", "jit.approve", "jit.reject", "jit.activate",
        "jit.issuing", "jit.submitted", "jit.active", "jit.expirypending", "jit.outcomeunknown", "jit.failed", "jit.revoked",
        "behavior.anomaly", "documents.observedchange", "trust-credentials.observedchange", "privilege-escalation.potential",
        "privilege-escalation.confirmeddecision",
        "cloud-change.observed", "cloud-change.coverage-gap", "cloud-change.poll-failed"
    });

    public static string ForDrift(string kind, string change) => kind == "effective-access"
        ? change == "Added" ? "access.added" : change == "Removed" ? "access.removed" : "access.changed"
        : kind == "review" ? "review.overdue" : kind == "scan" ? "scan.failed"
        : change == "Added" ? "finding.created" : change == "Removed" ? "finding.resolved" : "finding.updated";
}

/// <summary>Explicitly requests a Jira or ServiceNow ticket for an existing organization event; replay is deduplicated.</summary>
public sealed record CreateOperationalTicketRequest(string Channel);

/// <summary>Passive connector health from durable delivery attempts, not an active connectivity probe.</summary>
public sealed record ConnectorHealthDto(string Channel, string ConnectorKind, string ConfigurationStatus, string Health,
    long Pending, long Sending, long Retrying, long DeadLettered, long Delivered,
    DateTimeOffset? LastAttemptAtUtc, DateTimeOffset? LastSuccessAtUtc, double? AverageLatencyMilliseconds);
