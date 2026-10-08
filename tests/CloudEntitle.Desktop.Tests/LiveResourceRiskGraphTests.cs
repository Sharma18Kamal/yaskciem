using System.IO;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using Xunit;

namespace CloudEntitle.Desktop.Tests;

public sealed class LiveResourceRiskGraphTests
{
    [Fact]
    public async Task LiveScan_ShouldCarryTaggedAssetSignalsThroughModeledPathIntoRiskFinding()
    {
        var provider = new TaggedResourceProvider();
        var scanner = new LiveCloudScanService(new ICloudProvider[] { provider });

        var result = await scanner.ScanAsync();

        Assert.Contains(result.ResourceSecuritySignals, signal => signal.Type == ResourceSecuritySignalType.PublicExposure);
        Assert.Contains(result.ResourceSecuritySignals, signal => signal.Type == ResourceSecuritySignalType.SensitiveData);
        Assert.Contains(result.ResourceSecuritySignals, signal => signal.Type == ResourceSecuritySignalType.CriticalAsset);
        Assert.All(result.ResourceSecurityCoverage, item => Assert.Equal(SecurityEvidenceCoverageStatus.Partial, item.Status));
        var path = Assert.Single(result.ModeledAccessPaths);
        Assert.Equal(AccessPathStatus.Partial, path.PathStatus);
        Assert.Contains(path.Nodes, node => node.NodeType == "PublicExposure");
        Assert.Contains(path.Nodes, node => node.NodeType == "SensitiveData");
        Assert.Contains(path.Nodes, node => node.NodeType == "CriticalAsset");
        var graphFinding = Assert.Single(result.ResourceRiskFindings);
        Assert.Equal("Exploitable Identity-to-Workload Path", graphFinding.Title);
        Assert.Equal(RiskCategory.ExternalAccess, graphFinding.Category);
        Assert.Equal(RiskEvaluationState.Potential, graphFinding.EvaluationState);
        Assert.Contains(result.Findings, finding => finding.Id == graphFinding.Id);
    }

    [Fact]
    public async Task KubernetesTrustMismatch_ShouldBecomeWorkloadRiskPathSignal()
    {
        var scanner = new LiveCloudScanService(Array.Empty<ICloudProvider>(), kubernetesRbacProvider: new MismatchedWorkloadProvider());

        var result = await scanner.ScanAsync();

        var signal = Assert.Single(result.ResourceSecuritySignals, item => item.Type == ResourceSecuritySignalType.WorkloadIdentityRisk);
        Assert.True(signal.IsConfirmed);
        Assert.Contains(result.ModeledAccessPaths, path => path.Nodes.Any(node => node.NodeType == "WorkloadIdentityRisk"));
        Assert.Contains(result.ResourceRiskFindings, finding => finding.Category == RiskCategory.ExternalAccess
            && finding.Evidence.Summary.Contains("WorkloadIdentityRisk=True", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LiveScanHistory_ShouldRetainSignalPathAndRiskModels()
    {
        var directory = Path.Combine(Path.GetTempPath(), "YaskCiemLiveGraphHistoryTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var historyStore = new LocalLiveScanHistoryStore(Path.Combine(directory, "scans.jsonl"));
            var scanner = new LiveCloudScanService(new ICloudProvider[] { new TaggedResourceProvider() }, historyStore);
            await scanner.ScanAsync();

            var saved = Assert.Single(await historyStore.GetRecentAsync());

            Assert.Contains(saved.ResourceSecuritySignals, signal => signal.Type == ResourceSecuritySignalType.CriticalAsset);
            Assert.Contains(saved.ResourceSecurityCoverage, item => item.SignalType == ResourceSecuritySignalType.PublicExposure);
            Assert.Single(saved.ModeledAccessPaths);
            Assert.Single(saved.ResourceRiskFindings);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class TaggedResourceProvider : ICloudProvider, IResourceProvider, ICloudEffectiveAccessProvider
    {
        private readonly CloudAccount _account = new()
        {
            Id = Guid.NewGuid(), Name = "Production", CloudProvider = CloudProviderType.Aws,
            ExternalAccountId = "123456789012", IsReadOnly = true
        };
        private readonly CloudPrincipal _principal;
        private readonly CloudResource _resource;

        public TaggedResourceProvider()
        {
            _principal = new CloudPrincipal
            {
                Id = Guid.NewGuid(), AccountId = _account.Id, CloudProvider = CloudProviderType.Aws,
                Name = "prod-service", PrincipalId = "prod-service", IdentityType = IdentityType.ServiceAccount,
                IsPrivileged = true, IsActive = true, UpdatedAt = DateTime.UtcNow
            };
            _resource = new CloudResource
            {
                Id = Guid.NewGuid(), AccountId = _account.Id, Name = "customer-records", Type = "AWS::S3::Bucket",
                ResourceId = "arn:aws:s3:::customer-records", Scope = "arn:aws:s3:::customer-records", UpdatedAt = DateTime.UtcNow,
                Tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["PubliclyAccessible"] = "true",
                    ["DataClassification"] = "confidential",
                    ["CriticalAsset"] = "true"
                }
            };
        }

        public CloudProviderType ProviderType => CloudProviderType.Aws;
        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<IReadOnlyList<CloudAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CloudAccount>>(new[] { _account });
        public Task<IReadOnlyList<CloudPrincipal>> DiscoverIdentitiesAsync(Guid accountId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CloudPrincipal>>(new[] { _principal });
        public Task<IReadOnlyList<CloudRole>> DiscoverRolesAsync(Guid accountId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CloudRole>>(new[] { new CloudRole { Id = Guid.NewGuid(), AccountId = _account.Id, Name = "AdministratorAccess", RoleId = "AdministratorAccess", IsPrivileged = true } });
        public Task<IReadOnlyList<CloudPermission>> DiscoverPermissionsAsync(Guid accountId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CloudPermission>>(Array.Empty<CloudPermission>());
        public Task<IReadOnlyList<CloudResource>> GetResourcesAsync(Guid accountId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CloudResource>>(new[] { _resource });
        public Task<IReadOnlyList<CloudEffectiveAccessAssignment>> DiscoverEffectiveAccessAsync(Guid accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudEffectiveAccessAssignment>>(new[]
            {
                new CloudEffectiveAccessAssignment(_principal.PrincipalId!, "AdministratorAccess", "AWS account", "AWS IAM assignment", true)
            });
    }

    private sealed class MismatchedWorkloadProvider : IKubernetesRbacProvider
    {
        public Task<KubernetesRbacScanResult> ScanKubernetesRbacAsync(CancellationToken cancellationToken = default, CloudProviderType? providerFilter = null)
        {
            var account = new CloudAccount
            {
                Id = Guid.NewGuid(), Name = "cluster-a", ExternalAccountId = "cluster-a", CloudProvider = CloudProviderType.Aws,
                AccountType = AccountType.Cluster, IsReadOnly = true
            };
            var principal = new CloudPrincipal
            {
                Id = Guid.NewGuid(), AccountId = account.Id, CloudProvider = CloudProviderType.Aws,
                Name = "serviceaccount:prod:api", PrincipalId = "serviceaccount:prod:api", IdentityType = IdentityType.ServiceAccount,
                IsPrivileged = true, IsActive = true, UpdatedAt = DateTime.UtcNow
            };
            var workloadTrace = new KubernetesWorkloadTrace(CloudProviderType.Aws, "cluster-a", "prod", "Deployment", "api", "api", "arn:aws:iam::123456789012:role/api",
                new[] { "cluster-admin" }, true, new WorkloadIdentityVerification(WorkloadTrustStatus.Mismatch, "OIDC audience does not match the provider trust policy."));
            var assignment = new CloudEffectiveAccessAssignment(principal.PrincipalId!, "cluster-admin", "clusters/cluster-a", "Kubernetes ClusterRoleBinding", true, "Kubernetes RBAC");
            var clusterResource = new CloudResource { Id = Guid.NewGuid(), AccountId = account.Id, Name = account.Name, Type = "Kubernetes Cluster", ResourceId = "clusters/cluster-a", Scope = "clusters/cluster-a" };
            var scan = new KubernetesClusterScan(account, new[] { principal }, Array.Empty<CloudRole>(), Array.Empty<CloudPermission>(), new[] { assignment }, new[] { clusterResource })
            {
                WorkloadTraces = new[] { workloadTrace }
            };
            return Task.FromResult(new KubernetesRbacScanResult(new[] { scan }, Array.Empty<string>()));
        }
    }
}
