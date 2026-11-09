using CloudEntitle.AccessPath.Models;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Domain.ValueObjects;
using CloudEntitle.RiskEngine.Abstractions;
using CloudEntitle.RiskEngine.Models;

namespace CloudEntitle.RiskEngine.Rules;

public sealed class CrossCloudAttackPathRule : IRiskRule
{
    public string RuleId => "CIEM-023";
    public string Name => "Evidence-Backed Cross-Cloud Attack Path";
    public RiskSeverity DefaultSeverity => RiskSeverity.High;
    public RiskCategory Category => RiskCategory.ExternalAccess;

    public Task<IReadOnlyCollection<RiskFindingModel>> EvaluateAsync(
        RiskRuleContext context, CancellationToken cancellationToken = default)
    {
        var graph = context.Request.AttackGraph;
        if (graph is null)
            return Task.FromResult<IReadOnlyCollection<RiskFindingModel>>(Array.Empty<RiskFindingModel>());

        var nodes = graph.Nodes.ToDictionary(item => item.Key, StringComparer.Ordinal);
        var findings = new List<RiskFindingModel>();
        foreach (var pathFinding in graph.Findings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = graph.Paths.FirstOrDefault(item => item.Id == pathFinding.AttackPathId);
            if (path is null
                || !nodes.TryGetValue(path.EntryPointNodeKey, out var entry)
                || !nodes.TryGetValue(path.TargetNodeKey, out var target)
                || entry.EntityId is not Guid principalId)
                continue;

            var fingerprint = $"{RuleId}|{path.Id}|{pathFinding.Reachability}";
            var now = context.EvaluatedAt;
            findings.Add(new RiskFindingModel
            {
                Id = GuidUtility.Create(GuidUtility.Namespace, fingerprint),
                RuleId = GuidUtility.Create(GuidUtility.Namespace, RuleId),
                CloudProvider = target.Provider,
                CloudAccountId = target.CloudAccountId,
                PrincipalId = principalId,
                ResourceId = target.EntityId,
                Severity = pathFinding.Severity,
                Status = FindingStatus.Open,
                Title = pathFinding.Title,
                Description = $"{pathFinding.Description} Attack path category: {pathFinding.Category}; reachability: {pathFinding.Reachability}.",
                Evidence = new RiskEvidence(pathFinding.EvidenceSummary, new Dictionary<string, string>
                {
                    ["AttackPathId"] = path.Id,
                    ["EntryPoint"] = entry.Name,
                    ["Target"] = target.Name,
                    ["Category"] = pathFinding.Category.ToString(),
                    ["Reachability"] = pathFinding.Reachability.ToString(),
                    ["EvidenceReferences"] = string.Join(";", pathFinding.EvidenceReferences)
                }),
                Recommendation = "Validate provider evidence and initial-access assumptions; an authorization path does not prove exploitation.",
                CreatedAt = now,
                UpdatedAt = now,
                DetectedAt = now,
                LastEvaluatedAt = now,
                EvaluationState = RiskEvaluationState.Potential,
                Category = Category,
                PriorityScore = pathFinding.RiskScore,
                RuleVersion = "1.0",
                Fingerprint = fingerprint
            });
        }
        return Task.FromResult<IReadOnlyCollection<RiskFindingModel>>(findings);
    }
}
