using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.IdentityGovernance.Models;
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
        var campaignId = Guid.NewGuid();
        var principalIds = identities.Select(identity => identity.PrincipalId).ToHashSet();
        var items = effectiveAccess.Where(access => principalIds.Contains(access.PrincipalId) && access.Decision is AccessDecision.Allow or AccessDecision.Conditional).Select(access => new AccessReviewItem(Guid.NewGuid(), campaignId, access.PrincipalId, access.Id, access.AccessPathId, access.Provider, $"Decision={access.Decision}; Permission={access.PermissionId}; Scope={access.Scope ?? "unknown"}", ReviewDecision.Pending, null, null, null)).ToArray();
        return new AccessReviewCampaign(campaignId, name, CampaignStatus.Open, DateTime.UtcNow, DateTime.UtcNow.Add(duration), principalIds.ToArray(), items);
    }

    public AccessReviewCampaign RecordDecision(AccessReviewCampaign campaign, Guid itemId, ReviewDecision decision, string reviewer, string comment)
    {
        var items = campaign.Items.Select(item => item.Id == itemId ? item with { Decision = decision, Reviewer = reviewer, ReviewComment = comment, DecidedAt = DateTime.UtcNow } : item).ToArray();
        var status = items.All(item => item.Decision != ReviewDecision.Pending) ? CampaignStatus.Completed : campaign.Status;
        return campaign with { Items = items, Status = status };
    }
}
