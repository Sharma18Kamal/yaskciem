using CloudEntitle.Domain;

namespace CloudEntitle.Analytics.Models;

public enum PermissionUsageState
{
    Used,
    Unused,
    Unknown
}

public sealed record PermissionUsageWindow(
    int Days,
    PermissionUsageState State,
    DateTime? LastUsedAtUtc,
    int SuccessfulActionCount);

public sealed record PermissionActionFrequency(string Action, int SuccessfulActionCount);

public sealed record PermissionUsageRecord(
    Guid EffectiveAccessId,
    Guid CloudAccountId,
    CloudProvider Provider,
    Guid PrincipalId,
    Guid PermissionId,
    string Permission,
    DateTime? LastUsedAtUtc,
    IReadOnlyList<PermissionUsageWindow> Windows,
    IReadOnlyList<PermissionActionFrequency> ActionFrequencies);

public sealed record PermissionUsageWindowSummary(
    int Days,
    int Used,
    int Unused,
    int Unknown);

public sealed record PermissionUsageReport(
    DateTime EvaluatedAtUtc,
    IReadOnlyList<PermissionUsageRecord> Permissions,
    IReadOnlyList<PermissionUsageWindowSummary> Summary);
