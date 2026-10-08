using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Desktop.Services;
using DomainAccessPath = CloudEntitle.Domain.Entities.AccessPath;

namespace CloudEntitle.Desktop.ViewModels;

public sealed record AccessPathNodeRow(int Sequence, string Type, string Name, string Evidence);
public sealed record AccessPathEdgeRow(string From, string Relationship, string To, string Confidence);
public sealed record AccessPathVisualNodeRow(int Sequence, string Type, string Name, string Evidence, string Relationship, string Confidence);
public sealed record AccessBlastRadiusSummary(int RelatedAssignments, int RelatedRoles, int ObservedResources, string Scope, string Assessment, string Caveat);

public sealed record AccessPathExplorerRow(
    string Provider,
    string Principal,
    string Role,
    string Scope,
    string PathType,
    string Evidence,
    string Confidence,
    bool Privileged,
    IReadOnlyList<AccessPathNodeRow> Nodes,
    IReadOnlyList<AccessPathEdgeRow> Edges);

public sealed class AccessPathExplorerViewModel : WorkspaceViewModel
{
    private readonly ILiveCloudScanService? _liveCloudScanService;
    private readonly ILiveScanHistoryStore? _liveScanHistoryStore;
    private readonly CloudConnectionState? _cloudConnectionState;
    private IReadOnlyList<LiveEffectiveAccessRecord> _records = Array.Empty<LiveEffectiveAccessRecord>();
    private IReadOnlyList<KubernetesWorkloadTrace> _workloadTraces = Array.Empty<KubernetesWorkloadTrace>();
    private IReadOnlyList<DomainAccessPath> _modeledPaths = Array.Empty<DomainAccessPath>();
    private IReadOnlyList<ResourceSecuritySignal> _resourceSignals = Array.Empty<ResourceSecuritySignal>();
    private string _searchText = string.Empty;
    private AccessPathExplorerRow? _selectedPath;

    public AccessPathExplorerViewModel(
        IDemoDataService demoData,
        ILiveCloudScanService? liveCloudScanService = null,
        ILiveScanHistoryStore? liveScanHistoryStore = null,
        CloudConnectionState? cloudConnectionState = null) : base(demoData)
    {
        _liveCloudScanService = liveCloudScanService;
        _liveScanHistoryStore = liveScanHistoryStore;
        _cloudConnectionState = cloudConnectionState;
        if (liveCloudScanService is not null) liveCloudScanService.ScanCompleted += (_, _) => _ = RefreshAsync();
        if (cloudConnectionState is not null) cloudConnectionState.PropertyChanged += (_, _) => _ = RefreshAsync();
        _ = RefreshAsync();
    }

    public override string Title => "Access Paths";
    public override string Subtitle => "Explore provider-reported identity-to-scope access paths.";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            OnPropertyChanged(nameof(Paths));
            OnPropertyChanged(nameof(HasResults));
        }
    }

    public AccessPathExplorerRow? SelectedPath
    {
        get => _selectedPath;
        set
        {
            if (!SetProperty(ref _selectedPath, value)) return;
            OnPropertyChanged(nameof(SelectedNodes));
            OnPropertyChanged(nameof(SelectedEdges));
            OnPropertyChanged(nameof(SelectedVisualNodes));
            OnPropertyChanged(nameof(BlastRadius));
        }
    }

    public IReadOnlyList<AccessPathExplorerRow> Paths => _records
        .Where(record => !record.AssignmentType.Equals("Role usage", StringComparison.OrdinalIgnoreCase))
        .Select(CreatePath)
        .Concat(_workloadTraces.Select(CreateWorkloadPath))
        .Concat(_modeledPaths.Select(CreateModeledPath))
        .Where(path => string.IsNullOrWhiteSpace(SearchText)
            || new[] { path.Provider, path.Principal, path.Role, path.Scope, path.PathType, path.Evidence }
                .Any(value => value.Contains(SearchText, StringComparison.OrdinalIgnoreCase)))
        .ToArray();

    public IReadOnlyList<AccessPathNodeRow> SelectedNodes => SelectedPath?.Nodes ?? Array.Empty<AccessPathNodeRow>();
    public IReadOnlyList<AccessPathEdgeRow> SelectedEdges => SelectedPath?.Edges ?? Array.Empty<AccessPathEdgeRow>();
    public IReadOnlyList<AccessPathVisualNodeRow> SelectedVisualNodes => SelectedPath is null
        ? Array.Empty<AccessPathVisualNodeRow>()
        : SelectedPath.Nodes.Select((node, index) =>
        {
            var edge = SelectedPath.Edges.ElementAtOrDefault(index);
            return new AccessPathVisualNodeRow(node.Sequence, node.Type, node.Name, node.Evidence, edge?.Relationship ?? string.Empty, edge?.Confidence ?? string.Empty);
        }).ToArray();
    public AccessBlastRadiusSummary? BlastRadius => SelectedPath is null ? null : BuildBlastRadius(SelectedPath);
    public bool HasResults => Paths.Count > 0;
    public int ResourceSignalCount => _resourceSignals.Count;
    public int ModeledResourcePathCount => _modeledPaths.Count;
    public string CoverageMessage => $"Live graph: {ModeledResourcePathCount} resource paths, {ResourceSignalCount} security signals. Tag/label signals and role-level access paths remain partial until provider-native action and security coverage is available. Kubernetes federation checks remain explicit.";

    public async Task RefreshAsync()
    {
        try
        {
            var currentAccess = _liveCloudScanService?.CurrentResult?.EffectiveAccessRecords;
            if (currentAccess is not null)
            {
                var current = _liveCloudScanService!.CurrentResult!;
                _records = FilterConnectedProvider(currentAccess).ToArray();
                _workloadTraces = FilterConnectedWorkloads(current.WorkloadTraces).ToArray();
                _modeledPaths = FilterConnectedModeledPaths(current.ModeledAccessPaths).ToArray();
                _resourceSignals = current.ResourceSecuritySignals;
            }
            else if (_liveScanHistoryStore is not null)
            {
                var history = await _liveScanHistoryStore.GetRecentAsync();
                var connected = _cloudConnectionState?.ConnectedProviders ?? new HashSet<CloudProviderType>();
                var relevantHistory = connected.Count == 1
                    ? history.Where(scan => scan.EffectiveAccess.Any(item => item.Provider.Equals(connected.Single().ToString(), StringComparison.OrdinalIgnoreCase)))
                    : history;
                var latest = relevantHistory.OrderByDescending(scan => scan.StartedAt).FirstOrDefault();
                _records = latest is null ? Array.Empty<LiveEffectiveAccessRecord>() : FilterConnectedProvider(latest.EffectiveAccess).ToArray();
                _workloadTraces = latest is null ? Array.Empty<KubernetesWorkloadTrace>() : FilterConnectedWorkloads(latest.WorkloadTraces).ToArray();
                _modeledPaths = latest is null ? Array.Empty<DomainAccessPath>() : FilterConnectedModeledPaths(latest.ModeledAccessPaths).ToArray();
                _resourceSignals = latest?.ResourceSecuritySignals ?? Array.Empty<ResourceSecuritySignal>();
            }
            else
            {
                _records = Array.Empty<LiveEffectiveAccessRecord>();
                _workloadTraces = Array.Empty<KubernetesWorkloadTrace>();
                _modeledPaths = Array.Empty<DomainAccessPath>();
                _resourceSignals = Array.Empty<ResourceSecuritySignal>();
            }

            SelectedPath = Paths.FirstOrDefault();
            OnPropertyChanged(nameof(Paths));
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(ResourceSignalCount));
            OnPropertyChanged(nameof(ModeledResourcePathCount));
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Unable to load access paths: {exception.Message}";
        }
    }

    private IEnumerable<LiveEffectiveAccessRecord> FilterConnectedProvider(IEnumerable<LiveEffectiveAccessRecord> records)
    {
        var connected = _cloudConnectionState?.ConnectedProviders ?? new HashSet<CloudProviderType>();
        return connected.Count == 1
            ? records.Where(record => record.Provider.Equals(connected.Single().ToString(), StringComparison.OrdinalIgnoreCase))
            : records;
    }

    private IEnumerable<KubernetesWorkloadTrace> FilterConnectedWorkloads(IEnumerable<KubernetesWorkloadTrace> traces)
    {
        var connected = _cloudConnectionState?.ConnectedProviders ?? new HashSet<CloudProviderType>();
        return connected.Count == 1
            ? traces.Where(trace => trace.Provider == connected.Single())
            : traces;
    }

    private IEnumerable<DomainAccessPath> FilterConnectedModeledPaths(IEnumerable<DomainAccessPath> paths)
    {
        var connected = _cloudConnectionState?.ConnectedProviders ?? new HashSet<CloudProviderType>();
        return connected.Count == 1
            ? paths.Where(path => path.Provider.ToString().Equals(connected.Single().ToString(), StringComparison.OrdinalIgnoreCase))
            : paths;
    }

    private AccessPathExplorerRow CreateModeledPath(DomainAccessPath path)
    {
        var nodes = path.Nodes.OrderBy(node => node.Sequence)
            .Select(node => new AccessPathNodeRow(node.Sequence + 1, node.NodeType, node.Name,
                string.IsNullOrWhiteSpace(node.EvidenceReference) ? "Provider scan" : node.EvidenceReference))
            .ToArray();
        var nodeNames = path.Nodes.ToDictionary(node => node.Id, node => node.Name);
        var edges = path.Edges.Select(edge => new AccessPathEdgeRow(
            nodeNames.GetValueOrDefault(edge.FromNodeId, edge.FromNodeId.ToString("D")), edge.RelationshipType,
            nodeNames.GetValueOrDefault(edge.ToNodeId, edge.ToNodeId.ToString("D")), edge.EvidenceReference ?? "Modeled provider path")).ToArray();
        var principal = path.Nodes.FirstOrDefault(node => node.NodeType == "Principal")?.Name ?? path.PrincipalId.ToString("D");
        var role = path.Nodes.FirstOrDefault(node => node.NodeType == "Role")?.Name ?? "Role assignment";
        var scope = path.Nodes.FirstOrDefault(node => node.NodeType == "Scope")?.Name ?? "Scope not reported";
        var privileged = _records.Any(record => record.Provider.Equals(path.Provider.ToString(), StringComparison.OrdinalIgnoreCase)
            && record.Principal.Equals(principal, StringComparison.OrdinalIgnoreCase) && record.IsPrivileged);
        return new AccessPathExplorerRow(path.Provider.ToString(), principal, role, scope, $"Modeled {path.PathType} ({path.PathStatus})",
            path.Evidence ?? "Signal-bearing path from current scan", "Partial provider evidence; not an authorization simulation.", privileged, nodes, edges);
    }

    private AccessBlastRadiusSummary BuildBlastRadius(AccessPathExplorerRow path)
    {
        var subjectNames = path.Nodes
            .Where(node => node.Type is "Identity" or "Kubernetes ServiceAccount" or "Cloud identity")
            .Select(node => node.Name)
            .Append(path.Principal)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var related = _records.Where(record => record.Provider.Equals(path.Provider, StringComparison.OrdinalIgnoreCase)
            && subjectNames.Contains(record.Principal)).ToArray();
        var scope = path.Scope.TrimEnd('/');
        var resources = (_liveCloudScanService?.CurrentResult?.ResourceRecords ?? Array.Empty<CloudResource>())
            .Count(resource => IsWithinScope(scope, resource.ResourceId ?? resource.ExternalId ?? resource.Scope));
        var assessment = path.Privileged
            ? "Privileged path; review the identity, grants, and scope."
            : "Review the identity's observed grants at this scope.";
        return new AccessBlastRadiusSummary(
            related.Length,
            related.Select(item => item.Role).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            resources,
            string.IsNullOrWhiteSpace(scope) ? "Scope not reported" : scope,
            assessment,
            "Observed counts are a lower bound from the current scan; unenumerated resources and indirect provider permissions are not included.");
    }

    private static bool IsWithinScope(string scope, string? resourceId)
    {
        if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(resourceId)) return false;
        var resource = resourceId.TrimEnd('/');
        return resource.Equals(scope, StringComparison.OrdinalIgnoreCase)
            || resource.StartsWith(scope + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static AccessPathExplorerRow CreateWorkloadPath(KubernetesWorkloadTrace trace)
    {
        var serviceAccount = $"serviceaccount:{trace.Namespace}:{trace.ServiceAccount}";
        var roleText = trace.KubernetesRoles.Count == 0 ? "No scanned Kubernetes role binding" : string.Join(", ", trace.KubernetesRoles);
        var scope = $"clusters/{trace.Cluster}/namespaces/{trace.Namespace}";
        var trust = trace.Trust.Status switch
        {
            WorkloadTrustStatus.Verified => $"Cloud trust verified: {trace.Trust.Details}",
            WorkloadTrustStatus.NotConfigured => $"No cloud identity configured: {trace.Trust.Details}",
            _ => $"Cloud trust {trace.Trust.Status}: {trace.Trust.Details}"
        };
        var nodes = new List<AccessPathNodeRow>
        {
            new(1, trace.WorkloadKind, trace.WorkloadName, $"Kubernetes context {trace.Cluster}"),
            new(2, "Kubernetes ServiceAccount", serviceAccount, $"Token injection {(trace.TokenInjectionEnabled ? "enabled" : "not observed")}"),
            new(3, "Kubernetes RBAC", roleText, "Resolved from current RoleBinding and ClusterRoleBinding scan")
        };
        var edges = new List<AccessPathEdgeRow>
        {
            new($"{trace.WorkloadKind}/{trace.WorkloadName}", "uses ServiceAccount", serviceAccount, "Pod or Deployment spec"),
            new(serviceAccount, "receives Kubernetes roles", roleText, "Current RBAC bindings")
        };
        if (!string.IsNullOrWhiteSpace(trace.CloudIdentity))
        {
            nodes.Add(new AccessPathNodeRow(4, "Cloud identity", trace.CloudIdentity, trust));
            edges.Add(new AccessPathEdgeRow(serviceAccount, "federates to", trace.CloudIdentity, trust));
        }
        return new AccessPathExplorerRow(trace.Provider.ToString(), serviceAccount, roleText, scope, $"Kubernetes {trace.WorkloadKind}", trace.Trust.Details, trust, trace.KubernetesRoles.Any(role => role.Contains("cluster-admin", StringComparison.OrdinalIgnoreCase)), nodes, edges);
    }

    private static AccessPathExplorerRow CreatePath(LiveEffectiveAccessRecord record)
    {
        var nodes = new List<AccessPathNodeRow>();
        var edges = new List<AccessPathEdgeRow>();
        var pathType = record.AssignmentType switch
        {
            "Kubernetes RBAC" => "Kubernetes RBAC",
            "ServiceIdentity" => "Workload identity",
            "Inherited" => "Inherited assignment",
            _ => "Provider assignment"
        };
        var identityMarker = "Kubernetes workload identity via ";
        var serviceAccount = record.AssignmentType == "ServiceIdentity" && record.Source.StartsWith(identityMarker, StringComparison.OrdinalIgnoreCase)
            ? record.Source[identityMarker.Length..]
            : null;

        AddNode("Identity", record.Principal, record.Source);
        if (!string.IsNullOrWhiteSpace(serviceAccount))
        {
            AddNode("Kubernetes ServiceAccount", serviceAccount, record.Source);
            edges.Add(new AccessPathEdgeRow(record.Principal, "Mapped by ServiceAccount annotation", serviceAccount, "Configured only; cloud trust not verified"));
        }
        else if (record.AssignmentType == "Kubernetes RBAC")
        {
            AddNode("Kubernetes Binding", record.Source, record.Source);
            edges.Add(new AccessPathEdgeRow(record.Principal, "Subject of binding", record.Source, "Provider-reported RBAC binding"));
        }

        AddNode("Role", record.Role, record.Source);
        edges.Add(new AccessPathEdgeRow(nodes[^2].Name, "Granted role", record.Role, record.Source));
        AddNode("Scope", record.Scope, record.Source);
        edges.Add(new AccessPathEdgeRow(record.Role, "Applies at", record.Scope, record.Source));

        var confidence = serviceAccount is not null
            ? "Configured workload identity link; cloud-side trust unverified"
            : "Provider-reported assignment; not an authorization simulation";
        return new AccessPathExplorerRow(record.Provider, record.Principal, record.Role, record.Scope, pathType, record.Source, confidence, record.IsPrivileged, nodes, edges);

        void AddNode(string type, string name, string evidence) => nodes.Add(new AccessPathNodeRow(nodes.Count + 1, type, name, evidence));
    }
}
