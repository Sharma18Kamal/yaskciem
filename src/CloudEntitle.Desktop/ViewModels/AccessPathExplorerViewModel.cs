using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;
using CloudEntitle.Desktop.Services;

namespace CloudEntitle.Desktop.ViewModels;

public sealed record AccessPathNodeRow(int Sequence, string Type, string Name, string Evidence);
public sealed record AccessPathEdgeRow(string From, string Relationship, string To, string Confidence);

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
        }
    }

    public IReadOnlyList<AccessPathExplorerRow> Paths => _records
        .Where(record => !record.AssignmentType.Equals("Role usage", StringComparison.OrdinalIgnoreCase))
        .Select(CreatePath)
        .Where(path => string.IsNullOrWhiteSpace(SearchText)
            || new[] { path.Provider, path.Principal, path.Role, path.Scope, path.PathType, path.Evidence }
                .Any(value => value.Contains(SearchText, StringComparison.OrdinalIgnoreCase)))
        .ToArray();

    public IReadOnlyList<AccessPathNodeRow> SelectedNodes => SelectedPath?.Nodes ?? Array.Empty<AccessPathNodeRow>();
    public IReadOnlyList<AccessPathEdgeRow> SelectedEdges => SelectedPath?.Edges ?? Array.Empty<AccessPathEdgeRow>();
    public bool HasResults => Paths.Count > 0;
    public string CoverageMessage => "Paths connect provider-reported assignments to their stated scope. Workload identity annotations are configured links, not proof of cloud trust or permission evaluation.";

    public async Task RefreshAsync()
    {
        try
        {
            var currentAccess = _liveCloudScanService?.CurrentResult?.EffectiveAccessRecords;
            if (currentAccess is not null)
            {
                _records = FilterConnectedProvider(currentAccess).ToArray();
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
            }
            else
            {
                _records = Array.Empty<LiveEffectiveAccessRecord>();
            }

            SelectedPath = Paths.FirstOrDefault();
            OnPropertyChanged(nameof(Paths));
            OnPropertyChanged(nameof(HasResults));
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
