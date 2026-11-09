using System.Security.Cryptography;
using System.Text;
using CloudEntitle.AccessPath.Models;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;

namespace CloudEntitle.AccessPath.Services;

public sealed class AttackPathEngine
{
    private const int MaximumConfiguredDepth = 16;
    private const int MaximumConfiguredPaths = 10000;
    private const int MaximumConfiguredNodes = 100000;
    private const int MaximumConfiguredEdges = 500000;
    private const int MaximumConfiguredTraversalOperations = 1000000;

    public Task<AttackGraph> BuildAsync(AttackGraphBuildRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        var evaluatedAt = request.EvaluatedAtUtc ?? DateTime.UtcNow;
        var diagnostics = new List<string>();
        var nodes = new Dictionary<string, AttackGraphNode>(StringComparer.Ordinal);
        var edges = new Dictionary<string, AttackGraphEdge>(StringComparer.Ordinal);
        var principals = request.Principals.ToDictionary(item => item.Id);
        var roles = request.Roles.ToDictionary(item => item.Id);
        var policies = request.Policies.ToDictionary(item => item.Id);
        var permissions = request.Permissions.ToDictionary(item => item.Id);
        var resources = request.Resources.ToDictionary(item => item.Id);
        var signalsByResource = (request.ResourceSecuritySignals ?? Array.Empty<ResourceSecuritySignal>())
            .GroupBy(item => item.ResourceId).ToDictionary(group => group.Key, group => group.ToArray());
        var complete = true;

        foreach (var principal in request.Principals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddNode(CreateNode(AttackGraphNodeType.Identity, principal.Id, principal.CloudProvider switch
            {
                CloudProviderType.Aws => CloudProvider.Aws,
                CloudProviderType.Azure => CloudProvider.Azure,
                CloudProviderType.Gcp => CloudProvider.Gcp,
                _ => CloudProvider.Kubernetes
            }, principal.AccountId, principal.Name, principal.PrincipalId,
                principal.IsExternal && principal.IsActive, false,
                $"IdentityType={principal.IdentityType}; External={principal.IsExternal}; Active={principal.IsActive}; Privileged={principal.IsPrivileged}",
                principal.IsPrivileged));
        }

        foreach (var resource in request.Resources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resourceSignals = signalsByResource.GetValueOrDefault(resource.Id) ?? Array.Empty<ResourceSecuritySignal>();
            var sensitive = resourceSignals.Any(IsSensitiveSignal)
                || HasSensitiveClassification(resource);
            AddNode(CreateNode(AttackGraphNodeType.Resource, resource.Id, ToProvider(resource.AccountId, request),
                resource.AccountId, resource.Name, resource.ResourceId ?? resource.ResourceUri,
                false, sensitive, SignalSummary(resourceSignals)));
        }

        foreach (var effective in request.EffectiveAccess)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!principals.TryGetValue(effective.PrincipalId, out var principal)
                || !permissions.TryGetValue(effective.PermissionId, out var permission))
            {
                diagnostics.Add($"Effective access {effective.Id:D} references an identity or permission outside the graph inventory.");
                complete = false;
                continue;
            }
            if (principal.AccountId != effective.CloudAccountId
                || ToProvider(principal.CloudProvider) != effective.Provider
                || permission.AccountId != effective.CloudAccountId)
            {
                diagnostics.Add($"Effective access {effective.Id:D} has provider/account attribution inconsistent with its identity or permission.");
                complete = false;
                continue;
            }

            var provider = effective.Provider;
            var identityKey = NodeKey(AttackGraphNodeType.Identity, provider, effective.CloudAccountId, principal.Id);
            string previousKey = identityKey;
            if (effective.RoleId is Guid roleId)
            {
                if (!roles.TryGetValue(roleId, out var role))
                {
                    diagnostics.Add($"Effective access {effective.Id:D} references a missing role.");
                    complete = false;
                }
                else
                {
                    var roleKey = AddNode(CreateNode(AttackGraphNodeType.Role, role.Id, provider,
                        effective.CloudAccountId, role.Name, role.RoleId, false, false,
                        $"Role scope={role.Scope ?? "not reported"}", role.IsPrivileged));
                    AddEdgeForAccess(effective, previousKey, roleKey, "HasRole", "Role assignment evidence");
                    previousKey = roleKey;
                }
            }
            if (effective.PolicyId is Guid policyId)
            {
                if (!policies.TryGetValue(policyId, out var policy))
                {
                    diagnostics.Add($"Effective access {effective.Id:D} references a missing policy.");
                    complete = false;
                }
                else
                {
                    var policyKey = AddNode(CreateNode(AttackGraphNodeType.Policy, policy.Id, provider,
                        effective.CloudAccountId, policy.Name, policy.ExternalId ?? policy.Name, false, false,
                        $"PolicyType={policy.PolicyType ?? "not reported"}"));
                    AddEdgeForAccess(effective, previousKey, policyKey, "AssignedPolicy", "Policy assignment evidence");
                    previousKey = policyKey;
                }
            }

            var permissionKey = AddNode(CreateNode(AttackGraphNodeType.Permission, permission.Id, provider,
                effective.CloudAccountId, permission.Name, permission.Source,
                false, false, $"Action={permission.Action ?? "unknown"}", effective.IsPrivileged));
            AddEdgeForAccess(effective, previousKey, permissionKey, "GrantsPermission",
                effective.AccessSource ?? permission.Source ?? "Effective-access engine");

            if (effective.ResourceId is not Guid resourceId || !resources.TryGetValue(resourceId, out var resourceRecord))
            {
                diagnostics.Add($"Effective access {effective.Id:D} has no resource present in the scanned inventory.");
                complete = false;
                continue;
            }
            if (resourceRecord.AccountId != effective.CloudAccountId)
            {
                diagnostics.Add($"Effective access {effective.Id:D} references a resource in a different cloud account.");
                complete = false;
                continue;
            }
            var resourceNodeKey = NodeKey(AttackGraphNodeType.Resource, provider, effective.CloudAccountId, resourceRecord.Id);
            AddEdgeForAccess(effective, permissionKey, resourceNodeKey, "AppliesToResource",
                effective.AccessSource ?? "Effective-access engine");
        }

        foreach (var trust in request.TrustRelationships)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceKey = NodeKey(AttackGraphNodeType.Identity, trust.SourceProvider, trust.SourceCloudAccountId, trust.SourcePrincipalId);
            var targetKey = NodeKey(AttackGraphNodeType.Identity, trust.TargetProvider, trust.TargetCloudAccountId, trust.TargetPrincipalId);
            if (!nodes.ContainsKey(sourceKey) && trust.CreateSourceNode && !string.IsNullOrWhiteSpace(trust.ExternalSourceName))
            {
                AddNode(CreateNode(AttackGraphNodeType.Identity, trust.SourcePrincipalId, trust.SourceProvider,
                    trust.SourceCloudAccountId, trust.ExternalSourceName, trust.EvidenceReference,
                    trust.SourceIsInitialAccessPoint, false, trust.ExternalSourceDetails));
            }
            if (!nodes.ContainsKey(sourceKey) || !nodes.ContainsKey(targetKey))
            {
                diagnostics.Add($"Trust relationship '{trust.RelationshipType}' references an identity absent from the scanned inventory.");
                complete = false;
                continue;
            }
            if (string.IsNullOrWhiteSpace(trust.EvidenceSource) || string.IsNullOrWhiteSpace(trust.EvidenceReference))
            {
                diagnostics.Add($"Trust relationship '{trust.RelationshipType}' is missing provider evidence and was excluded.");
                complete = false;
                continue;
            }

            var sourcePermission = trust.RequiresSourcePermission
                ? FindRequiredSourcePermission(trust, request.EffectiveAccess, permissions)
                : null;
            var feasibility = ResolveTrustFeasibility(trust, sourcePermission);
            AddEdge(new AttackGraphEdge(
                EdgeKey(sourceKey, targetKey, trust.EvidenceReference, trust.RelationshipType),
                sourceKey, targetKey, trust.RelationshipType, trust.SourceProvider,
                trust.SourceCloudAccountId, feasibility, trust.EvidenceSource, trust.EvidenceReference,
                sourcePermission?.Id, ConditionEvidence: trust.ConditionEvidence,
                Details: $"Status={trust.Status}; Condition={trust.ConditionState}; ObservedAt={trust.ObservedAtUtc.ToUniversalTime():O}"));
        }

        var paths = EnumeratePaths(request, nodes, edges, principals, diagnostics, ref complete, cancellationToken);
        var findings = paths.Select(path => CreateFinding(path, edges)).ToArray();
        return Task.FromResult(new AttackGraph(
            StableId($"attack-graph|{evaluatedAt:O}|{nodes.Count}|{edges.Count}"),
            evaluatedAt, nodes.Values.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray(),
            edges.Values.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray(), paths, findings,
            diagnostics.Distinct(StringComparer.Ordinal).ToArray(), complete));

        string AddNode(AttackGraphNode node)
        {
            if (nodes.TryGetValue(node.Key, out var existing)) return existing.Key;
            if (nodes.Count >= request.MaxNodes)
            {
                complete = false;
                throw new AttackGraphLimitException("Attack graph node limit reached; no complete graph was produced.");
            }
            nodes.Add(node.Key, node);
            return node.Key;
        }

        void AddEdgeForAccess(DomainEffectiveAccess effective, string from, string to, string relationship, string source)
        {
            var permission = permissions[effective.PermissionId];
            var feasibility = ResolveAccessFeasibility(effective);
            var evidence = effective.AccessSource ?? permission.Source ?? $"effective-access:{effective.Id:D}";
            if (string.IsNullOrWhiteSpace(effective.Evidence) && string.IsNullOrWhiteSpace(effective.AccessSource)
                && string.IsNullOrWhiteSpace(permission.Source))
                feasibility = AttackEdgeFeasibility.Unknown;
            AddEdge(new AttackGraphEdge(EdgeKey(from, to, effective.Id.ToString("D"), relationship),
                from, to, relationship, effective.Provider, effective.CloudAccountId, feasibility,
                source, evidence, effective.Id, effective.Scope,
                effective.IsConditional ? "Conditional grant; request context was not re-evaluated by the attack graph." : null,
                $"Decision={effective.Decision}; Evaluation={effective.EvaluationStatus}; Direct={effective.IsDirect}; Inherited={effective.IsInherited}; Conditional={effective.IsConditional}; ProviderEvidence={(string.IsNullOrWhiteSpace(effective.Evidence) ? "not retained" : effective.Evidence)}"));
        }

        void AddEdge(AttackGraphEdge edge)
        {
            if (edges.ContainsKey(edge.Key)) return;
            if (edges.Count >= request.MaxEdges)
            {
                complete = false;
                throw new AttackGraphLimitException("Attack graph edge limit reached; no complete graph was produced.");
            }
            edges.Add(edge.Key, edge);
        }
    }

    private static IReadOnlyList<AttackPath> EnumeratePaths(
        AttackGraphBuildRequest request,
        IReadOnlyDictionary<string, AttackGraphNode> nodes,
        IReadOnlyDictionary<string, AttackGraphEdge> edges,
        IReadOnlyDictionary<Guid, CloudPrincipal> principals,
        ICollection<string> diagnostics,
        ref bool complete,
        CancellationToken cancellationToken)
    {
        var isComplete = complete;
        var adjacency = edges.Values.Where(edge => edge.Feasibility is AttackEdgeFeasibility.Verified or AttackEdgeFeasibility.Potential or AttackEdgeFeasibility.Unknown)
            .GroupBy(edge => edge.FromNodeKey)
            .ToDictionary(group => group.Key, group => group.OrderBy(edge => edge.Key, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var entryPoints = nodes.Values.Where(node => node.Type == AttackGraphNodeType.Identity && node.IsInitialAccessPoint)
            .OrderBy(node => node.Key, StringComparer.Ordinal).ToArray();
        var paths = new List<AttackPath>();
        var traversalOperations = 0;
        foreach (var entry in entryPoints)
        {
            var nodePath = new List<string> { entry.Key };
            var edgePath = new List<string>();
            var visited = new HashSet<string>(StringComparer.Ordinal) { entry.Key };
            Walk(entry);
            if (paths.Count >= request.MaxPaths) break;

            void Walk(AttackGraphNode current)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++traversalOperations > request.MaxTraversalOperations)
                {
                    if (isComplete) diagnostics.Add("Attack graph traversal operation limit reached; result is incomplete.");
                    isComplete = false;
                    return;
                }
                if (edgePath.Count >= request.MaxPathDepth)
                {
                    if (adjacency.ContainsKey(current.Key))
                    {
                        if (isComplete) diagnostics.Add("Attack path depth limit reached; result is incomplete.");
                        isComplete = false;
                    }
                    return;
                }
                if (!adjacency.TryGetValue(current.Key, out var outgoing)) return;
                foreach (var edge in outgoing)
                {
                    if (paths.Count >= request.MaxPaths)
                    {
                        if (isComplete) diagnostics.Add("Attack path count limit reached; result is incomplete.");
                        isComplete = false;
                        return;
                    }
                    if (visited.Contains(edge.ToNodeKey))
                    {
                        if (isComplete) diagnostics.Add($"Cycle detected at graph edge {edge.Key}; cyclic expansion was suppressed.");
                        continue;
                    }
                    if (!nodes.TryGetValue(edge.ToNodeKey, out var next)) continue;
                    traversalOperations++;
                    if (traversalOperations > request.MaxTraversalOperations)
                    {
                        isComplete = false;
                        diagnostics.Add("Attack graph traversal operation limit reached; result is incomplete.");
                        return;
                    }
                    visited.Add(next.Key);
                    nodePath.Add(next.Key);
                    edgePath.Add(edge.Key);
                    if (next.Type == AttackGraphNodeType.Resource)
                    {
                        var reachability = ResolveReachability(edgePath, edges);
                        var category = ResolveCategory(entry, nodePath, edgePath, nodes, edges, principals);
                        var score = CalculateRiskScore(reachability, category, next.IsSensitive);
                        paths.Add(new(StableId($"attack-path|{string.Join('|', nodePath)}|{string.Join('|', edgePath)}"),
                            entry.Key, next.Key, category, reachability, score, nodePath.ToArray(), edgePath.ToArray(),
                            BuildEvidenceSummary(nodePath, edgePath, nodes, edges)));
                    }
                    else if (next.Type == AttackGraphNodeType.Identity)
                    {
                        Walk(next);
                    }
                    else if (next.Type is AttackGraphNodeType.Role or AttackGraphNodeType.Policy or AttackGraphNodeType.Permission)
                    {
                        Walk(next);
                    }
                    edgePath.RemoveAt(edgePath.Count - 1);
                    nodePath.RemoveAt(nodePath.Count - 1);
                    visited.Remove(next.Key);
                    if (traversalOperations > request.MaxTraversalOperations) return;
                }
            }
        }
        if (entryPoints.Length == 0) diagnostics.Add("No externally identified initial access point was present in the inventory.");
        complete = isComplete;
        return paths.OrderByDescending(item => item.RiskScore).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
    }

    private static AttackPathCategory ResolveCategory(AttackGraphNode entry, IReadOnlyList<string> nodePath,
        IReadOnlyList<string> edgePath, IReadOnlyDictionary<string, AttackGraphNode> nodes,
        IReadOnlyDictionary<string, AttackGraphEdge> edges, IReadOnlyDictionary<Guid, CloudPrincipal> principals)
    {
        var target = nodes[nodePath[^1]];
        if (target.IsSensitive) return AttackPathCategory.SensitiveResourceExposure;
        var pathEdges = edgePath.Select(key => edges[key]).ToArray();
        if (pathEdges.Any(edge => edge.Provider != entry.Provider))
            return AttackPathCategory.CrossCloudFederation;
        if (pathEdges.Any(edge => edge.CloudAccountId != entry.CloudAccountId))
            return AttackPathCategory.CrossAccount;
        if (pathEdges.Any(edge => edge.ToNodeKey != edge.FromNodeKey
                && nodes[edge.ToNodeKey].Type == AttackGraphNodeType.Identity))
            return AttackPathCategory.LateralMovement;
        var entryIsPrivileged = entry.IsPrivileged
            || entry.EntityId is Guid entryId && principals.TryGetValue(entryId, out var entryPrincipal)
                && entryPrincipal.IsPrivileged;
        var gainsPrivilege = nodePath.Skip(1).Select(key => nodes[key]).Any(node => node.IsPrivileged
            || node.Type == AttackGraphNodeType.Identity && node.EntityId is Guid id
                && principals.TryGetValue(id, out var principal) && principal.IsPrivileged);
        return gainsPrivilege && !entryIsPrivileged
            ? AttackPathCategory.PrivilegeEscalation
            : AttackPathCategory.InitialAccess;
    }

    private static AttackPathReachability ResolveReachability(IReadOnlyList<string> edgeKeys,
        IReadOnlyDictionary<string, AttackGraphEdge> edges)
    {
        var feasibility = edgeKeys.Select(key => edges[key].Feasibility).ToArray();
        if (feasibility.All(item => item == AttackEdgeFeasibility.Verified))
            return AttackPathReachability.VerifiedReachable;
        if (feasibility.Contains(AttackEdgeFeasibility.Potential))
            return AttackPathReachability.Potential;
        return AttackPathReachability.Theoretical;
    }

    private static AttackPathFinding CreateFinding(AttackPath path, IReadOnlyDictionary<string, AttackGraphEdge> edges)
    {
        var severity = path.RiskScore >= 85 ? RiskSeverity.Critical
            : path.RiskScore >= 70 ? RiskSeverity.High
            : path.RiskScore >= 45 ? RiskSeverity.Medium : RiskSeverity.Low;
        var title = path.Reachability == AttackPathReachability.VerifiedReachable
            ? $"Verified {path.Category} path"
            : $"Potential {path.Category} path";
        var refs = path.EdgeKeys.Select(key => edges[key].EvidenceReference).Distinct(StringComparer.Ordinal).ToArray();
        var reachabilityText = path.Reachability switch
        {
            AttackPathReachability.VerifiedReachable => "Every edge is supported by fully evaluated allowed access or confirmed trust evidence.",
            AttackPathReachability.Potential => "At least one access edge is conditional or partially evaluated; reachability is not confirmed.",
            _ => "At least one trust or authorization relationship is unknown or unverified; this is a theoretical path, not confirmed reachability."
        };
        return new(StableId($"attack-finding|{path.Id}"), path.Id, path.Category, path.Reachability,
            severity, path.RiskScore, title, reachabilityText, path.EvidenceSummary, refs);
    }

    private static int CalculateRiskScore(AttackPathReachability reachability, AttackPathCategory category, bool sensitive)
    {
        var score = category switch
        {
            AttackPathCategory.SensitiveResourceExposure => 75,
            AttackPathCategory.PrivilegeEscalation => 70,
            AttackPathCategory.CrossCloudFederation => 65,
            AttackPathCategory.CrossAccount => 60,
            AttackPathCategory.LateralMovement => 55,
            _ => 40
        };
        if (sensitive) score += 15;
        if (reachability != AttackPathReachability.VerifiedReachable) score -= 20;
        return Math.Clamp(score, 0, 100);
    }

    private static string BuildEvidenceSummary(IReadOnlyList<string> nodeKeys, IReadOnlyList<string> edgeKeys,
        IReadOnlyDictionary<string, AttackGraphNode> nodes, IReadOnlyDictionary<string, AttackGraphEdge> edges)
    {
        var nodeSummary = string.Join(" -> ", nodeKeys.Select(key => nodes[key].Name));
        var edgeEvidence = edgeKeys.Select(key => edges[key])
            .Select(edge => $"{edge.RelationshipType}: {edge.EvidenceSource} [{edge.Feasibility}; {edge.EvidenceReference}]");
        return $"{nodeSummary}. {string.Join(" | ", edgeEvidence)}";
    }

    private static AttackEdgeFeasibility ResolveAccessFeasibility(DomainEffectiveAccess access)
    {
        if (access.Decision == AccessDecision.Deny || access.AccessType == AccessType.Deny)
            return AttackEdgeFeasibility.Denied;
        if (access.Decision == AccessDecision.Conditional)
            return AttackEdgeFeasibility.Potential;
        if (access.Decision != AccessDecision.Allow) return AttackEdgeFeasibility.Unknown;
        if (access.EvaluationStatus != EvaluationStatus.FullyEvaluated || access.IsConditional)
            return AttackEdgeFeasibility.Potential;
        return AttackEdgeFeasibility.Verified;
    }

    private static AttackEdgeFeasibility ResolveTrustFeasibility(AttackTrustRelationship trust, DomainEffectiveAccess? sourcePermission)
    {
        if (trust.Status.Equals("Rejected", StringComparison.OrdinalIgnoreCase)
            || trust.Status.Equals("Denied", StringComparison.OrdinalIgnoreCase))
            return AttackEdgeFeasibility.Denied;
        if (trust.ConditionState.Equals("NotSatisfied", StringComparison.OrdinalIgnoreCase))
            return AttackEdgeFeasibility.Denied;
        if (trust.RequiresSourcePermission && sourcePermission is null)
            return AttackEdgeFeasibility.Unknown;
        if (!trust.Status.Equals("Confirmed", StringComparison.OrdinalIgnoreCase)
            || !trust.ConditionState.Equals("Satisfied", StringComparison.OrdinalIgnoreCase))
            return AttackEdgeFeasibility.Unknown;
        if (sourcePermission is not null && ResolveAccessFeasibility(sourcePermission) != AttackEdgeFeasibility.Verified)
            return ResolveAccessFeasibility(sourcePermission) == AttackEdgeFeasibility.Denied
                ? AttackEdgeFeasibility.Denied : AttackEdgeFeasibility.Potential;
        return AttackEdgeFeasibility.Verified;
    }

    private static DomainEffectiveAccess? FindRequiredSourcePermission(AttackTrustRelationship trust,
        IReadOnlyList<DomainEffectiveAccess> access,
        IReadOnlyDictionary<Guid, CloudPermission> permissions)
    {
        if (string.IsNullOrWhiteSpace(trust.RequiredSourcePermissionPattern)) return null;
        var matches = access.Where(item => item.PrincipalId == trust.SourcePrincipalId
            && item.CloudAccountId == trust.SourceCloudAccountId
            && item.Provider == trust.SourceProvider
            && permissions.TryGetValue(item.PermissionId, out var permission)
            && ActionMatches(trust.SourceProvider, trust.RequiredSourcePermissionPattern, permission.Action ?? permission.Name)).ToArray();
        return matches.FirstOrDefault(item => ResolveAccessFeasibility(item) == AttackEdgeFeasibility.Verified)
            ?? matches.FirstOrDefault(item => ResolveAccessFeasibility(item) == AttackEdgeFeasibility.Potential)
            ?? matches.FirstOrDefault();
    }

    private static bool ActionMatches(CloudProvider provider, string pattern, string action)
    {
        var comparison = provider == CloudProvider.Gcp ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var p = 0;
        var a = 0;
        var star = -1;
        var retry = -1;
        while (a < action.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || string.Equals(pattern[p].ToString(), action[a].ToString(), comparison)))
            {
                p++;
                a++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                retry = a;
            }
            else if (star >= 0)
            {
                p = star + 1;
                a = ++retry;
            }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    private static AttackGraphNode CreateNode(AttackGraphNodeType type, Guid entityId, CloudProvider provider,
        Guid accountId, string name, string? evidence, bool initial, bool sensitive, string? details,
        bool privileged = false)
        => new(NodeKey(type, provider, accountId, entityId), type, entityId, provider, accountId,
            name, evidence, initial, sensitive, details, privileged);

    private static string NodeKey(AttackGraphNodeType type, CloudProvider provider, Guid accountId, Guid id)
        => $"{provider}:{accountId:D}:{type}:{id:D}";

    private static string EdgeKey(string from, string to, string evidence, string relationship)
        => StableId($"edge|{from}|{to}|{relationship}|{evidence}");

    private static string StableId(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32].ToLowerInvariant();

    private static CloudProvider ToProvider(Guid accountId, AttackGraphBuildRequest request)
    {
        var access = request.EffectiveAccess.FirstOrDefault(item => item.CloudAccountId == accountId);
        if (access is not null) return access.Provider;
        var principal = request.Principals.FirstOrDefault(item => item.AccountId == accountId);
        return principal is null ? CloudProvider.Kubernetes : principal.CloudProvider switch
        {
            CloudProviderType.Aws => CloudProvider.Aws,
            CloudProviderType.Azure => CloudProvider.Azure,
            CloudProviderType.Gcp => CloudProvider.Gcp,
            _ => CloudProvider.Kubernetes
        };
    }

    private static CloudProvider ToProvider(CloudProviderType provider) => provider switch
    {
        CloudProviderType.Aws => CloudProvider.Aws,
        CloudProviderType.Azure => CloudProvider.Azure,
        CloudProviderType.Gcp => CloudProvider.Gcp,
        _ => CloudProvider.Kubernetes
    };

    private static bool IsSensitiveSignal(ResourceSecuritySignal signal)
        => signal.IsConfirmed && signal.Type is (ResourceSecuritySignalType.SensitiveData
            or ResourceSecuritySignalType.CriticalAsset or ResourceSecuritySignalType.PublicExposure);

    private static bool HasSensitiveClassification(CloudResource resource)
        => resource.Tags.Any(tag => (tag.Key.Contains("sensitivity", StringComparison.OrdinalIgnoreCase)
                || tag.Key.Contains("classification", StringComparison.OrdinalIgnoreCase)
                || tag.Key.Contains("critical", StringComparison.OrdinalIgnoreCase))
            && (tag.Value.Contains("sensitive", StringComparison.OrdinalIgnoreCase)
                || tag.Value.Contains("confidential", StringComparison.OrdinalIgnoreCase)
                || tag.Value.Contains("restricted", StringComparison.OrdinalIgnoreCase)
                || tag.Value.Contains("critical", StringComparison.OrdinalIgnoreCase)
                || tag.Value.Contains("tier0", StringComparison.OrdinalIgnoreCase)));

    private static string? SignalSummary(IEnumerable<ResourceSecuritySignal> signals)
        => string.Join("; ", signals.Select(item => $"{item.Type}={item.Name} from {item.Source} ({(item.IsConfirmed ? "confirmed" : "unconfirmed")})"));

    private static void ValidateRequest(AttackGraphBuildRequest request)
    {
        if (request.MaxPathDepth is < 1 or > MaximumConfiguredDepth
            || request.MaxPaths is < 1 or > MaximumConfiguredPaths
            || request.MaxNodes is < 1 or > MaximumConfiguredNodes
            || request.MaxEdges is < 1 or > MaximumConfiguredEdges
            || request.MaxTraversalOperations is < 1 or > MaximumConfiguredTraversalOperations)
            throw new ArgumentOutOfRangeException(nameof(request), "Attack graph limits are outside the supported safe range.");
    }

    private sealed class AttackGraphLimitException(string message) : InvalidOperationException(message);
}
