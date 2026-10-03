using System.Text.Json;
using System.IO;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Desktop.ViewModels;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using Xunit;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;

namespace CloudEntitle.Desktop.Tests;

public sealed class KubernetesRbacScannerTests
{
    [Fact]
    public async Task Scanner_ShouldMapEksRbacAndServiceAccountCloudIdentity()
    {
        var scanner = new KubernetesRbacScanner(new FakeKubectlRunner());

        var result = await scanner.ScanKubernetesRbacAsync();

        var cluster = Assert.Single(result.Clusters);
        Assert.Equal(CloudProviderType.Aws, cluster.Cluster.CloudProvider);
        Assert.Equal(AccountType.Cluster, cluster.Cluster.AccountType);
        Assert.Contains(cluster.Roles, role => role.Name == "ClusterRole/cluster-admin" && role.IsPrivileged);
        Assert.Contains(cluster.AccessAssignments, assignment => assignment.Principal == "serviceaccount:apps:app" && assignment.Role == "ClusterRole/cluster-admin");
        Assert.Contains(cluster.AccessAssignments, assignment => assignment.Principal == "arn:aws:iam::123456789012:role/app" && assignment.AssignmentType == "ServiceIdentity");
        Assert.Contains(cluster.Permissions, permission => permission.Action == "*" && permission.Resource == "*/*");
        var podTrace = Assert.Single(cluster.WorkloadTraces, trace => trace.WorkloadKind == "Pod");
        Assert.Equal("apps/app", $"{podTrace.Namespace}/{podTrace.ServiceAccount}");
        Assert.Contains("ClusterRole/cluster-admin", podTrace.KubernetesRoles);
        Assert.Equal(WorkloadTrustStatus.Verified, podTrace.Trust.Status);
        Assert.Contains(cluster.Coverage, item => item.DataSource.EndsWith("Pods and Deployments", StringComparison.Ordinal) && item.Status == ScanCoverageStatus.Collected);
    }

    [Fact]
    public async Task Scanner_ShouldIdentifyAksAndGkeContexts()
    {
        var scanner = new KubernetesRbacScanner(new FakeKubectlRunner(includeAllProviders: true));

        var result = await scanner.ScanKubernetesRbacAsync();

        Assert.Equal(3, result.Clusters.Count);
        Assert.Contains(result.Clusters, cluster => cluster.Cluster.CloudProvider == CloudProviderType.Azure);
        Assert.Contains(result.Clusters, cluster => cluster.Cluster.CloudProvider == CloudProviderType.Gcp);
        Assert.Contains(result.Clusters, cluster => cluster.Cluster.CloudProvider == CloudProviderType.Aws);
    }

    [Fact]
    public async Task Scanner_ShouldOnlyReadContextsMatchingConnectedProvider()
    {
        var scanner = new KubernetesRbacScanner(new FakeKubectlRunner(includeAllProviders: true));

        var result = await scanner.ScanKubernetesRbacAsync(providerFilter: CloudProviderType.Gcp);

        var cluster = Assert.Single(result.Clusters);
        Assert.Equal(CloudProviderType.Gcp, cluster.Cluster.CloudProvider);
        Assert.DoesNotContain(result.ProviderMessages, message => message.Contains("eks", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.ProviderMessages, message => message.Contains("aks", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LiveScan_ShouldIncludeKubernetesClusterAndAccessPaths()
    {
        var scanner = new KubernetesRbacScanner(new FakeKubectlRunner());
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var historyStore = new LocalLiveScanHistoryStore(Path.Combine(directory, "scans.jsonl"));
        var liveScan = new LiveCloudScanService(Array.Empty<ICloudProvider>(), historyStore, kubernetesRbacProvider: scanner);

        var result = await liveScan.ScanAsync();
        var history = Assert.Single(await historyStore.GetRecentAsync());
        var accessPaths = new AccessPathExplorerViewModel(new EmptyDemoDataService(), liveCloudScanService: liveScan);
        await accessPaths.RefreshAsync();

        Assert.Equal(1, result.AccountCount);
        Assert.Contains(result.AccountRecords, account => account.PermissionSet == "Kubernetes RBAC");
        Assert.Contains(result.EffectiveAccessRecords, assignment => assignment.Principal == "arn:aws:iam::123456789012:role/app");
        Assert.Contains(result.ResourceRecords, resource => resource.Type == "Kubernetes Cluster");
        Assert.Contains(result.Coverage, item => item.DataSource.EndsWith("Pods and Deployments", StringComparison.Ordinal) && item.Status == ScanCoverageStatus.Collected);
        Assert.Equal(2, history.WorkloadTraces.Count);
        var workloadPath = Assert.Single(accessPaths.Paths, path => path.PathType == "Kubernetes Deployment");
        Assert.Contains(workloadPath.Nodes, node => node.Type == "Kubernetes ServiceAccount" && node.Name == "serviceaccount:apps:app");
        Assert.Contains(workloadPath.Edges, edge => edge.Relationship == "federates to" && edge.Confidence.Contains("verified", StringComparison.OrdinalIgnoreCase));
        Directory.Delete(directory, recursive: true);
    }

    private sealed class FakeKubectlRunner : ICloudCommandRunner
    {
        private readonly bool _includeAllProviders;

        public FakeKubectlRunner(bool includeAllProviders = false) => _includeAllProviders = includeAllProviders;

        public Task<CloudCommandResult> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            if (command == "aws")
            {
                if (arguments.Contains("get-caller-identity")) return Task.FromResult(new CloudCommandResult(0, "{\"Account\":\"123456789012\"}", string.Empty));
                if (arguments.Contains("describe-cluster")) return Task.FromResult(new CloudCommandResult(0, "{\"cluster\":{\"identity\":{\"oidc\":{\"issuer\":\"https://oidc.eks.us-east-1.amazonaws.com/id/EXAMPLE\"}}}}", string.Empty));
                if (arguments.Contains("get-role"))
                {
                    var trust = "{\"Version\":\"2012-10-17\",\"Statement\":[{\"Effect\":\"Allow\",\"Principal\":{\"Federated\":\"arn:aws:iam::123456789012:oidc-provider/oidc.eks.us-east-1.amazonaws.com/id/EXAMPLE\"},\"Action\":\"sts:AssumeRoleWithWebIdentity\",\"Condition\":{\"StringEquals\":{\"oidc.eks.us-east-1.amazonaws.com/id/EXAMPLE:aud\":\"sts.amazonaws.com\",\"oidc.eks.us-east-1.amazonaws.com/id/EXAMPLE:sub\":\"system:serviceaccount:apps:app\"}}}]}";
                    return Task.FromResult(new CloudCommandResult(0, JsonSerializer.Serialize(new { Role = new { AssumeRolePolicyDocument = JsonDocument.Parse(trust).RootElement } }), string.Empty));
                }
            }
            if (arguments.SequenceEqual(new[] { "config", "view", "--output=json" }))
            {
                var output = _includeAllProviders
                    ? "{\"contexts\":[{\"name\":\"arn:aws:eks:us-east-1:123456789012:cluster/demo\",\"context\":{\"cluster\":\"demo-cluster\",\"user\":\"demo-user\"}},{\"name\":\"aks-prod\",\"context\":{\"cluster\":\"aks-cluster\",\"user\":\"aks-user\"}},{\"name\":\"gke_demo_region_cluster\",\"context\":{\"cluster\":\"gke-cluster\",\"user\":\"gke-user\"}}],\"clusters\":[{\"name\":\"demo-cluster\",\"cluster\":{\"server\":\"https://demo.eks.amazonaws.com\"}},{\"name\":\"aks-cluster\",\"cluster\":{\"server\":\"https://aks.hcp.eastus.azmk8s.io\"}},{\"name\":\"gke-cluster\",\"cluster\":{\"server\":\"https://gke.gke.goog\"}}],\"users\":[{\"name\":\"demo-user\",\"user\":{\"exec\":{\"command\":\"aws\"}}},{\"name\":\"aks-user\",\"user\":{\"exec\":{\"command\":\"kubelogin\"}}},{\"name\":\"gke-user\",\"user\":{\"exec\":{\"command\":\"gke-gcloud-auth-plugin\"}}}]}"
                    : "{\"contexts\":[{\"name\":\"arn:aws:eks:us-east-1:123456789012:cluster/demo\",\"context\":{\"cluster\":\"demo-cluster\",\"user\":\"demo-user\"}}],\"clusters\":[{\"name\":\"demo-cluster\",\"cluster\":{\"server\":\"https://demo.eks.amazonaws.com\"}}],\"users\":[{\"name\":\"demo-user\",\"user\":{\"exec\":{\"command\":\"aws\"}}}]}";
                return Task.FromResult(new CloudCommandResult(0, output, string.Empty));
            }

            if (arguments.Contains("clusterroles,clusterrolebindings"))
            {
                return Task.FromResult(new CloudCommandResult(0,
                    "{\"items\":[{\"kind\":\"ClusterRole\",\"metadata\":{\"name\":\"cluster-admin\"},\"rules\":[{\"apiGroups\":[\"*\"],\"resources\":[\"*\"],\"verbs\":[\"*\"]}]},{\"kind\":\"ClusterRoleBinding\",\"metadata\":{\"name\":\"app-admin\"},\"roleRef\":{\"kind\":\"ClusterRole\",\"name\":\"cluster-admin\"},\"subjects\":[{\"kind\":\"ServiceAccount\",\"name\":\"app\",\"namespace\":\"apps\"}]}]}", string.Empty));
            }

            if (arguments.Contains("serviceaccounts"))
            {
                return Task.FromResult(new CloudCommandResult(0,
                    "{\"items\":[{\"kind\":\"ServiceAccount\",\"metadata\":{\"name\":\"app\",\"namespace\":\"apps\",\"annotations\":{\"eks.amazonaws.com/role-arn\":\"arn:aws:iam::123456789012:role/app\"}}}]}", string.Empty));
            }

            if (arguments.Contains("pods,deployments"))
            {
                return Task.FromResult(new CloudCommandResult(0,
                    "{\"items\":[{\"kind\":\"Pod\",\"metadata\":{\"name\":\"app-0\",\"namespace\":\"apps\"},\"spec\":{\"serviceAccountName\":\"app\"}},{\"kind\":\"Deployment\",\"metadata\":{\"name\":\"app\",\"namespace\":\"apps\"},\"spec\":{\"template\":{\"metadata\":{\"labels\":{}},\"spec\":{\"serviceAccountName\":\"app\"}}}}]}", string.Empty));
            }

            return Task.FromResult(new CloudCommandResult(0, "{\"items\":[]}", string.Empty));
        }
    }

    private sealed class EmptyDemoDataService : IDemoDataService
    {
        public event EventHandler? DataChanged { add { } remove { } }
        public bool IsDemoMode => false;
        public Task<DemoStatistics> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DemoStatistics(0, 0, 0, 0, 0, 0, 0, null));
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DemoSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DemoSnapshot(Array.Empty<CloudAccount>(), Array.Empty<CloudPrincipal>(), Array.Empty<CloudResource>(), Array.Empty<Entitlement>(), Array.Empty<DomainEffectiveAccess>(), Array.Empty<RiskFindingModel>(), Array.Empty<RecommendationModel>(), Array.Empty<Scan>()));
    }
}
