using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.RiskEngine.Models;
using CloudEntitle.RiskEngine.Rules;
using CloudEntitle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using RiskFinding = CloudEntitle.Domain.Entities.RiskFindingModel;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;
using System.Text.Json;
using CloudEntitle.Compliance.Services;

namespace CloudEntitle.Desktop.Services;

public sealed record ToxicCombinationFindingRow(
    Guid Id, string RuleKey, string RuleName, string Provider, string Identity, string Resource,
    string Severity, int RiskScore, string Confidence, string EvaluationState, string ResourceRelation,
    string Evidence, string Recommendation, DateTime LastEvaluatedAtUtc);

public sealed record ToxicCombinationEvidenceRow(
    Guid FindingId, string RuleKey, string Provider, string Identity, string Action, string Scope,
    string AssignmentType, bool IsDirect, bool IsInherited, bool IsConditional, string EvaluationStatus,
    string SourceEvidence, string Resource);

public sealed record ToxicCombinationRecommendationRow(
    string RuleKey, string Title, string Description, string Status, bool Executable, string Evidence);

public sealed record ToxicCombinationTrustPathRow(
    Guid FindingId, int Sequence, string SourceProvider, string SourceIdentity, Guid SourceAccountId,
    string TargetProvider, string TargetIdentity, Guid TargetAccountId, string RelationshipType,
    string Status, string EvidenceSource, string EvidenceReference, bool RequiresSourcePermission,
    string? RequiredSourcePermissionPattern, string ConditionEvidence, DateTime ObservedAtUtc);

public sealed record ToxicCombinationRuleAuditRow(
    DateTime Timestamp, string Action, string Actor, string RuleKey, string Details);

public interface IToxicCombinationWorkspaceService
{
    Task<IReadOnlyList<ToxicCombinationRule>> GetRulesAsync(CancellationToken token);
    Task<IReadOnlyList<ToxicCombinationRule>> GetEnabledRulesAsync(CancellationToken token);
    Task SaveRulesAsync(IReadOnlyCollection<ToxicCombinationRule> rules, CancellationToken token,
        Guid? actorUserId = null, Guid? organizationId = null, string? actorEmail = null);
    Task AddRuleAsync(ToxicCombinationRule rule, CancellationToken token,
        Guid? actorUserId = null, Guid? organizationId = null, string? actorEmail = null);
    Task<IReadOnlyList<ToxicCombinationFindingRow>> PreviewRuleAsync(ToxicCombinationRule rule, CancellationToken token);
    Task<IReadOnlyList<ToxicCombinationRuleAuditRow>> GetRuleAuditHistoryAsync(CancellationToken token);
    Task<IReadOnlyList<ToxicCombinationFindingRow>> GetFindingsAsync(CancellationToken token);
    Task<IReadOnlyList<ToxicCombinationEvidenceRow>> GetEvidenceAsync(CancellationToken token);
    Task<IReadOnlyList<ToxicCombinationRecommendationRow>> GetRecommendationsAsync(CancellationToken token);
    Task<IReadOnlyList<ToxicCombinationTrustPathRow>> GetTrustPathEvidenceAsync(CancellationToken token);
    Task RecordEvaluationAsync(RiskEvaluationRequest request, IReadOnlyList<RiskFinding> findings, CancellationToken token);
    Task LoadDemoAsync(CancellationToken token);
}

public sealed class ToxicCombinationWorkspaceService(
    IDbContextFactory<CloudEntitleDbContext> contextFactory,
    CloudEntitle.RiskEngine.Services.RiskEngine riskEngine) : IToxicCombinationWorkspaceService
{
    private const int MaximumRules = 500;
    private const int MaximumFindings = 5000;

    public async Task<IReadOnlyList<ToxicCombinationRule>> GetRulesAsync(CancellationToken token)
    {
        await using var db = await contextFactory.CreateDbContextAsync(token);
        await EnsureDefaultRulesAsync(db, token);
        return await db.ToxicCombinationRules.AsNoTracking().OrderBy(item => item.Provider).ThenBy(item => item.Name)
            .ToArrayAsync(token);
    }

    public async Task<IReadOnlyList<ToxicCombinationRule>> GetEnabledRulesAsync(CancellationToken token)
        => (await GetRulesAsync(token)).Where(item => item.Enabled).ToArray();

    public async Task SaveRulesAsync(IReadOnlyCollection<ToxicCombinationRule> rules, CancellationToken token,
        Guid? actorUserId = null, Guid? organizationId = null, string? actorEmail = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.Count is 0 or > MaximumRules) throw new ArgumentOutOfRangeException(nameof(rules), "Supply between 1 and 500 rules.");
        foreach (var rule in rules) ValidateRule(rule);

        await using var db = await contextFactory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await EnsureDefaultRulesAsync(db, token);
        var existing = await db.ToxicCombinationRules.ToDictionaryAsync(item => item.Key, StringComparer.Ordinal, token);
        if (rules.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() != rules.Count)
            throw new ArgumentException("Rule keys must be unique.", nameof(rules));
        if (existing.Count + rules.Count(item => !existing.ContainsKey(item.Key)) > MaximumRules)
            throw new InvalidOperationException($"The maximum of {MaximumRules} toxic-combination rules has been reached.");
        foreach (var update in rules)
        {
            if (!existing.TryGetValue(update.Key, out var saved))
            {
                update.Id = StableGuid(update.Key);
                update.CreatedAtUtc = DateTime.UtcNow;
                db.ToxicCombinationRules.Add(update);
                AddRuleAudit(db, update, "Created", actorUserId, organizationId, actorEmail, null, SerializeRule(update));
                continue;
            }

            var before = SerializeRule(saved);
            saved.Name = update.Name.Trim();
            saved.Description = update.Description.Trim();
            saved.Provider = update.Provider;
            saved.FirstPermissionPattern = update.FirstPermissionPattern.Trim();
            saved.SecondPermissionPattern = update.SecondPermissionPattern.Trim();
            saved.ResourceRequirement = update.ResourceRequirement;
            saved.FirstRequiresPrivilegedAccess = update.FirstRequiresPrivilegedAccess;
            saved.SecondRequiresPrivilegedAccess = update.SecondRequiresPrivilegedAccess;
            saved.RequireSameResource = update.RequireSameResource;
            saved.RequireTrustPath = update.RequireTrustPath;
            saved.Severity = update.Severity;
            saved.RiskScore = update.RiskScore;
            saved.Enabled = update.Enabled;
            saved.Recommendation = update.Recommendation.Trim();
            var afterWithoutVersion = SerializeRule(saved);
            if (!string.Equals(before, afterWithoutVersion, StringComparison.Ordinal))
            {
                saved.Version = NextVersion(saved.Version);
                saved.UpdatedAtUtc = DateTime.UtcNow;
                AddRuleAudit(db, saved, "Updated", actorUserId, organizationId, actorEmail, before, SerializeRule(saved));
            }
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    public async Task AddRuleAsync(ToxicCombinationRule rule, CancellationToken token,
        Guid? actorUserId = null, Guid? organizationId = null, string? actorEmail = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        rule.Key = rule.Key.Trim();
        ValidateRule(rule);
        await using var db = await contextFactory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await EnsureDefaultRulesAsync(db, token);
        if (await db.ToxicCombinationRules.CountAsync(token) >= MaximumRules)
            throw new InvalidOperationException($"The maximum of {MaximumRules} toxic-combination rules has been reached.");
        if (await db.ToxicCombinationRules.AnyAsync(item => item.Key == rule.Key, token))
            throw new ArgumentException($"A toxic-combination rule with key '{rule.Key}' already exists.", nameof(rule));
        rule.Id = StableGuid(rule.Key);
        rule.Key = rule.Key.Trim();
        rule.Name = rule.Name.Trim();
        rule.Description = rule.Description.Trim();
        rule.FirstPermissionPattern = rule.FirstPermissionPattern.Trim();
        rule.SecondPermissionPattern = rule.SecondPermissionPattern.Trim();
        rule.Recommendation = rule.Recommendation.Trim();
        rule.Version = "1.0";
        rule.CreatedAtUtc = DateTime.UtcNow;
        rule.UpdatedAtUtc = rule.CreatedAtUtc;
        db.ToxicCombinationRules.Add(rule);
        AddRuleAudit(db, rule, "Created", actorUserId, organizationId, actorEmail, null, SerializeRule(rule));
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    public async Task<IReadOnlyList<ToxicCombinationFindingRow>> PreviewRuleAsync(ToxicCombinationRule rule, CancellationToken token)
    {
        ValidateRule(rule);
        await using var db = await contextFactory.CreateDbContextAsync(token);
        const int maxRows = 20_000;
        var accounts = await db.CloudAccounts.AsNoTracking().Take(maxRows + 1).ToArrayAsync(token);
        var principals = await db.CloudPrincipals.AsNoTracking().Take(maxRows + 1).ToArrayAsync(token);
        var resources = await db.CloudResources.AsNoTracking().Take(maxRows + 1).ToArrayAsync(token);
        var permissions = await db.CloudPermissions.AsNoTracking().Take(maxRows + 1).ToArrayAsync(token);
        var roles = await db.CloudRoles.AsNoTracking().Take(maxRows + 1).ToArrayAsync(token);
        var policies = await db.CloudPolicies.AsNoTracking().Take(maxRows + 1).ToArrayAsync(token);
        var entitlements = await db.Entitlements.AsNoTracking().Take(maxRows + 1).ToArrayAsync(token);
        var accesses = await db.EffectiveAccesses.AsNoTracking().Take(maxRows + 1).ToArrayAsync(token);
        var paths = await db.AccessPaths.AsNoTracking().Include(item => item.Nodes).Include(item => item.Edges)
            .Take(maxRows + 1).ToArrayAsync(token);
        if (new[] { accounts.Length, principals.Length, resources.Length, permissions.Length, roles.Length,
                policies.Length, entitlements.Length, accesses.Length, paths.Length }.Any(count => count > maxRows))
            throw new InvalidOperationException("Rule preview inventory exceeds the supported local analysis limit.");

        var request = new RiskEvaluationRequest(accounts, principals, resources, roles, policies, permissions,
            Array.Empty<CloudEntitle.Normalization.Models.NormalizedEntitlement>(), accesses, paths, Array.Empty<ActivityRecord>(),
            Options: new(EnabledRules: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CIEM-021" }),
            ToxicCombinationRules: [rule],
            ToxicTrustRelationships: ToxicTrustRelationshipMapper.FromFederationEvidence(principals));
        var result = await riskEngine.EvaluateAsync(request, token);
        var principalNames = principals.ToDictionary(item => item.Id, item => item.Name);
        var resourceNames = resources.ToDictionary(item => item.Id, item => item.Name);
        return result.Findings.Select(item => new ToxicCombinationFindingRow(item.Id, rule.Key, rule.Name,
            item.CloudProvider.ToString(), principalNames.GetValueOrDefault(item.PrincipalId, item.PrincipalId.ToString("D")),
            item.ResourceId is { } resourceId ? resourceNames.GetValueOrDefault(resourceId, resourceId.ToString("D")) : "Resource scope / account",
            item.Severity.ToString(), item.PriorityScore, item.Evidence.Details.GetValueOrDefault("Confidence", "Low"),
            item.EvaluationState.ToString(), item.Evidence.Details.GetValueOrDefault("ResourceRelation", "Unknown"),
            item.Evidence.Summary, item.Evidence.Details.GetValueOrDefault("Recommendation", rule.Recommendation),
            item.LastEvaluatedAt.ToUniversalTime())).ToArray();
    }

    public async Task<IReadOnlyList<ToxicCombinationRuleAuditRow>> GetRuleAuditHistoryAsync(CancellationToken token)
    {
        await using var db = await contextFactory.CreateDbContextAsync(token);
        var events = await db.AuditEvents.AsNoTracking().Where(item => item.EventType.StartsWith("ToxicCombination.Rule"))
            .OrderByDescending(item => item.Timestamp).Take(500).ToArrayAsync(token);
        return events.Select(item => new ToxicCombinationRuleAuditRow(item.Timestamp, item.EventType,
            item.Metadata.GetValueOrDefault("actorEmail", "Unattributed local user"),
            item.Metadata.GetValueOrDefault("ruleKey", string.Empty),
            $"Before: {item.Metadata.GetValueOrDefault("before", "(new rule)")}; " +
            $"After: {item.Metadata.GetValueOrDefault("after", item.Description)}")).ToArray();
    }

    public async Task<IReadOnlyList<ToxicCombinationFindingRow>> GetFindingsAsync(CancellationToken token)
    {
        await using var db = await contextFactory.CreateDbContextAsync(token);
        var findings = await db.ToxicCombinationFindings.AsNoTracking()
            .OrderByDescending(item => item.LastEvaluatedAtUtc).Take(MaximumFindings).ToArrayAsync(token);
        var ruleNames = await db.ToxicCombinationRules.AsNoTracking().ToDictionaryAsync(item => item.Key, item => item.Name, token);
        var principals = await db.CloudPrincipals.AsNoTracking().ToDictionaryAsync(item => item.Id, item => item.Name, token);
        var resources = await db.CloudResources.AsNoTracking().ToDictionaryAsync(item => item.Id, item => item.Name, token);
        return findings.Select(item => new ToxicCombinationFindingRow(item.Id, item.RuleKey,
            ruleNames.GetValueOrDefault(item.RuleKey, item.RuleKey), item.Provider.ToString(),
            principals.GetValueOrDefault(item.PrincipalId, item.PrincipalId.ToString("D")),
            item.ResourceId is { } resourceId ? resources.GetValueOrDefault(resourceId, resourceId.ToString("D")) : "Resource scope / account",
            item.Severity.ToString(), item.RiskScore, item.Confidence, item.EvaluationState.ToString(),
            item.ResourceRelation, item.EvidenceSummary, item.Recommendation, item.LastEvaluatedAtUtc)).ToArray();
    }

    public async Task<IReadOnlyList<ToxicCombinationEvidenceRow>> GetEvidenceAsync(CancellationToken token)
    {
        await using var db = await contextFactory.CreateDbContextAsync(token);
        var evidence = await db.ToxicCombinationEvidence.AsNoTracking().OrderBy(item => item.FindingId).ToArrayAsync(token);
        var findings = await db.ToxicCombinationFindings.AsNoTracking().ToDictionaryAsync(item => item.Id, token);
        var principals = await db.CloudPrincipals.AsNoTracking().ToDictionaryAsync(item => item.Id, item => item.Name, token);
        var resources = await db.CloudResources.AsNoTracking().ToDictionaryAsync(item => item.Id, item => item.Name, token);
        return evidence.Where(item => findings.ContainsKey(item.FindingId)).Select(item =>
        {
            var finding = findings[item.FindingId];
            return new ToxicCombinationEvidenceRow(item.FindingId, finding.RuleKey, finding.Provider.ToString(),
                principals.GetValueOrDefault(finding.PrincipalId, finding.PrincipalId.ToString("D")),
                item.PermissionAction, item.Scope ?? "Unknown scope", item.AssignmentType, item.IsDirect,
                item.IsInherited, item.IsConditional, item.EvaluationStatus, item.SourceEvidence,
                item.ResourceId is { } resourceId ? resources.GetValueOrDefault(resourceId, resourceId.ToString("D")) : "Unresolved");
        }).ToArray();
    }

    public async Task<IReadOnlyList<ToxicCombinationRecommendationRow>> GetRecommendationsAsync(CancellationToken token)
    {
        await using var db = await contextFactory.CreateDbContextAsync(token);
        var recommendations = await db.ToxicCombinationRecommendations.AsNoTracking().ToArrayAsync(token);
        var findings = await db.ToxicCombinationFindings.AsNoTracking().ToDictionaryAsync(item => item.Id, token);
        return recommendations.Where(item => findings.ContainsKey(item.FindingId)).Select(item =>
        {
            var finding = findings[item.FindingId];
            return new ToxicCombinationRecommendationRow(finding.RuleKey, item.Title, item.Description,
                item.Status, item.Executable, item.Evidence);
        }).ToArray();
    }

    public async Task<IReadOnlyList<ToxicCombinationTrustPathRow>> GetTrustPathEvidenceAsync(CancellationToken token)
    {
        await using var db = await contextFactory.CreateDbContextAsync(token);
        var records = await db.ToxicCombinationTrustPathEvidence.AsNoTracking()
            .OrderBy(item => item.FindingId).ThenBy(item => item.Sequence).ToArrayAsync(token);
        var findings = await db.ToxicCombinationFindings.AsNoTracking().ToDictionaryAsync(item => item.Id, token);
        var principals = await db.CloudPrincipals.AsNoTracking().ToDictionaryAsync(item => item.Id, item => item.Name, token);
        return records.Where(item => findings.ContainsKey(item.FindingId)).Select(item =>
            new ToxicCombinationTrustPathRow(item.FindingId, item.Sequence, item.SourceProvider.ToString(),
                principals.GetValueOrDefault(item.SourcePrincipalId, item.SourcePrincipalId.ToString("D")),
                item.SourceCloudAccountId, item.TargetProvider.ToString(),
                principals.GetValueOrDefault(item.TargetPrincipalId, item.TargetPrincipalId.ToString("D")),
                item.TargetCloudAccountId, item.RelationshipType, item.Status, item.EvidenceSource,
                item.EvidenceReference, item.RequiresSourcePermission, item.RequiredSourcePermissionPattern,
                item.ConditionEvidence, item.ObservedAtUtc)).ToArray();
    }

    public async Task RecordEvaluationAsync(RiskEvaluationRequest request, IReadOnlyList<RiskFinding> findings, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(findings);
        var toxicFindings = findings.Where(item => item.Evidence.Details.ContainsKey("RuleKey")).ToArray();
        if (toxicFindings.Length > MaximumFindings)
            throw new InvalidOperationException("Toxic-combination findings exceed the persistence limit; narrow the inventory.");
        if (toxicFindings.Length == 0) return;

        await using var db = await contextFactory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await EnsureDefaultRulesAsync(db, token);
        var rules = await db.ToxicCombinationRules.ToDictionaryAsync(item => item.Key, StringComparer.Ordinal, token);
        foreach (var finding in toxicFindings)
        {
            token.ThrowIfCancellationRequested();
            var details = finding.Evidence.Details;
            var ruleKey = RequireDetail(details, "RuleKey");
            if (!rules.TryGetValue(ruleKey, out var rule))
                throw new InvalidOperationException($"Finding references unconfigured rule '{ruleKey}'.");
            var firstAccess = FindAccess(request, RequireGuid(details, "FirstEffectiveAccessId"));
            var secondAccess = FindAccess(request, RequireGuid(details, "SecondEffectiveAccessId"));
            var firstPermission = FindPermission(request, firstAccess.PermissionId);
            var secondPermission = FindPermission(request, secondAccess.PermissionId);
            var confidence = RequireDetail(details, "Confidence");
            var resourceRelation = RequireDetail(details, "ResourceRelation");
            var existing = await db.ToxicCombinationFindings.SingleOrDefaultAsync(item => item.Fingerprint == finding.Fingerprint, token);
            if (existing is null)
            {
                existing = new ToxicCombinationFinding
                {
                    Id = StableGuid(finding.Fingerprint),
                    RuleId = rule.Id,
                    RuleKey = rule.Key,
                    Fingerprint = finding.Fingerprint,
                    Provider = finding.CloudProvider,
                    CloudAccountId = finding.CloudAccountId,
                    PrincipalId = finding.PrincipalId,
                    ResourceId = finding.ResourceId,
                    FirstDetectedAtUtc = finding.DetectedAt.ToUniversalTime()
                };
                db.ToxicCombinationFindings.Add(existing);
            }

            existing.RuleId = rule.Id;
            existing.RuleKey = rule.Key;
            existing.Severity = finding.Severity;
            existing.RiskScore = finding.PriorityScore;
            existing.EvaluationState = finding.EvaluationState;
            existing.Confidence = confidence;
            existing.ResourceRelation = resourceRelation;
            existing.EvidenceSummary = finding.Evidence.Summary;
            existing.Recommendation = details.GetValueOrDefault("Recommendation", rule.Recommendation);
            existing.LastEvaluatedAtUtc = finding.LastEvaluatedAt.ToUniversalTime();

            db.ToxicCombinationEvidence.RemoveRange(await db.ToxicCombinationEvidence.Where(item => item.FindingId == existing.Id).ToArrayAsync(token));
            db.ToxicCombinationTrustPathEvidence.RemoveRange(await db.ToxicCombinationTrustPathEvidence
                .Where(item => item.FindingId == existing.Id).ToArrayAsync(token));
            db.PermissionCombinations.RemoveRange(await db.PermissionCombinations.Where(item => item.FindingId == existing.Id).ToArrayAsync(token));
            db.ToxicCombinationRecommendations.RemoveRange(await db.ToxicCombinationRecommendations.Where(item => item.FindingId == existing.Id).ToArrayAsync(token));
            await db.SaveChangesAsync(token);
            AddEvidence(firstAccess, firstPermission, "First", details, existing.Id);
            AddEvidence(secondAccess, secondPermission, "Second", details, existing.Id);
            db.PermissionCombinations.Add(new PermissionCombination
            {
                Id = StableGuid($"{existing.Id:D}|combination"),
                FindingId = existing.Id,
                FirstPermissionId = firstPermission.Id,
                SecondPermissionId = secondPermission.Id,
                FirstEffectiveAccessId = firstAccess.Id,
                SecondEffectiveAccessId = secondAccess.Id,
                FirstAction = firstPermission.Action ?? string.Empty,
                SecondAction = secondPermission.Action ?? string.Empty,
                ProviderSemantics = ProviderSemantics(finding.CloudProvider),
                ScopeAssessment = resourceRelation
            });
            if (details.TryGetValue("TrustPathEvidence", out var trustPathJson))
            {
                var trustPath = JsonSerializer.Deserialize<ToxicTrustRelationshipEvidence[]>(trustPathJson)
                    ?? throw new InvalidOperationException("Toxic trust-path finding contains invalid evidence.");
                if (trustPath.Length is 0 or > 4)
                    throw new InvalidOperationException("Toxic trust-path evidence exceeds the supported path depth.");
                for (var index = 0; index < trustPath.Length; index++)
                {
                    var link = trustPath[index];
                    db.ToxicCombinationTrustPathEvidence.Add(new ToxicCombinationTrustPathEvidence
                    {
                        Id = StableGuid($"{existing.Id:D}|trust|{index}|{link.EvidenceReference}"),
                        FindingId = existing.Id,
                        Sequence = index,
                        SourceProvider = link.SourceProvider,
                        SourceCloudAccountId = link.SourceCloudAccountId,
                        SourcePrincipalId = link.SourcePrincipalId,
                        TargetProvider = link.TargetProvider,
                        TargetCloudAccountId = link.TargetCloudAccountId,
                        TargetPrincipalId = link.TargetPrincipalId,
                        RelationshipType = link.RelationshipType,
                        Status = link.Status.ToString(),
                        EvidenceSource = link.EvidenceSource,
                        EvidenceReference = link.EvidenceReference,
                        RequiresSourcePermission = link.RequiresSourcePermission,
                        RequiredSourcePermissionPattern = link.RequiredSourcePermissionPattern,
                        ConditionEvidence = link.ConditionEvidence ?? string.Empty,
                        ObservedAtUtc = link.ObservedAtUtc.ToUniversalTime()
                    });
                }
            }
            db.ToxicCombinationRecommendations.Add(new ToxicCombinationRecommendation
            {
                Id = StableGuid($"{existing.Id:D}|recommendation"),
                FindingId = existing.Id,
                Title = "Review and separate toxic permissions",
                Description = existing.Recommendation,
                Evidence = finding.Evidence.Summary,
                Executable = false,
                Status = "ReviewOnly",
                CreatedAtUtc = finding.LastEvaluatedAt.ToUniversalTime()
            });
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);

        void AddEvidence(DomainEffectiveAccess access, CloudPermission permission, string side,
            IReadOnlyDictionary<string, string> details, Guid findingId)
        {
            var evidence = string.Join("; ", new[]
            {
                $"AccessId={access.Id:D}", $"PermissionId={permission.Id:D}",
                $"Source={access.AccessSource ?? "Unknown"}", $"Evidence={access.Evidence ?? "No provider evidence supplied"}",
                $"Evaluation={access.EvaluationStatus}", $"Conditional={access.IsConditional}",
                $"Condition={details.GetValueOrDefault(side == "First" ? "FirstConditionEvidence" : "SecondConditionEvidence", "Unknown")}"
            });
            db.ToxicCombinationEvidence.Add(new ToxicCombinationEvidence
            {
                Id = StableGuid($"{findingId:D}|{access.Id:D}"),
                FindingId = findingId,
                EffectiveAccessId = access.Id,
                PermissionId = permission.Id,
                ResourceId = access.ResourceId,
                RoleId = access.RoleId,
                PolicyId = access.PolicyId,
                AccessPathId = access.AccessPathId,
                PermissionAction = permission.Action ?? permission.Name,
                Scope = access.Scope ?? permission.Scope ?? permission.Resource,
                AssignmentType = access.AssignmentType.ToString(),
                IsDirect = access.IsDirect,
                IsInherited = access.IsInherited,
                IsConditional = access.IsConditional || access.Decision == AccessDecision.Conditional,
                EvaluationStatus = access.EvaluationStatus.ToString(),
                SourceEvidence = $"{side}: {evidence}; Provider semantics={ProviderSemantics(access.Provider)}; " +
                    $"Pattern={details.GetValueOrDefault(side == "First" ? "FirstAction" : "SecondAction", string.Empty)}"
            });
        }
    }

    public async Task LoadDemoAsync(CancellationToken token)
    {
        var rules = await GetEnabledRulesAsync(token);
        await using var db = await contextFactory.CreateDbContextAsync(token);
        var account = await db.CloudAccounts.AsNoTracking().FirstOrDefaultAsync(item =>
            item.CloudProvider == CloudProviderType.Aws && item.ExternalAccountId != null
            && item.ExternalAccountId.StartsWith("demo-"), token);
        if (account is null) throw new InvalidOperationException("Load Demo Mode inventory before generating toxic-combination examples.");
        var principal = await db.CloudPrincipals.AsNoTracking().FirstAsync(item => item.AccountId == account.Id, token);
        var resource = await db.CloudResources.AsNoTracking().FirstAsync(item => item.AccountId == account.Id, token);
        var readerId = StableGuid("toxic-demo-reader");
        var policyId = StableGuid("toxic-demo-public-policy");
        var reader = new CloudPermission { Id = readerId, AccountId = account.Id, Name = "Synthetic sensitive storage read", Action = "s3:GetObject", Scope = resource.ResourceId, Source = "Synthetic Demo Mode" };
        var policy = new CloudPermission { Id = policyId, AccountId = account.Id, Name = "Synthetic bucket policy modification", Action = "s3:PutBucketPolicy", Scope = resource.ResourceId, Source = "Synthetic Demo Mode" };
        resource.Name = "Synthetic Sensitive Storage";
        resource.Type = "S3 Bucket";
        resource.ResourceId = "arn:aws:s3:::demo-sensitive-storage";
        resource.Tags["classification"] = "sensitive";
        var first = new DomainEffectiveAccess
        {
            Id = StableGuid("toxic-demo-reader-access"), CloudAccountId = account.Id, Provider = CloudProvider.Aws,
            PrincipalId = principal.Id, PermissionId = readerId, ResourceId = resource.Id, Decision = AccessDecision.Allow,
            EvaluationStatus = EvaluationStatus.FullyEvaluated, AccessType = AccessType.Allow, AssignmentType = AssignmentType.Direct,
            IsDirect = true, Evidence = "Synthetic Demo Mode permission assignment."
        };
        var second = new DomainEffectiveAccess
        {
            Id = StableGuid("toxic-demo-policy-access"), CloudAccountId = account.Id, Provider = CloudProvider.Aws,
            PrincipalId = principal.Id, PermissionId = policyId, ResourceId = resource.Id, Decision = AccessDecision.Allow,
            EvaluationStatus = EvaluationStatus.FullyEvaluated, AccessType = AccessType.Allow, AssignmentType = AssignmentType.Inherited,
            IsInherited = true, Evidence = "Synthetic Demo Mode inherited policy assignment."
        };
        var request = new RiskEvaluationRequest([account], [principal], [resource], [], [], [reader, policy], [],
            [first, second], [], [], EvaluatedAt: DateTime.UtcNow,
            Options: new(EnabledRules: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CIEM-021" }),
            ToxicCombinationRules: rules);
        var result = await riskEngine.EvaluateAsync(request, token);
        await RecordEvaluationAsync(request, result.Findings, token);
    }

    private static async Task EnsureDefaultRulesAsync(CloudEntitleDbContext db, CancellationToken token)
    {
        var defaults = ToxicCombinationRuleDefaults.Create();
        var existing = await db.ToxicCombinationRules.Select(item => item.Key).ToArrayAsync(token);
        var existingKeys = existing.ToHashSet(StringComparer.Ordinal);
        var missing = defaults.Where(item => !existingKeys.Contains(item.Key)).ToArray();
        if (missing.Length == 0) return;
        db.ToxicCombinationRules.AddRange(missing);
        await db.SaveChangesAsync(token);
    }

    private static void ValidateRule(ToxicCombinationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (string.IsNullOrWhiteSpace(rule.Key) || rule.Key.Length > 120
            || rule.Key.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_'))
            || string.IsNullOrWhiteSpace(rule.Name)
            || rule.Name.Length > 240 || string.IsNullOrWhiteSpace(rule.Description) || rule.Description.Length > 2000
            || string.IsNullOrWhiteSpace(rule.FirstPermissionPattern) || rule.FirstPermissionPattern.Length > 500
            || string.IsNullOrWhiteSpace(rule.SecondPermissionPattern) || rule.SecondPermissionPattern.Length > 500
            || rule.Recommendation.Length > 2000 || rule.RiskScore is < 0 or > 100
            || !Enum.IsDefined(rule.Severity) || !Enum.IsDefined(rule.ResourceRequirement)
            || rule.Provider.HasValue && !Enum.IsDefined(rule.Provider.Value))
            throw new ArgumentException("Toxic-combination rule values are invalid or exceed supported limits.", nameof(rule));
        if (rule.FirstPermissionPattern.Split('|').Any(string.IsNullOrWhiteSpace)
            || rule.SecondPermissionPattern.Split('|').Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Permission patterns must be non-empty; use | to separate alternatives.", nameof(rule));
    }

    private static string SerializeRule(ToxicCombinationRule rule)
        => JsonSerializer.Serialize(new
        {
            rule.Key, rule.Name, rule.Description, rule.Provider, rule.FirstPermissionPattern,
            rule.SecondPermissionPattern, rule.ResourceRequirement, rule.FirstRequiresPrivilegedAccess,
            rule.SecondRequiresPrivilegedAccess, rule.RequireSameResource, rule.RequireTrustPath,
            rule.Severity, rule.RiskScore, rule.Enabled, rule.Recommendation
        });

    private static string NextVersion(string current)
    {
        var parts = current.Split('.');
        return parts.Length == 2 && int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor)
            ? $"{major}.{minor + 1}" : "1.1";
    }

    private static void AddRuleAudit(CloudEntitleDbContext db, ToxicCombinationRule rule, string action,
        Guid? actorUserId, Guid? organizationId, string? actorEmail, string? before, string after)
    {
        var description = $"Toxic-combination rule {rule.Key} {action.ToLowerInvariant()} (version {rule.Version}).";
        db.AuditEvents.Add(new AuditEventService().Create(
            $"ToxicCombination.Rule{action}", description, actorUserId, organizationId,
            metadata: new Dictionary<string, string>
            {
                ["ruleKey"] = rule.Key,
                ["version"] = rule.Version,
                ["actorEmail"] = string.IsNullOrWhiteSpace(actorEmail) ? "Unattributed local user" : actorEmail.Trim(),
                ["before"] = before ?? string.Empty,
                ["after"] = after
            }));
    }

    private static DomainEffectiveAccess FindAccess(RiskEvaluationRequest request, Guid id)
        => request.EffectiveAccess.SingleOrDefault(item => item.Id == id)
            ?? throw new InvalidOperationException($"Toxic finding references missing effective access '{id:D}'.");

    private static CloudPermission FindPermission(RiskEvaluationRequest request, Guid id)
        => request.Permissions.SingleOrDefault(item => item.Id == id)
            ?? throw new InvalidOperationException($"Toxic finding references missing permission '{id:D}'.");

    private static string RequireDetail(IReadOnlyDictionary<string, string> details, string key)
        => details.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new InvalidOperationException($"Toxic finding omitted required evidence '{key}'.");

    private static Guid RequireGuid(IReadOnlyDictionary<string, string> details, string key)
        => Guid.TryParse(RequireDetail(details, key), out var value) && value != Guid.Empty
            ? value : throw new InvalidOperationException($"Toxic finding has invalid evidence '{key}'.");

    private static string ProviderSemantics(CloudProvider provider) => provider switch
    {
        CloudProvider.Aws => "AWS IAM action names are case-insensitive; a permission match is not proof of exploitability.",
        CloudProvider.Azure => "Azure RBAC action names are case-insensitive; scope and inherited grants require validation.",
        CloudProvider.Gcp => "GCP IAM permission names are case-sensitive; impersonation requires explicit permission evidence.",
        _ => "Unsupported provider semantics."
    };

    private static Guid StableGuid(string value)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }
}
