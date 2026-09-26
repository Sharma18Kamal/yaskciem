using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;

namespace CloudEntitle.Analytics.Models;

public sealed record AnalyticsMetric(string Label, int Value);
public sealed record AnalyticsSnapshot(
    int CloudAccounts,
    int Identities,
    int Resources,
    int Entitlements,
    int PrivilegedIdentities,
    int RiskFindings,
    int CriticalFindings,
    int HighFindings,
    int DormantIdentities,
    int ExternalIdentities,
    int AllowDecisions,
    int DenyDecisions,
    int ConditionalDecisions,
    DateTime? LastScan,
    IReadOnlyList<AnalyticsMetric> AccountsByProvider,
    IReadOnlyList<AnalyticsMetric> FindingsByProvider,
    IReadOnlyList<AnalyticsMetric> FindingsBySeverity,
    IReadOnlyList<AnalyticsMetric> IdentitiesByType,
    IReadOnlyList<AnalyticsMetric> AccessByDecision);
