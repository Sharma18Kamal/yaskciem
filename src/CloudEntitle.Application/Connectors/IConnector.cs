namespace CloudEntitle.Application.Connectors;

public enum CoverageStatus
{
    Collected,
    NoData,
    Partial,
    Denied,
    Unavailable,
    Unsupported,
    Skipped
}

/// <summary>Identifies one tenant-owned cloud scope (AWS account, Azure subscription, GCP project).</summary>
public sealed record ConnectorScope(
    Guid OrganizationId,
    string Provider,
    string ExternalId,
    string? RoleArn = null,
    string? ExternalSecret = null);

public sealed record CoverageEntry(string Area, CoverageStatus Status, string? Message = null);

public sealed record ConnectorIdentity(string Kind, string ExternalId, string Name, IReadOnlyDictionary<string, string> Attributes);

public sealed record ConnectorResult(
    ConnectorScope Scope,
    DateTimeOffset CollectedAt,
    IReadOnlyList<ConnectorIdentity> Identities,
    IReadOnlyList<CoverageEntry> Coverage);

/// <summary>CLI-free, read-only discovery against a federated, short-lived credential.</summary>
public interface IConnector
{
    string Provider { get; }

    Task<ConnectorResult> DiscoverAsync(ConnectorScope scope, CancellationToken cancellationToken = default);
}
