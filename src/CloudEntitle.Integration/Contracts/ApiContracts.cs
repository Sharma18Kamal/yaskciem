using CloudEntitle.Domain;

namespace CloudEntitle.Integration.Contracts;

public static class ApiContractVersion
{
    public const string Current = "v1";
}

public sealed record ApiHealthResponse(string Status, string Version, DateTime UtcTime);
public sealed record ApiReadinessResponse(string Status, bool DatabaseReady, bool ConfigurationReady, string Version);
public sealed record ProviderCapabilityDto(CloudProvider Provider, bool ReadOnly, bool ConnectorAvailable, string AuthenticationMode);
public sealed record ApiIdentityDto(Guid TenantId, Guid UserObjectId);
public sealed record PostureSummaryDto(int CloudAccounts, int Identities, int Resources, int Entitlements, int RiskFindings, int CriticalFindings, DateTime? LastScanAt);
public sealed record ApiError(string Code, string Message, string CorrelationId);
public sealed record ApiEnvelope<T>(T Data, string CorrelationId, string Version);
