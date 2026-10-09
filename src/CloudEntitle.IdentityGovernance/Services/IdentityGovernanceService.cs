using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.IdentityGovernance.Models;
using System.Security.Cryptography;
using System.Text;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;
using DomainAccessPath = CloudEntitle.Domain.Entities.AccessPath;

namespace CloudEntitle.IdentityGovernance.Services;

public sealed class IdentityGovernanceService
{
    public IReadOnlyList<IdentityGovernanceRecord> Assess(
        IReadOnlyList<CloudPrincipal> principals,
        IReadOnlyList<DomainEffectiveAccess> effectiveAccess,
        IReadOnlyList<ActivityRecord> activity,
        GovernanceOptions? options = null,
        DateTime? asOf = null)
    {
        options ??= new GovernanceOptions();
        var now = asOf ?? DateTime.UtcNow;
        return principals.Select(principal =>
        {
            var principalActivity = activity.Where(item => item.PrincipalId == principal.Id).OrderByDescending(item => item.Timestamp).FirstOrDefault();
            var lastActivity = principal.LastActivityAt ?? principalActivity?.Timestamp;
            int? days = lastActivity.HasValue ? Math.Max(0, (int)(now - lastActivity.Value).TotalDays) : null;
            var isService = principal.IdentityType is IdentityType.ServiceAccount or IdentityType.ServicePrincipal or IdentityType.ManagedIdentity or IdentityType.Application;
            var lifecycle = principal.Status == IdentityStatus.Disabled ? GovernanceLifecycleState.Disabled
                : principal.IsExternal && options.IncludeExternalIdentities ? GovernanceLifecycleState.External
                : isService && options.IncludeServiceIdentities ? GovernanceLifecycleState.ServiceIdentity
                : !principal.IsActive ? GovernanceLifecycleState.Inactive
                : days.HasValue && days.Value >= options.DormancyDays ? GovernanceLifecycleState.Dormant
                : lastActivity.HasValue ? GovernanceLifecycleState.Active
                : GovernanceLifecycleState.Unknown;
            var access = effectiveAccess.Where(item => item.PrincipalId == principal.Id && item.Decision is AccessDecision.Allow or AccessDecision.Conditional).ToArray();
            var privileged = access.Count(item => item.IsPrivileged);
            var evidence = new List<string> { $"IdentityType={principal.IdentityType}", $"Active={principal.IsActive}", $"PrivilegedAccess={privileged}" };
            if (lastActivity.HasValue) evidence.Add($"LastActivity={lastActivity.Value:O}");
            else evidence.Add("LastActivity=Unknown");
            var diagnostics = lastActivity.HasValue ? Array.Empty<string>() : new[] { "GOV-001 Activity data unavailable." };
            return new IdentityGovernanceRecord(principal.Id, principal.AccountId, principal.DisplayName, principal.IdentityType, lifecycle, principal.IsPrivileged || privileged > 0, principal.IsExternal, lastActivity, days, access.Length, privileged, evidence, diagnostics);
        }).ToArray();
    }

    public AccessReviewCampaign CreateCampaign(string name, IReadOnlyList<IdentityGovernanceRecord> identities, IReadOnlyList<DomainEffectiveAccess> effectiveAccess, IReadOnlyList<DomainAccessPath> paths, TimeSpan duration)
    {
        var principalIds = identities.Select(identity => identity.PrincipalId).ToHashSet();
        var candidates = effectiveAccess
            .Where(access => principalIds.Contains(access.PrincipalId) && access.Decision is AccessDecision.Allow or AccessDecision.Conditional or AccessDecision.Unknown)
            .Select(access => new AccessReviewCandidate(
                access.PrincipalId,
                access.Id,
                access.AccessPathId,
                access.Provider,
                $"Decision={access.Decision}; Permission={access.PermissionId}; Scope={access.Scope ?? "unknown"}",
                $"Decision={access.Decision}; Evaluation={access.EvaluationStatus}; Assignment={access.AssignmentType}; Source={access.AccessSource ?? "not reported"}; Evidence={access.Evidence ?? "not recorded"}"))
            .ToArray();
        return CreateCampaign(name, candidates, duration);
    }

    public AccessReviewCampaign CreateCampaign(string name, IReadOnlyList<AccessReviewCandidate> candidates, TimeSpan duration,
        string? assignedReviewer = null, DateTime? asOf = null, string? templateId = null, int reminderLeadDays = 3,
        int reminderIntervalDays = 1, string? createdBy = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A campaign name is required.", nameof(name));
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration), "Campaign duration must be positive.");
        if (reminderLeadDays is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(reminderLeadDays));
        if (reminderIntervalDays is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(reminderIntervalDays));
        var now = asOf ?? DateTime.UtcNow;
        var creator = string.IsNullOrWhiteSpace(createdBy) ? assignedReviewer?.Trim() ?? "System" : createdBy.Trim();
        var campaignId = Guid.NewGuid();
        var dueAt = now.Add(duration);
        var items = candidates.Select(candidate => new AccessReviewItem(
            Guid.NewGuid(), campaignId, candidate.PrincipalId, candidate.EffectiveAccessId, candidate.AccessPathId,
            candidate.Provider, candidate.AccessSummary, ReviewDecision.Pending, null, null, null)
        {
            AssignedReviewer = string.IsNullOrWhiteSpace(assignedReviewer) ? null : assignedReviewer.Trim(),
            OwnerSnapshot = string.IsNullOrWhiteSpace(candidate.SuggestedReviewer) ? null : candidate.SuggestedReviewer.Trim(),
            DueAt = dueAt,
            EvidenceSnapshot = candidate.EvidenceSnapshot,
            AuditTrail = new[] { new AccessReviewAuditEntry(now, creator, "Created", "Evidence snapshot captured when the review campaign was created.") }
        }).ToArray();
        var audit = new[] { new AccessReviewAuditEntry(now, creator, "CampaignCreated", $"Created {items.Length} review item(s); due {dueAt:O}.") };
        return new AccessReviewCampaign(campaignId, name.Trim(), CampaignStatus.Open, now, dueAt, candidates.Select(candidate => candidate.PrincipalId).Distinct().ToArray(), items)
        {
            CreatedBy = creator == "System" ? null : creator,
            TemplateId = templateId,
            ReminderLeadDays = reminderLeadDays,
            ReminderIntervalDays = reminderIntervalDays,
            AuditTrail = audit
        };
    }

    public AccessReviewCampaign AssignReviewer(AccessReviewCampaign campaign, Guid itemId, string reviewer, DateTime dueAt, string actor, DateTime? asOf = null)
    {
        if (string.IsNullOrWhiteSpace(reviewer)) throw new ArgumentException("A reviewer is required.", nameof(reviewer));
        var now = asOf ?? DateTime.UtcNow;
        if (dueAt <= now) throw new ArgumentOutOfRangeException(nameof(dueAt), "The review deadline must be in the future.");
        var item = campaign.Items.FirstOrDefault(candidate => candidate.Id == itemId)
            ?? throw new ArgumentException("The review item does not belong to this campaign.", nameof(itemId));
        var updated = item with
        {
            AssignedReviewer = reviewer.Trim(),
            DueAt = dueAt,
            AuditTrail = item.AuditTrail.Append(new AccessReviewAuditEntry(now, actor, "ReviewerAssigned", $"Assigned to {reviewer.Trim()}; due {dueAt:O}.")).ToArray()
        };
        return UpdateCampaign(campaign, updated, new AccessReviewAuditEntry(now, actor, "ReviewerAssigned", $"{reviewer.Trim()} assigned to review item {itemId}."), now);
    }

    public AccessReviewCampaign RecordDecision(AccessReviewCampaign campaign, Guid itemId, ReviewDecision decision, string reviewer,
        string comment, DateTime? exceptionExpiresAt = null, DateTime? asOf = null, string? actor = null)
    {
        if (decision == ReviewDecision.Pending) throw new ArgumentException("A decision cannot be Pending.", nameof(decision));
        if (string.IsNullOrWhiteSpace(reviewer)) throw new ArgumentException("A reviewer is required.", nameof(reviewer));
        var now = asOf ?? DateTime.UtcNow;
        if (decision == ReviewDecision.ExceptionGranted && (!exceptionExpiresAt.HasValue || exceptionExpiresAt.Value <= now))
            throw new ArgumentOutOfRangeException(nameof(exceptionExpiresAt), "An exception must have a future expiration date.");
        var item = campaign.Items.FirstOrDefault(candidate => candidate.Id == itemId)
            ?? throw new ArgumentException("The review item does not belong to this campaign.", nameof(itemId));
        var actingUser = string.IsNullOrWhiteSpace(actor) ? reviewer.Trim() : actor.Trim();
        var commentText = comment ?? string.Empty;
        var attestation = new AccessReviewAttestation(reviewer.Trim(), actingUser, decision, commentText, now,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.EvidenceSnapshot))).ToLowerInvariant(), item.EvidenceSnapshot);
        var updated = item with
        {
            Decision = decision,
            Reviewer = reviewer.Trim(),
            ReviewComment = commentText,
            DecidedAt = now,
            DueAt = decision == ReviewDecision.ExceptionGranted ? exceptionExpiresAt : item.DueAt,
            ExceptionExpiresAt = decision == ReviewDecision.ExceptionGranted ? exceptionExpiresAt : null,
            ExceptionExpiredAt = null,
            EscalatedAt = null,
            EscalationReason = null,
            Attestations = item.Attestations.Append(attestation).ToArray(),
            AuditTrail = item.AuditTrail.Append(new AccessReviewAuditEntry(now, actingUser, "AttestationRecorded", $"{decision} attested by {reviewer.Trim()}; evidence SHA-256 {attestation.EvidenceSha256}."))
                .Append(new AccessReviewAuditEntry(now, actingUser, decision.ToString(), decision == ReviewDecision.ExceptionGranted ? $"{commentText}; exception expires {exceptionExpiresAt:O}." : commentText)).ToArray()
        };
        return UpdateCampaign(campaign, updated, new AccessReviewAuditEntry(now, actingUser, "AttestationRecorded", $"{decision} recorded for review item {itemId} with evidence hash {attestation.EvidenceSha256}."), now);
    }

    public AccessReviewCampaign ReevaluateCampaign(AccessReviewCampaign campaign, DateTime? asOf = null)
    {
        var now = asOf ?? DateTime.UtcNow;
        var items = campaign.Items.Select(item =>
        {
            var updated = item;
            if (item.Decision == ReviewDecision.ExceptionGranted && item.ExceptionExpiresAt <= now && !item.ExceptionExpiredAt.HasValue)
            {
                updated = updated with
                {
                    ExceptionExpiredAt = now,
                    AuditTrail = updated.AuditTrail.Append(new AccessReviewAuditEntry(now, "System", "ExceptionExpired", $"Exception expired at {item.ExceptionExpiresAt:O}; access requires a new review decision.")).ToArray()
                };
            }
            var unresolved = updated.Decision == ReviewDecision.Pending || updated.ExceptionExpiredAt.HasValue;
            var dueAt = updated.DueAt ?? campaign.DueAt;
            var reminderAt = dueAt.AddDays(-campaign.ReminderLeadDays);
            if (unresolved && now >= reminderAt && now < dueAt
                && (!updated.LastReminderAt.HasValue || now >= updated.LastReminderAt.Value.AddDays(campaign.ReminderIntervalDays)))
            {
                updated = updated with
                {
                    ReminderCount = updated.ReminderCount + 1,
                    LastReminderAt = now,
                    AuditTrail = updated.AuditTrail.Append(new AccessReviewAuditEntry(now, "System", "ReviewerReminderDue",
                        $"Reminder {updated.ReminderCount + 1} is due for {updated.AssignedReviewer ?? "unassigned reviewer"}; item deadline is {dueAt:O}." )).ToArray()
                };
            }
            if (unresolved && dueAt <= now && !updated.EscalatedAt.HasValue)
            {
                updated = updated with
                {
                    EscalatedAt = now,
                    EscalationReason = updated.ExceptionExpiredAt.HasValue ? "Expired exception has not been re-reviewed." : "Review deadline passed without a decision.",
                    AuditTrail = updated.AuditTrail.Append(new AccessReviewAuditEntry(now, "System", "Escalated", updated.ExceptionExpiredAt.HasValue ? "Expired exception has not been re-reviewed." : "Review deadline passed without a decision.")).ToArray()
                };
            }
            return updated;
        }).ToArray();
        var complete = items.All(item => item.Decision != ReviewDecision.Pending && !item.ExceptionExpiredAt.HasValue);
        var escalated = items.Any(item => item.EscalatedAt.HasValue);
        var status = complete ? CampaignStatus.Completed : escalated ? CampaignStatus.Escalated : CampaignStatus.Open;
        var audit = campaign.AuditTrail;
        var reminderEntries = items.SelectMany(item => item.AuditTrail).Where(entry => entry.Action == "ReviewerReminderDue")
            .Where(entry => !audit.Any(existing => existing.Timestamp == entry.Timestamp && existing.Details == entry.Details));
        audit = audit.Concat(reminderEntries).ToArray();
        if (status != campaign.Status) audit = audit.Append(new AccessReviewAuditEntry(now, "System", "CampaignStatusChanged", $"{campaign.Status} -> {status}.")).ToArray();
        return campaign with { Items = items, Status = status, AuditTrail = audit };
    }

    private AccessReviewCampaign UpdateCampaign(AccessReviewCampaign campaign, AccessReviewItem updatedItem, AccessReviewAuditEntry auditEntry, DateTime now)
    {
        var items = campaign.Items.Select(item => item.Id == updatedItem.Id ? updatedItem : item).ToArray();
        return ReevaluateCampaign(campaign with { Items = items, AuditTrail = campaign.AuditTrail.Append(auditEntry).ToArray() }, now);
    }
}
