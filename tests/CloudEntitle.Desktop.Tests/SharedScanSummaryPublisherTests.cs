using CloudEntitle.Application.Interfaces;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.IdentityGovernance.Models;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.OrganizationManagement.Models;
using Xunit;

namespace CloudEntitle.Desktop.Tests;

public sealed class SharedScanSummaryPublisherTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishCurrentAsync_ShouldIncludeOnlyScannedAccountsBoundToOrganization(bool complete)
    {
        var organizationId = Guid.NewGuid();
        var boundAccountId = Guid.NewGuid();
        var unrelatedAccountId = Guid.NewGuid();
        var scan = new LiveCloudScanResult(2, 2, 3, 1, 2, 1,
            new[]
            {
                new RiskFinding { Id = Guid.NewGuid(), RuleId = Guid.NewGuid(), Title = "Admin risk", Principal = "admin", CloudProvider = CloudProviderType.Aws, CloudAccountId = boundAccountId, Severity = RiskSeverity.Critical },
                new RiskFinding { Id = Guid.NewGuid(), CloudProvider = CloudProviderType.Azure, CloudAccountId = unrelatedAccountId, Severity = RiskSeverity.High }
            },
            DateTime.UtcNow, Array.Empty<string>(),
            new[]
            {
                new CloudPrincipal { Id = Guid.NewGuid(), AccountId = boundAccountId },
                new CloudPrincipal { Id = Guid.NewGuid(), AccountId = unrelatedAccountId }
            },
            new[]
            {
                new CloudResource { Id = Guid.NewGuid(), AccountId = boundAccountId },
                new CloudResource { Id = Guid.NewGuid(), AccountId = unrelatedAccountId },
                new CloudResource { Id = Guid.NewGuid(), AccountId = unrelatedAccountId }
            },
            new[] { new DiscoveredCloudRole(CloudProviderType.Aws, new CloudRole { Id = Guid.NewGuid(), AccountId = boundAccountId }) },
            new[]
            {
                new DiscoveredCloudPermission(CloudProviderType.Aws, new CloudPermission { Id = Guid.NewGuid(), AccountId = boundAccountId }),
                new DiscoveredCloudPermission(CloudProviderType.Azure, new CloudPermission { Id = Guid.NewGuid(), AccountId = unrelatedAccountId })
            }, DateTime.UtcNow.AddMinutes(-5),
            new[]
            {
                new LiveEffectiveAccessRecord("Aws", "role", "ReadOnly", "scope", "IAM", false, AccountId: "123456789012"),
                new LiveEffectiveAccessRecord("Azure", "user", "Reader", "scope", "RBAC", false, AccountId: "unbound-subscription")
            },
            new[]
            {
                new LiveScanAccountRecord("Aws", "Production", "123456789012", "", "profile", "Scanned") { CloudAccountId = boundAccountId },
                new LiveScanAccountRecord("Azure", "Other", "unbound-subscription", "", "profile", "Scanned") { CloudAccountId = unrelatedAccountId },
                new LiveScanAccountRecord("Aws", "Skipped", "999999999999", "", "profile", "Not selected") { CloudAccountId = Guid.NewGuid() }
            })
        {
            Coverage = new[]
            {
                new ScanCoverageRecord("Aws", "Resources", complete ? ScanCoverageStatus.Collected : ScanCoverageStatus.Partial, 1, "Tagged resources"),
                new ScanCoverageRecord("Aws", "Effective access", complete ? ScanCoverageStatus.Collected : ScanCoverageStatus.Partial, 1, "IAM access"),
                new ScanCoverageRecord("Azure", "Resources", ScanCoverageStatus.Collected, 2, "Resource inventory")
            }
        };
        var scanService = new FakeLiveScanService(scan);
        var sharedApi = new FakeSharedOrganizationApi(organizationId, boundAccountId);
        var publisher = new SharedScanSummaryPublisher(scanService, sharedApi);

        var saved = await publisher.PublishCurrentAsync(organizationId);

        Assert.NotNull(saved);
        Assert.Equal("123456789012", Assert.Single(saved.Accounts).ExternalAccountId);
        Assert.Equal(1, saved.Identities);
        Assert.Equal(1, saved.Resources);
        Assert.Equal(1, saved.Roles);
        Assert.Equal(1, saved.Permissions);
        Assert.Equal(1, saved.EffectiveAccess);
        Assert.Equal(1, saved.Findings);
        Assert.Equal(1, saved.CriticalFindings);
        Assert.Equal(1, sharedApi.UploadCount);
        Assert.NotNull(sharedApi.UploadRequest);
        Assert.Contains(sharedApi.UploadRequest.Records, record => record.Kind == "identity" && record.ExternalAccountId == "123456789012");
        Assert.Contains(sharedApi.UploadRequest.Records, record => record.Kind == "finding" && record.Provider == CloudProvider.Aws);
        Assert.Contains(sharedApi.UploadRequest.Records, record => record.Kind == "effective-access" && record.Provider == CloudProvider.Aws);
        Assert.DoesNotContain(sharedApi.UploadRequest.Records, record => record.ExternalAccountId == "unbound-subscription");
        Assert.Equal(complete ? 3 : 0, sharedApi.UploadRequest.MonitoringScopes.Count);
        if (complete)
        {
            Assert.All(sharedApi.UploadRequest.MonitoringScopes, scope => Assert.Equal("123456789012", scope.ExternalAccountId));
            var finding = Assert.Single(SharedMonitoringProjection.Project(sharedApi.UploadRequest.Records, "finding"));
            Assert.Equal(SharedAlertSeverity.Critical, finding.Severity);
        }
    }

    private sealed class FakeLiveScanService : ILiveCloudScanService
    {
        private readonly LiveCloudScanResult _result;

        public FakeLiveScanService(LiveCloudScanResult result) => _result = result;

        public LiveCloudScanResult? CurrentResult => _result;
        public event EventHandler? ScanCompleted { add { } remove { } }
        public Task<IReadOnlyList<LiveScanAccountRecord>> DiscoverAccountsAsync(CancellationToken cancellationToken = default) => Task.FromResult(_result.AccountRecords);
        public Task<LiveCloudScanResult> ScanAsync(CancellationToken cancellationToken = default) => Task.FromResult(_result);
        public Task<LiveCloudScanResult> ScanAsync(IReadOnlySet<string>? selectedAccountKeys, CancellationToken cancellationToken = default) => Task.FromResult(_result);
    }

    private sealed class FakeSharedOrganizationApi(Guid organizationId, Guid accountId) : ISharedOrganizationApi
    {
        public int UploadCount { get; private set; }
        public UploadSharedScanSummaryRequest? UploadRequest { get; private set; }
        public Task<IReadOnlyList<OrganizationProfile>> GetOrganizationsAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<OrganizationWorkspaceDto> GetWorkspaceAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(new OrganizationWorkspaceDto(new OrganizationProfile(organizationId, "Shared", "shared", OrganizationStatus.Active, DateTime.UtcNow),
                new OrganizationMember(Guid.NewGuid(), organizationId, Guid.NewGuid(), "owner@example.invalid", OrganizationMemberRole.OrganizationAdmin, true, DateTime.UtcNow),
                new[] { OrganizationAction.ViewInventory, OrganizationAction.RunScan }, Array.Empty<OrganizationMember>(),
                new[] { new TenantBinding(Guid.NewGuid(), organizationId, CloudProvider.Aws, accountId, "123456789012", "Production", DateTime.UtcNow) },
                Array.Empty<OrganizationCustomRole>(), Array.Empty<OrganizationAuditEntry>()));
        public Task<OrganizationProfile> CreateOrganizationAsync(string name, string tenantKey, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<OrganizationMember> InviteMemberAsync(Guid id, string email, OrganizationRoleOption role, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<OrganizationMember> UpdateMemberRoleAsync(Guid id, Guid memberId, OrganizationRoleOption role, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<OrganizationMember> SetMemberActiveAsync(Guid id, Guid memberId, bool isActive, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<TenantBinding> BindAccountAsync(Guid id, BindOrganizationAccountRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<OrganizationCustomRole> SaveCustomRoleAsync(Guid id, Guid? roleId, string name, IReadOnlyList<OrganizationAction> permissions, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DeleteCustomRoleAsync(Guid id, Guid roleId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AccessReviewCampaign>> GetAccessReviewsAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task SaveAccessReviewAsync(Guid id, AccessReviewCampaign campaign, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> GetEligibleAccessReviewersAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<SharedScanSummaryDto> UploadScanSummaryAsync(Guid id, UploadSharedScanSummaryRequest request, CancellationToken cancellationToken = default)
        {
            UploadCount++;
            UploadRequest = request;
            return Task.FromResult(new SharedScanSummaryDto(request.ScanId, id, Guid.NewGuid(), Guid.NewGuid(), request.StartedAtUtc, request.CompletedAtUtc,
                request.Provider, request.Accounts, request.Identities, request.Resources, request.Roles, request.Permissions, request.EffectiveAccess,
                request.Findings, request.CriticalFindings, request.HighFindings, request.Coverage));
        }
        public Task<IReadOnlyList<SharedScanSummaryDto>> GetScanSummariesAsync(Guid id, int limit = 30, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<SharedCiemRecordDto>> GetScanRecordsAsync(Guid id, Guid scanId, string? kind = null, string? accountId = null, int limit = 500, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<SharedCiemRecordDto>> GetFindingsAsync(Guid id, Guid? scanId = null, int limit = 500, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
