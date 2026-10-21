using System.IO;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Desktop.ViewModels;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.OrganizationManagement.Models;
using CloudEntitle.OrganizationManagement.Services;
using Xunit;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;

namespace CloudEntitle.Desktop.Tests;

public sealed class OrganizationViewModelTests
{
    [Theory]
    [InlineData(OrganizationMemberRole.OrganizationAdmin, true)]
    [InlineData(OrganizationMemberRole.Viewer, false)]
    public async Task CentralMonitoring_ShouldShowFailuresGuardRuleEditsAndClearStateOnSignOut(OrganizationMemberRole role, bool canEdit)
    {
        var directory = Path.Combine(Path.GetTempPath(), "YaskCiemMonitoringViewModelTests", Guid.NewGuid().ToString("N"));
        var tenantId = Guid.NewGuid().ToString();
        var owner = new OrganizationIdentity(Guid.NewGuid(), "owner", tenantId, "owner@example.invalid");
        var identity = role == OrganizationMemberRole.OrganizationAdmin ? owner
            : new OrganizationIdentity(Guid.NewGuid(), "viewer", tenantId, "viewer@example.invalid");
        var service = new OrganizationManagementService(new LocalOrganizationStore(Path.Combine(directory, "organizations.json")));
        var organization = service.CreateOrganization("Shared", "shared", tenantId, owner);
        if (identity != owner)
        {
            service.AddMember(organization.Id, owner, identity.Email, role);
            service.AcceptInvitation(organization.Id, identity);
        }
        try
        {
            var monitor = new TestMonitoringApi();
            var viewModel = new OrganizationViewModel(new TestDemoDataService(), service, new TestSsoService(identity),
                new TestLiveCloudScanService(CreateResult(Guid.NewGuid(), Guid.NewGuid())), new AuthenticatedOrganizationContext(),
                sharedMonitoringApi: monitor);
            await viewModel.SignInAsync();
            await viewModel.RefreshMonitoringAsync();
            Assert.True(viewModel.CanReadMonitoring);
            Assert.Equal(canEdit, viewModel.CanManageNotificationRules);
            Assert.Contains("1 failed", viewModel.MonitoringStatus);
            viewModel.SelectedDelivery = Assert.Single(viewModel.NotificationDeliveries);
            await viewModel.LoadDeliveryAttemptsAsync();
            Assert.Equal(503, Assert.Single(viewModel.DeliveryAttempts).HttpStatus);
            await viewModel.SaveNotificationRuleAsync();
            Assert.Equal(canEdit ? 1 : 0, monitor.Saves);

            monitor.PendingRefresh = new TaskCompletionSource<IReadOnlyList<SharedNotificationRuleDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var inFlight = viewModel.RefreshMonitoringAsync();
            await viewModel.SignOutAsync();
            monitor.PendingRefresh.SetResult(Array.Empty<SharedNotificationRuleDto>());
            await inFlight;
            Assert.Empty(viewModel.NotificationDeliveries);
            Assert.Empty(viewModel.DeliveryAttempts);
            Assert.False(viewModel.CanReadMonitoring);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TenantSummary_ShouldOnlyIncludeBoundAccountsFromLiveScan()
    {
        var tenantId = Guid.NewGuid().ToString();
        var identity = new OrganizationIdentity(Guid.NewGuid(), "entra-subject", tenantId, "owner@example.invalid");
        var storePath = Path.Combine(Path.GetTempPath(), "YaskCiemOrganizationViewModelTests", Guid.NewGuid().ToString("N"), "organizations.json");
        var organizationService = new OrganizationManagementService(new LocalOrganizationStore(storePath));
        var organization = organizationService.CreateOrganization("Acme", "acme", tenantId, identity);
        var priorScanAccountId = Guid.NewGuid();
        var currentScanAccountId = Guid.NewGuid();
        var otherAccountId = Guid.NewGuid();
        organizationService.BindCloudAccount(organization.Id, identity, CloudProvider.Azure, priorScanAccountId, "subscription-bound", "Bound Subscription");
        var liveScan = new TestLiveCloudScanService(CreateResult(currentScanAccountId, otherAccountId));
        var authenticatedContext = new AuthenticatedOrganizationContext();
        var viewModel = new OrganizationViewModel(new TestDemoDataService(), organizationService, new TestSsoService(identity), liveScan, authenticatedContext);

        await viewModel.SignInAsync();

        Assert.Equal(identity, authenticatedContext.Identity);
        Assert.Equal(organization.Id, authenticatedContext.OrganizationId);
        Assert.Equal(1, viewModel.BoundAccountCount);
        Assert.Equal(1, viewModel.ConsolidatedAccountCount);
        Assert.Equal(1, viewModel.ConsolidatedIdentities);
        Assert.Equal(1, viewModel.ConsolidatedResources);
        Assert.Equal(1, viewModel.ConsolidatedFindings);
        Assert.Equal(1, viewModel.ConsolidatedCriticalFindings);
        Assert.Equal(1, viewModel.ConsolidatedEffectiveAccess);
    }

    private static LiveCloudScanResult CreateResult(Guid boundAccountId, Guid otherAccountId)
    {
        var boundPrincipal = new CloudPrincipal { Id = Guid.NewGuid(), AccountId = boundAccountId, Name = "Bound principal", IsActive = true };
        var otherPrincipal = new CloudPrincipal { Id = Guid.NewGuid(), AccountId = otherAccountId, Name = "Other principal", IsActive = true };
        var boundResource = new CloudResource { Id = Guid.NewGuid(), AccountId = boundAccountId, Name = "Bound resource" };
        var otherResource = new CloudResource { Id = Guid.NewGuid(), AccountId = otherAccountId, Name = "Other resource" };
        var boundFinding = new RiskFinding { Id = Guid.NewGuid(), CloudAccountId = boundAccountId, Severity = RiskSeverity.Critical };
        var otherFinding = new RiskFinding { Id = Guid.NewGuid(), CloudAccountId = otherAccountId, Severity = RiskSeverity.High };
        return new LiveCloudScanResult(2, 2, 2, 0, 0, 0, new[] { boundFinding, otherFinding }, DateTime.UtcNow, Array.Empty<string>(),
            new[] { boundPrincipal, otherPrincipal }, new[] { boundResource, otherResource }, Array.Empty<DiscoveredCloudRole>(),
            Array.Empty<DiscoveredCloudPermission>(), DateTime.UtcNow,
            new[]
            {
                new LiveEffectiveAccessRecord("Azure", "bound", "Reader", "/", "test", false, AccountId: "subscription-bound"),
                new LiveEffectiveAccessRecord("Azure", "other", "Owner", "/", "test", true, AccountId: "subscription-other")
            },
            new[]
            {
                new LiveScanAccountRecord("Azure", "Bound Subscription", "subscription-bound", string.Empty, string.Empty, "Scanned") { CloudAccountId = boundAccountId },
                new LiveScanAccountRecord("Azure", "Other Subscription", "subscription-other", string.Empty, string.Empty, "Not selected for this scan") { CloudAccountId = otherAccountId }
            });
    }

    private sealed class TestSsoService(OrganizationIdentity identity) : IOrganizationSsoService
    {
        public Task<OrganizationIdentity> SignInAsync(string tenantId, CancellationToken cancellationToken = default)
            => Task.FromResult(identity);
    }

    private sealed class TestMonitoringApi : ISharedMonitoringApi
    {
        private readonly SharedDeliveryDto _delivery = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "security",
            "Failed", 5, DateTime.UtcNow, null, DateTime.UtcNow, "http_rejected");
        public int Saves { get; private set; }
        public TaskCompletionSource<IReadOnlyList<SharedNotificationRuleDto>>? PendingRefresh { get; set; }
        public Task<IReadOnlyList<SharedNotificationRuleDto>> GetNotificationRulesAsync(Guid organizationId, CancellationToken cancellationToken = default)
            => PendingRefresh?.Task ?? Task.FromResult<IReadOnlyList<SharedNotificationRuleDto>>(Array.Empty<SharedNotificationRuleDto>());
        public Task<SharedNotificationRuleDto> SaveNotificationRuleAsync(Guid organizationId, SharedNotificationRuleDto rule, CancellationToken cancellationToken = default)
        {
            Saves++;
            return Task.FromResult(rule);
        }
        public Task<IReadOnlyList<SharedDriftEventDto>> GetDriftEventsAsync(Guid organizationId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SharedDriftEventDto>>(Array.Empty<SharedDriftEventDto>());
        public Task<IReadOnlyList<SharedDeliveryDto>> GetNotificationDeliveriesAsync(Guid organizationId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SharedDeliveryDto>>(new[] { _delivery });
        public Task<SharedDeliveryStatusDto> GetNotificationDeliveryStatusAsync(Guid organizationId, Guid deliveryId, CancellationToken cancellationToken = default)
            => Task.FromResult(new SharedDeliveryStatusDto(_delivery, new[] { new SharedDeliveryAttemptDto(Guid.NewGuid(), 5,
                DateTime.UtcNow.AddSeconds(-1), DateTime.UtcNow, 503, "http_rejected") }));
    }

    private sealed class TestLiveCloudScanService(LiveCloudScanResult result) : ILiveCloudScanService
    {
        public LiveCloudScanResult? CurrentResult { get; private set; } = result;
        public event EventHandler? ScanCompleted { add { } remove { } }
        public Task<IReadOnlyList<LiveScanAccountRecord>> DiscoverAccountsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CurrentResult!.AccountRecords);
        public Task<LiveCloudScanResult> ScanAsync(CancellationToken cancellationToken = default) => Task.FromResult(CurrentResult!);
        public Task<LiveCloudScanResult> ScanAsync(IReadOnlySet<string>? selectedAccountKeys, CancellationToken cancellationToken = default)
            => Task.FromResult(CurrentResult!);
    }

    private sealed class TestDemoDataService : IDemoDataService
    {
        public event EventHandler? DataChanged { add { } remove { } }
        public bool IsDemoMode => false;
        public Task<DemoStatistics> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DemoStatistics(0, 0, 0, 0, 0, 0, 0, null));
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DemoSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DemoSnapshot(Array.Empty<CloudAccount>(), Array.Empty<CloudPrincipal>(), Array.Empty<CloudResource>(),
                Array.Empty<Entitlement>(), Array.Empty<DomainEffectiveAccess>(), Array.Empty<RiskFindingModel>(), Array.Empty<RecommendationModel>(), Array.Empty<Scan>()));
    }
}
