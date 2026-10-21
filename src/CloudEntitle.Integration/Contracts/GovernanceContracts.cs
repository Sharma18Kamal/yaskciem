using CloudEntitle.Domain;
using CloudEntitle.IdentityGovernance.Models;

namespace CloudEntitle.Integration.Contracts;

/// <summary>Changes current workflow responsibility without altering captured review evidence.</summary>
public sealed record ReassignReviewItemRequest(
    long ExpectedAssignmentVersion, string? Owner, string Reviewer, string? Team,
    string? ExternalAccountId, DateTime DueAtUtc, string Reason);

/// <summary>Active organization members eligible to own or review shared work.</summary>
public sealed record GovernanceAssigneesDto(IReadOnlyList<string> Owners, IReadOnlyList<string> Reviewers);

/// <summary>Current review work and temporary exception deadlines for one item.</summary>
public sealed record GovernanceWorkItemDto(
    Guid CampaignId, string Campaign, Guid ItemId, CloudProvider Provider, string Account, string Team,
    string? Owner, string? Reviewer, ReviewDecision Decision, DateTime DueAtUtc, bool Completed, bool Overdue,
    DateTime? ExceptionExpiresAtUtc, bool ExceptionExpired, int? ExceptionAgeDays,
    int Reminders, int ExceptionReminders, DateTime? EscalatedAtUtc);

/// <summary>Review completion and current exception age distribution grouped by account and team.</summary>
public sealed record GovernanceGroupDto(
    CloudProvider Provider, string Account, string Team, int Total, int Completed, int Unresolved, int Overdue,
    decimal CompletionPercent, int ActiveExceptions, int ExpiredExceptions, int ExceptionsUnder30Days,
    int Exceptions30To89Days, int Exceptions90DaysOrOlder, int? OldestExceptionDays);

/// <summary>One persisted governance action, with campaign/item and authenticated audit metadata.</summary>
public sealed record GovernanceHistoryDto(Guid CampaignId, string Campaign, Guid? ItemId, AccessReviewAuditEntry Event);

/// <summary>Organization-wide deadline report evaluated at the indicated UTC time; no cloud access is changed.</summary>
public sealed record GovernanceReportDto(
    DateTime AsOfUtc, IReadOnlyList<GovernanceWorkItemDto> Items, IReadOnlyList<GovernanceGroupDto> Groups,
    IReadOnlyList<GovernanceHistoryDto> History);
