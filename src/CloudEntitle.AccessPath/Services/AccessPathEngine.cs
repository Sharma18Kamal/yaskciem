using System.Text;
using System.Text.Json;
using CloudEntitle.AccessPath.Abstractions;
using CloudEntitle.AccessPath.Models;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Normalization.Models;
using DomainAccessPath = CloudEntitle.Domain.Entities.AccessPath;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;

namespace CloudEntitle.AccessPath.Services;

public sealed class AccessPathEngine : IAccessPathEngine
{
    public Task<AccessPathResult> BuildAsync(AccessPathRequest request, CancellationToken cancellationToken = default)
    {
        var paths = new List<DomainAccessPath>();
        var diagnostics = new List<AccessPathDiagnostic>();
        var selected = request.EffectiveAccess
            .Where(item => !request.EffectiveAccessId.HasValue || item.Id == request.EffectiveAccessId.Value)
            .Where(item => !request.PrincipalId.HasValue || item.PrincipalId == request.PrincipalId.Value)
            .Where(item => !request.ResourceId.HasValue || item.ResourceId == request.ResourceId.Value)
            .ToArray();

        foreach (var effective in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resource = request.Resources.FirstOrDefault(item => item.Id == effective.ResourceId);
            if (resource == null)
            {
                diagnostics.Add(new("AP-003", "Effective access references a missing resource.", effective.Id));
                continue;
            }

            var eligiblePrincipals = ResolvePrincipalClosure(effective.PrincipalId, request.GroupMemberships);
            var entitlements = request.Normalized.Entitlements.Where(item => eligiblePrincipals.Contains(item.PrincipalId) && item.PermissionId == effective.PermissionId && (item.ResourceId == resource.Id || item.Scope.OriginalScope == effective.Scope || resource.ResourceId?.StartsWith(item.Scope.OriginalScope.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) == true)).ToArray();
            if (entitlements.Length == 0)
            {
                diagnostics.Add(new("AP-004", "No normalized entitlement explains the effective access.", effective.Id));
                continue;
            }

            var generated = new List<DomainAccessPath>();
            foreach (var entitlement in entitlements)
            {
                var groupChains = FindMembershipChains(effective.PrincipalId, entitlement.PrincipalId, request.GroupMemberships, request.MaxPathDepth, diagnostics, effective.Id);
                if (groupChains.Count == 0) groupChains.Add(Array.Empty<Guid>());
                foreach (var groupChain in groupChains)
                {
                    var path = BuildPath(effective, entitlement, resource, groupChain, request, diagnostics);
                    generated.Add(path);
                    if (generated.Count >= request.MaxPathsPerEffectiveAccess) { diagnostics.Add(new("AP-002", "Path expansion limit reached.", effective.Id)); break; }
                }
                if (generated.Count >= request.MaxPathsPerEffectiveAccess) break;
            }

            var distinct = generated.GroupBy(PathSignature).Select(group => group.First()).ToList();
            paths.AddRange(request.Mode == PathMode.Shortest ? distinct.OrderBy(path => path.Nodes.Count).Take(1) : distinct);
        }

        var ordered = paths.OrderBy(path => path.Id).ToArray();
        var stats = new AccessPathStatistics(ordered.Length, ordered.Count(path => path.PathStatus == AccessPathStatus.Complete), ordered.Count(path => path.PathStatus == AccessPathStatus.Partial), ordered.Count(path => path.PathStatus == AccessPathStatus.Unknown), ordered.Count(path => path.PathStatus == AccessPathStatus.Unsupported), ordered.Count(path => path.PathType == AccessPathType.Direct), ordered.Count(path => path.PathType is AccessPathType.GroupBased or AccessPathType.NestedGroup), ordered.Count(path => path.IsInherited()), ordered.Count(path => path.PathType == AccessPathType.Conditional));
        return Task.FromResult(new AccessPathResult(ordered, ordered.FirstOrDefault(), diagnostics, stats));
    }

    private static DomainAccessPath BuildPath(DomainEffectiveAccess effective, NormalizedEntitlement entitlement, CloudResource resource, IReadOnlyList<Guid> groups, AccessPathRequest request, List<AccessPathDiagnostic> diagnostics)
    {
        var provider = effective.Provider;
        var pathId = Stable($"path|{effective.Id}|{entitlement.Id}|{string.Join(',', groups)}");
        var nodes = new List<AccessPathNode>();
        var edges = new List<AccessPathEdge>();
        var sequence = 0;
        var status = effective.EvaluationStatus == EvaluationStatus.Unsupported ? AccessPathStatus.Unsupported : effective.EvaluationStatus == EvaluationStatus.Invalid ? AccessPathStatus.Invalid : effective.EvaluationStatus is EvaluationStatus.PartiallyEvaluated or EvaluationStatus.ConditionRequired ? AccessPathStatus.Partial : AccessPathStatus.Complete;
        var pathType = groups.Count == 0 ? entitlement.IsConditional ? AccessPathType.Conditional : entitlement.IsInherited ? AccessPathType.Inherited : entitlement.AssignmentType switch { AssignmentType.Policy => AccessPathType.PolicyBased, AssignmentType.Role => AccessPathType.RoleBased, _ => AccessPathType.Direct } : groups.Count > 1 ? AccessPathType.NestedGroup : AccessPathType.GroupBased;
        var principal = request.Normalized.Principals.FirstOrDefault(item => item.Id == effective.PrincipalId);
        AddNode("Principal", effective.PrincipalId, principal?.Name ?? "Unknown principal", entitlement.Evidence.SourceId);
        foreach (var groupId in groups)
        {
            var group = request.GroupMemberships.FirstOrDefault(item => item.GroupId == groupId);
            AddNode(groups.Count > 1 ? "NestedGroup" : "Group", groupId, $"Group {groupId}", group?.Id.ToString());
        }
        if (entitlement.RoleId.HasValue) AddNode("Role", entitlement.RoleId.Value, request.Normalized.Roles.FirstOrDefault(item => item.Id == entitlement.RoleId)?.Name ?? "Unknown role", entitlement.Evidence.SourceId);
        if (entitlement.PolicyId.HasValue) AddNode("Policy", entitlement.PolicyId.Value, request.Normalized.Policies.FirstOrDefault(item => item.Id == entitlement.PolicyId)?.Name ?? "Unknown policy", entitlement.Evidence.SourceId);
        AddNode("Permission", entitlement.PermissionId, request.Normalized.Permissions.FirstOrDefault(item => item.Id == entitlement.PermissionId)?.Name ?? "Unknown permission", entitlement.Evidence.SourceId);
        if (entitlement.IsConditional && entitlement.Condition != null) AddNode("Condition", null, entitlement.Condition.Expression ?? "Condition requires evaluation", entitlement.Evidence.SourceId);
        AddNode("Scope", null, entitlement.Scope.OriginalScope, entitlement.Evidence.SourceId);
        AddNode("Resource", resource.Id, resource.Name, resource.ResourceId);

        var path = new DomainAccessPath { Id = pathId, Provider = provider, CloudAccountId = effective.CloudAccountId, PrincipalId = effective.PrincipalId, ResourceId = resource.Id, TargetResourceId = resource.Id, PermissionId = effective.PermissionId, EffectiveAccessId = effective.Id, PathType = pathType, PathStatus = status, Description = Explain(effective, nodes), Evidence = JsonSerializer.Serialize(entitlement.Evidence), EngineVersion = "0.1.0", CreatedAt = DateTime.UtcNow, Nodes = nodes, Edges = edges };
        if (nodes.Count > request.MaxNodesPerPath) { path.PathStatus = AccessPathStatus.Partial; diagnostics.Add(new("AP-002", "Maximum nodes per path reached.", path.Id)); path.Nodes = path.Nodes.Take(request.MaxNodesPerPath).ToList(); path.Edges = path.Edges.Where(edge => path.Nodes.Any(node => node.Id == edge.FromNodeId) && path.Nodes.Any(node => node.Id == edge.ToNodeId)).ToList(); }
        return path;

        void AddNode(string type, Guid? entityId, string name, string? evidence)
        {
            var node = new AccessPathNode { Id = Stable($"{pathId}|node|{sequence}"), AccessPathId = pathId, NodeType = type, Provider = provider, EntityId = entityId, NodeId = entityId ?? Stable($"{pathId}|{type}|{name}"), Name = name, Sequence = sequence++, EvidenceReference = evidence, Metadata = JsonSerializer.Serialize(new { source = evidence }) };
            if (nodes.Count > 0) edges.Add(new AccessPathEdge { Id = Stable($"{pathId}|edge|{nodes[^1].Id}|{node.Id}"), AccessPathId = pathId, FromNodeId = nodes[^1].Id, ToNodeId = node.Id, Provider = provider, RelationshipType = Relationship(nodes[^1].NodeType, type), EvidenceReference = evidence });
            nodes.Add(node);
        }
    }

    private static List<IReadOnlyList<Guid>> FindMembershipChains(Guid principalId, Guid targetId, IReadOnlyList<GroupMembership> memberships, int maxDepth, List<AccessPathDiagnostic> diagnostics, Guid effectiveAccessId)
    {
        if (principalId == targetId) return new();
        var results = new List<IReadOnlyList<Guid>>();
        var path = new List<Guid>();
        var visited = new HashSet<Guid>();
        Walk(principalId);
        return results;

        void Walk(Guid current)
        {
            if (path.Count >= maxDepth) { diagnostics.Add(new("AP-002", "Maximum group traversal depth reached.", effectiveAccessId)); return; }
            foreach (var membership in memberships.Where(item => item.PrincipalId == current))
            {
                if (!visited.Add(membership.GroupId)) { diagnostics.Add(new("AP-001", "Circular group relationship detected.", effectiveAccessId)); continue; }
                path.Add(membership.GroupId);
                if (membership.GroupId == targetId) results.Add(path.ToArray()); else Walk(membership.GroupId);
                path.RemoveAt(path.Count - 1);
                visited.Remove(membership.GroupId);
            }
        }
    }

    private static HashSet<Guid> ResolvePrincipalClosure(Guid principalId, IReadOnlyList<GroupMembership> memberships)
    {
        var resolved = new HashSet<Guid> { principalId };
        var pending = new Stack<Guid>();
        pending.Push(principalId);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var membership in memberships.Where(item => item.PrincipalId == current))
            {
                if (resolved.Add(membership.GroupId)) pending.Push(membership.GroupId);
            }
        }
        return resolved;
    }

    private static string Relationship(string from, string to) => to switch { "Group" or "NestedGroup" => "MemberOf", "Role" => "AssignedRole", "Policy" => "AssignedPolicy", "Permission" => "GrantsPermission", "Condition" => "ConditionApplies", "Scope" => "ScopedAt", "Resource" => "AppliesTo", _ => "DerivedFrom" };
    private static string PathSignature(DomainAccessPath path) => string.Join('|', path.Nodes.OrderBy(node => node.Sequence).Select(node => $"{node.NodeType}:{node.NodeId}"));
    private static string Explain(DomainEffectiveAccess effective, IReadOnlyList<AccessPathNode> nodes) => $"Effective decision {effective.Decision} for {string.Join(" -> ", nodes.Select(node => node.Name))}.";
    private static Guid Stable(string value) => GuidUtility.Create(GuidUtility.Namespace, value);
}

public sealed class TextAccessPathFormatter : IAccessPathFormatter
{
    public string Format(DomainAccessPath path) => string.Join(Environment.NewLine + "  ↓" + Environment.NewLine, path.Nodes.OrderBy(node => node.Sequence).Select(node => node.Name));
}

internal static class GuidUtility
{
    public static readonly Guid Namespace = Guid.Parse("7c31cbf1-3b9f-4cc2-b9d2-0f3f0f02c6af");
    public static Guid Create(Guid namespaceId, string name)
    {
        var hash = System.Security.Cryptography.SHA1.HashData(namespaceId.ToByteArray().Concat(Encoding.UTF8.GetBytes(name)).ToArray());
        var bytes = hash.Take(16).ToArray(); bytes[6] = (byte)((bytes[6] & 0x0f) | 0x50); bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80); return new Guid(bytes);
    }
}

internal static class AccessPathExtensions
{
    public static bool IsInherited(this DomainAccessPath path) => path.PathType == AccessPathType.Inherited || path.Nodes.Any(node => node.NodeType == "Scope" && node.Name.Contains("inherited", StringComparison.OrdinalIgnoreCase));
}
