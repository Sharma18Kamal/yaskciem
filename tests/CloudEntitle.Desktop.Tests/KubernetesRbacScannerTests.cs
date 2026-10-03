using CloudEntitle.Application.Interfaces;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Domain;
using Xunit;

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
    public async Task LiveScan_ShouldIncludeKubernetesClusterAndAccessPaths()
    {
        var scanner = new KubernetesRbacScanner(new FakeKubectlRunner());
        var liveScan = new LiveCloudScanService(Array.Empty<ICloudProvider>(), kubernetesRbacProvider: scanner);

        var result = await liveScan.ScanAsync();

        Assert.Equal(1, result.AccountCount);
        Assert.Contains(result.AccountRecords, account => account.PermissionSet == "Kubernetes RBAC");
        Assert.Contains(result.EffectiveAccessRecords, assignment => assignment.Principal == "arn:aws:iam::123456789012:role/app");
        Assert.Contains(result.ResourceRecords, resource => resource.Type == "Kubernetes Cluster");
    }

    private sealed class FakeKubectlRunner : ICloudCommandRunner
    {
        private readonly bool _includeAllProviders;

        public FakeKubectlRunner(bool includeAllProviders = false) => _includeAllProviders = includeAllProviders;

        public Task<CloudCommandResult> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
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

            return Task.FromResult(new CloudCommandResult(0, "{\"items\":[]}", string.Empty));
        }
    }
}
