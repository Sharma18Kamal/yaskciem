using System.IO;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Integration.Contracts;
using Xunit;

namespace CloudEntitle.Desktop.Tests;

public sealed class LiveResourceRiskGraphTests
{
    [Fact]
    public async Task PermissionDocuments_DetectActualToxicPairAsPotentialAndRejectDisplayNameMatching()
    {
        var account = new CloudAccount { Id = Guid.NewGuid(), CloudProvider = CloudProviderType.Aws, ExternalId = "123456789012" };
        var principal = new CloudPrincipal { Id = Guid.NewGuid(), AccountId = account.Id, CloudProvider = CloudProviderType.Aws,
            PrincipalId = "arn:aws:iam::123456789012:user/reader", Name = "reader" };
        var resource = new CloudResource { Id = Guid.NewGuid(), AccountId = account.Id, Type = "AWS::S3::Bucket",
            ResourceId = "arn:aws:s3:::source", Tags = new() { ["DataClassification"] = "confidential" } };
        var grants = new[] { "s3:GetObject", "s3:PutBucketPolicy" }.Select(action => new ObservedPermissionGrant(principal.PrincipalId,
            principal.PrincipalId, "reader", action, "v1", new string('A', 64), action, "AWS account", action,
            resource.ResourceId, "Allow", false, false, false)).ToArray();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new EntitlementEvidenceSnapshot(Guid.NewGuid(), CloudProvider.Aws, account.ExternalId!, now.AddMinutes(-1), now, grants, ["Partial"]);
        var evaluator = new LiveResourceRiskGraphEvaluator();
        var result = await evaluator.EvaluateDocumentsAsync(account, [principal], [resource], snapshot);
        Assert.Contains(result.Findings, finding => finding.Title == "Sensitive storage read plus public access control");
        Assert.All(result.Findings, finding => Assert.Equal(RiskEvaluationState.Potential, finding.EvaluationState));
        Assert.All(result.AccessPaths, path => Assert.NotEqual(AccessPathStatus.Complete, path.PathStatus));
        resource.ResourceUri = resource.ResourceId;
        resource.ResourceId = null;
        var uriOnly = await evaluator.EvaluateDocumentsAsync(account, [principal], [resource], snapshot);
        Assert.Contains(uriOnly.Findings, finding => finding.Title == "Sensitive storage read plus public access control");
        var conditional = await evaluator.EvaluateDocumentsAsync(account, [principal], [resource],
            snapshot with { Grants = [grants[0], grants[1] with { HasCondition = true }] });
        Assert.DoesNotContain(conditional.Findings, finding => finding.Title == "Sensitive storage read plus public access control");
        var denied = await evaluator.EvaluateDocumentsAsync(account, [principal], [resource],
            snapshot with { Grants = [grants[0], grants[1] with { Effect = "Deny" }] });
        Assert.DoesNotContain(denied.Findings, finding => finding.Title == "Sensitive storage read plus public access control");
        principal.Name = principal.PrincipalId;
        principal.PrincipalId = "arn:aws:iam::123456789012:user/other";
        var nameOnly = await evaluator.EvaluateDocumentsAsync(account, [principal], [resource], snapshot);
        Assert.Empty(nameOnly.Findings);
        await Assert.ThrowsAsync<ArgumentException>(() => evaluator.EvaluateDocumentsAsync(account, [principal], [resource], snapshot with { AccountId = "999999999999" }));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    public async Task LiveScan_CredentialOnlyUserRequiresCompleteProviderInventory(bool complete, bool expired, bool expected)
    {
        var provider = new TaggedResourceProvider();
        provider.SetCredentialEvidence(complete, expired);
        var result = await new LiveCloudScanService([provider]).ScanAsync();
        Assert.Empty(result.ResourceRecords);
        Assert.Equal(expected, result.ResourceRiskFindings.Any(item => item.Title == (expired ? "Expired identity credential" : "Stale identity credential")));
    }
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
        var graphFinding = Assert.Single(result.ResourceRiskFindings, finding => finding.Title == "Exploitable Identity-to-Workload Path");
        Assert.Contains(result.ResourceRiskFindings, finding => finding.Title == "Overprivileged Workload Identity");
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
            Assert.Contains(saved.ResourceRiskFindings, finding => finding.Title == "Exploitable Identity-to-Workload Path");
            Assert.Contains(saved.ResourceRiskFindings, finding => finding.Title == "Overprivileged Workload Identity");
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
        private bool _credentialsOnly;

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
        public void SetCredentialEvidence(bool complete, bool expired)
        {
            _credentialsOnly = true;
            _principal.IdentityType = IdentityType.User;
            _principal.CredentialInventoryComplete = complete;
            _principal.CredentialLastRotatedAtUtc = DateTime.UtcNow.AddDays(expired ? -1 : -180);
            _principal.CredentialExpiresAtUtc = expired ? DateTime.UtcNow.AddDays(-1) : null;
            _principal.CredentialEvidenceSource = "Provider complete credential metadata";
        }
        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<IReadOnlyList<CloudAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CloudAccount>>(new[] { _account });
        public Task<IReadOnlyList<CloudPrincipal>> DiscoverIdentitiesAsync(Guid accountId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CloudPrincipal>>(new[] { _principal });
        public Task<IReadOnlyList<CloudRole>> DiscoverRolesAsync(Guid accountId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CloudRole>>(new[] { new CloudRole { Id = Guid.NewGuid(), AccountId = _account.Id, Name = "AdministratorAccess", RoleId = "AdministratorAccess", IsPrivileged = true } });
        public Task<IReadOnlyList<CloudPermission>> DiscoverPermissionsAsync(Guid accountId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CloudPermission>>(Array.Empty<CloudPermission>());
        public Task<IReadOnlyList<CloudResource>> GetResourcesAsync(Guid accountId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CloudResource>>(_credentialsOnly ? [] : new[] { _resource });
        public Task<IReadOnlyList<CloudEffectiveAccessAssignment>> DiscoverEffectiveAccessAsync(Guid accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudEffectiveAccessAssignment>>(_credentialsOnly ? [] : new[]
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
