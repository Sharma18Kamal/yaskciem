using CloudEntitle.AccessPath.Models;
using CloudEntitle.AccessPath.Services;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.EffectiveAccess.Models;
using CloudEntitle.RiskEngine.Abstractions;
using CloudEntitle.RiskEngine.Models;

namespace CloudEntitle.Desktop.Services;

public sealed class WhatIfScenarioAnalyzer(
    IRiskEngine riskEngine,
    IToxicCombinationWorkspaceService? toxicCombinationWorkspace = null) : IWhatIfScenarioAnalyzer
{
    private readonly AccessPathEngine _accessPathEngine = new();
    private readonly AttackPathEngine _attackPathEngine = new();

    public async Task<WhatIfSecurityImpact> AnalyzeAsync(
        WhatIfSecuritySnapshot before,
        WhatIfSecuritySnapshot after,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var evaluatedAt = after.Request.EvaluationTime ?? DateTime.UtcNow;
        var beforeAnalysis = await AnalyzeSnapshotAsync(before, evaluatedAt, cancellationToken);
        var afterAnalysis = await AnalyzeSnapshotAsync(after, evaluatedAt, cancellationToken);
        var addedFindings = afterAnalysis.Risk.Findings.Where(item => !beforeAnalysis.Risk.Findings
                .Any(previous => previous.Fingerprint == item.Fingerprint))
            .Select(ToFinding).ToArray();
        var removedFindings = beforeAnalysis.Risk.Findings.Where(item => !afterAnalysis.Risk.Findings
                .Any(current => current.Fingerprint == item.Fingerprint))
            .Select(ToFinding).ToArray();
        var addedPaths = afterAnalysis.Graph.Paths.Where(item => !beforeAnalysis.Graph.Paths
                .Any(previous => PathKey(previous) == PathKey(item)))
            .Select(ToAttackPath).ToArray();
        var removedPaths = beforeAnalysis.Graph.Paths.Where(item => !afterAnalysis.Graph.Paths
                .Any(current => PathKey(current) == PathKey(item)))
            .Select(ToAttackPath).ToArray();
        var diagnostics = beforeAnalysis.Diagnostics.Concat(afterAnalysis.Diagnostics)
            .Distinct(StringComparer.Ordinal).ToArray();
        var complete = beforeAnalysis.IsComplete && afterAnalysis.IsComplete && diagnostics.Length == 0;
        return new WhatIfSecurityImpact(
            complete,
            complete ? MaxScore(beforeAnalysis.Risk.Findings) : null,
            complete ? MaxScore(afterAnalysis.Risk.Findings) : null,
            beforeAnalysis.Risk.Findings.Count,
            afterAnalysis.Risk.Findings.Count,
            beforeAnalysis.Risk.Findings.Count(IsToxicFinding),
            afterAnalysis.Risk.Findings.Count(IsToxicFinding),
            beforeAnalysis.Graph.Paths.Count,
            afterAnalysis.Graph.Paths.Count,
            addedFindings,
            removedFindings,
            addedPaths,
            removedPaths,
            diagnostics);
    }

    private async Task<SnapshotAnalysis> AnalyzeSnapshotAsync(
        WhatIfSecuritySnapshot snapshot,
        DateTime evaluatedAt,
        CancellationToken cancellationToken)
    {
        var request = snapshot.Request;
        var normalized = request.Normalized;
        var accessPaths = await _accessPathEngine.BuildAsync(new AccessPathRequest(
            snapshot.Evaluation.EffectiveAccess, normalized, request.Resources, request.GroupMemberships,
            EvaluatedAt: evaluatedAt), cancellationToken);
        var trustRelationships = AttackTrustRelationshipMapper.FromProviderEvidence(normalized.Principals);
        var graph = await _attackPathEngine.BuildAsync(new AttackGraphBuildRequest(
            normalized.Principals, normalized.Roles, normalized.Policies, normalized.Permissions,
            request.Resources, snapshot.Evaluation.EffectiveAccess, trustRelationships,
            EvaluatedAtUtc: evaluatedAt), cancellationToken);
        var accounts = normalized.Principals.Select(item => item.AccountId).Distinct().Select(accountId =>
        {
            var provider = normalized.Principals.First(item => item.AccountId == accountId).CloudProvider;
            return new CloudAccount
            {
                Id = accountId,
                CloudProvider = provider,
                Name = $"{provider} account"
            };
        }).ToArray();
        var toxicRules = toxicCombinationWorkspace is null
            ? null
            : await toxicCombinationWorkspace.GetEnabledRulesAsync(cancellationToken);
        var toxicTrust = ToxicTrustRelationshipMapper.FromFederationEvidence(normalized.Principals);
        var riskRequest = new RiskEvaluationRequest(
            accounts, normalized.Principals, request.Resources, normalized.Roles, normalized.Policies,
            normalized.Permissions, normalized.Entitlements, snapshot.Evaluation.EffectiveAccess,
            accessPaths.Paths, Array.Empty<ActivityRecord>(), EvaluatedAt: evaluatedAt,
            ToxicCombinationRules: toxicRules, ToxicTrustRelationships: toxicTrust, AttackGraph: graph);
        var risk = await riskEngine.EvaluateAsync(riskRequest, cancellationToken);
        var diagnostics = snapshot.Evaluation.Diagnostics.Select(item => item.Message)
            .Concat(accessPaths.Diagnostics.Select(item => item.Message))
            .Concat(graph.Diagnostics)
            .Concat(risk.Diagnostics.Select(item => item.Message))
            .ToArray();
        var complete = graph.IsComplete && accessPaths.Statistics.UnknownPaths == 0
            && accessPaths.Statistics.UnsupportedPaths == 0
            && snapshot.Evaluation.UnknownCount == 0 && snapshot.Evaluation.UnsupportedCount == 0
            && risk.Statistics.RulesFailed == 0;
        return new SnapshotAnalysis(risk, graph, diagnostics, complete);
    }

    private static bool IsToxicFinding(RiskFindingModel finding)
        => finding.Evidence.Details.ContainsKey("RuleEntityId");

    private static int MaxScore(IReadOnlyList<RiskFindingModel> findings)
        => findings.Count == 0 ? 0 : findings.Max(item => item.PriorityScore);

    private static WhatIfFinding ToFinding(RiskFindingModel item)
        => new(item.Fingerprint, item.Title, item.PriorityScore, item.Severity.ToString(),
            item.EvaluationState.ToString(), item.Evidence.Summary);

    private static string PathKey(AttackPath path)
        => $"{path.EntryPointNodeKey}|{path.TargetNodeKey}|{path.Category}|{path.Reachability}";

    private static WhatIfAttackPath ToAttackPath(AttackPath path)
        => new(PathKey(path), path.Category.ToString(), path.Reachability.ToString(),
            path.RiskScore, path.EvidenceSummary);

    private sealed record SnapshotAnalysis(
        RiskEvaluationResult Risk,
        AttackGraph Graph,
        IReadOnlyList<string> Diagnostics,
        bool IsComplete);
}
