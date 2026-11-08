using System.IO;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Desktop.ViewModels;
using CloudEntitle.Domain;
using CloudEntitle.IdentityGovernance.Models;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.OrganizationManagement.Models;
using CloudEntitle.OrganizationManagement.Services;
using Xunit;

namespace CloudEntitle.Desktop.Tests;

public sealed partial class OrganizationViewModelTests
{
    [Theory]
    [InlineData(CloudProvider.Azure)]
    [InlineData(CloudProvider.Aws)]
    [InlineData(CloudProvider.Gcp)]
    public async Task TrustCredential_RequiresPerRunConsentAndNeverCollectsAtSignInOrSavedRead(CloudProvider provider)
    {
        using var fixture = new TrustCredentialFixture();
        var vm = fixture.ViewModel;
        await vm.SignInAsync();
        Assert.Equal(0, fixture.Collector.Reads);
        Assert.Equal(0, fixture.Api.TrustUploads);
        vm.SelectedTrustCredentialBinding = Assert.Single(vm.TrustCredentialBindings, item => item.Provider == provider);
        await vm.CollectTrustCredentialEvidenceAsync();
        Assert.Equal(0, fixture.Collector.Reads);
        Assert.Contains("Explicitly authorize", vm.TrustCredentialMonitoringStatus);
        vm.TrustCredentialReadConsent = true;
        await vm.CollectTrustCredentialEvidenceAsync();
        Assert.Equal(1, fixture.Collector.Reads);
        Assert.Equal(provider, fixture.Collector.Query!.Provider);
        Assert.Equal(vm.SelectedTrustCredentialBinding.ExternalTenantId, fixture.Collector.Query.AccountId);
        Assert.Equal(1, fixture.Api.TrustUploads);
        Assert.Single(vm.TrustCredentialObservations);
        Assert.Contains("Partial", vm.TrustCredentialCoverageDetails);
        Assert.False(vm.TrustCredentialReadConsent);
        await vm.LoadTrustCredentialHistoryAsync();
        vm.SelectedTrustCredentialSnapshot = Assert.Single(vm.TrustCredentialHistory);
        await vm.LoadSelectedTrustCredentialSnapshotAsync();
        Assert.Equal(1, fixture.Collector.Reads);
        Assert.Single(vm.TrustCredentialObservations);
        Assert.Equal(provider, fixture.Api.LastTrustReadProvider);
        await vm.CollectTrustCredentialEvidenceAsync();
        Assert.Equal(1, fixture.Collector.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrustCredential_CancelsAndDiscardsLateCollectionOnBindingChangeOrSignOut(bool signOut)
    {
        using var fixture = new TrustCredentialFixture();
        var vm = fixture.ViewModel;
        await vm.SignInAsync();
        vm.SelectedTrustCredentialBinding = vm.TrustCredentialBindings[0];
        vm.TrustCredentialReadConsent = true;
        fixture.Collector.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = vm.CollectTrustCredentialEvidenceAsync();
        Assert.True(vm.TrustCredentialBusy);
        if (signOut) await vm.SignOutAsync();
        else vm.SelectedTrustCredentialBinding = vm.TrustCredentialBindings[1];
        Assert.True(fixture.Collector.Token.IsCancellationRequested);
        Assert.False(vm.TrustCredentialBusy);
        Assert.False(vm.TrustCredentialReadConsent);
        fixture.Collector.Pending.SetResult(fixture.Collector.Snapshot!);
        await pending;
        Assert.Equal(0, fixture.Api.TrustUploads);
        Assert.Empty(vm.TrustCredentialObservations);
        Assert.Empty(vm.TrustCredentialHistory);
        Assert.Empty(vm.TrustCredentialChanges);
        Assert.Empty(vm.PrivilegeEscalationSignals);
        Assert.Empty(vm.TrustCredentialCoverageDetails);
        if (signOut) Assert.Null(vm.SelectedTrustCredentialBinding);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrustCredential_ClearsSavedStateAndDiscardsLateHistoryOnBindingChangeOrSignOut(bool signOut)
    {
        using var fixture = new TrustCredentialFixture();
        var vm = fixture.ViewModel;
        await vm.SignInAsync();
        vm.SelectedTrustCredentialBinding = vm.TrustCredentialBindings[0];
        vm.TrustCredentialReadConsent = true;
        await vm.CollectTrustCredentialEvidenceAsync();
        var saved = Assert.Single(vm.TrustCredentialHistory);
        vm.TrustCredentialReadConsent = true;
        fixture.Api.PendingTrustHistory = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = vm.LoadTrustCredentialHistoryAsync();
        if (signOut) await vm.SignOutAsync();
        else vm.SelectedTrustCredentialBinding = vm.TrustCredentialBindings[1];
        fixture.Api.PendingTrustHistory.SetResult([saved]);
        await pending;
        Assert.Empty(vm.TrustCredentialHistory);
        Assert.Empty(vm.TrustCredentialObservations);
        Assert.Empty(vm.TrustCredentialChanges);
        Assert.Empty(vm.PrivilegeEscalationSignals);
        Assert.Empty(vm.TrustCredentialCoverageDetails);
        Assert.Null(vm.SelectedTrustCredentialSnapshot);
        Assert.False(vm.TrustCredentialReadConsent);
        Assert.False(vm.TrustCredentialBusy);
    }

    [Fact]
    public async Task TrustCredential_DiscardsLateHistoryWhenBindingChangesAwayAndBack()
    {
        using var fixture = new TrustCredentialFixture();
        var vm = fixture.ViewModel;
        await vm.SignInAsync();
        var binding = vm.TrustCredentialBindings[0];
        vm.SelectedTrustCredentialBinding = binding;
        vm.TrustCredentialReadConsent = true;
        await vm.CollectTrustCredentialEvidenceAsync();
        var saved = Assert.Single(vm.TrustCredentialHistory);
        fixture.Api.PendingTrustHistory = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = vm.LoadTrustCredentialHistoryAsync();
        vm.SelectedTrustCredentialBinding = vm.TrustCredentialBindings[1];
        vm.SelectedTrustCredentialBinding = binding;
        fixture.Api.PendingTrustHistory.SetResult([saved]);
        await pending;
        Assert.Empty(vm.TrustCredentialHistory);
        Assert.Empty(vm.TrustCredentialObservations);
    }

    [Fact]
    public async Task TrustCredential_DiscardsLateSavedSnapshotAfterSignOut()
    {
        using var fixture = new TrustCredentialFixture();
        var vm = fixture.ViewModel;
        await vm.SignInAsync();
        vm.SelectedTrustCredentialBinding = vm.TrustCredentialBindings[0];
        vm.TrustCredentialReadConsent = true;
        await vm.CollectTrustCredentialEvidenceAsync();
        vm.SelectedTrustCredentialSnapshot = Assert.Single(vm.TrustCredentialHistory);
        fixture.Api.PendingTrustSnapshot = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = vm.LoadSelectedTrustCredentialSnapshotAsync();
        await vm.SignOutAsync();
        fixture.Api.PendingTrustSnapshot.SetResult(fixture.Collector.Snapshot!);
        await pending;
        Assert.Empty(vm.TrustCredentialObservations);
        Assert.Empty(vm.TrustCredentialCoverageDetails);
        Assert.Null(vm.SelectedTrustCredentialSnapshot);
    }

    private sealed class TrustCredentialFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Environment.CurrentDirectory, $".trust-credential-tests-{Guid.NewGuid():N}");
        public TestMonitoringApi Api { get; } = new();
        public TestTrustCredentialCollector Collector { get; } = new();
        public OrganizationViewModel ViewModel { get; }

        public TrustCredentialFixture()
        {
            var identity = new OrganizationIdentity(Guid.NewGuid(), "owner", Guid.NewGuid().ToString(), "owner@example.invalid");
            var service = new OrganizationManagementService(new LocalOrganizationStore(Path.Combine(_directory, "organizations.json")));
            var organization = service.CreateOrganization("Trust monitoring", "trust-monitoring", identity.TenantId, identity);
            service.BindCloudAccount(organization.Id, identity, CloudProvider.Azure, Guid.NewGuid(), Guid.NewGuid().ToString(), "Azure");
            service.BindCloudAccount(organization.Id, identity, CloudProvider.Aws, Guid.NewGuid(), "123456789012", "AWS");
            service.BindCloudAccount(organization.Id, identity, CloudProvider.Gcp, Guid.NewGuid(), "sample-project-123", "GCP");
            ViewModel = new(new TestDemoDataService(), service, new TestSsoService(identity),
                new TestLiveCloudScanService(CreateResult(Guid.NewGuid(), Guid.NewGuid())), new AuthenticatedOrganizationContext(),
                sharedOrganizationApi: new TrustWorkspaceApi(service, identity) { Monitoring = Api }, sharedMonitoringApi: Api,
                trustCredentialCollector: Collector);
            ViewModel.SignInTenantId = identity.TenantId;
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class TestTrustCredentialCollector : ITrustCredentialEvidenceCollector
    {
        public int Reads { get; private set; }
        public TrustCredentialEvidenceQuery? Query { get; private set; }
        public CancellationToken Token { get; private set; }
        public TaskCompletionSource<TrustCredentialSnapshot>? Pending { get; set; }
        public TrustCredentialSnapshot? Snapshot { get; private set; }
        public Task<TrustCredentialSnapshot> CollectAsync(TrustCredentialEvidenceQuery query, CancellationToken token = default)
        {
            Reads++;
            Query = query;
            Token = token;
            var now = DateTimeOffset.UtcNow;
            Snapshot = new(Guid.NewGuid(), query.Provider, query.AccountId, now.AddSeconds(-1), now, "Fake bounded source",
                [new("principal", "workload", "KeyCredential", "key", "key metadata", null, null, [], null, null, new string('a', 64))],
                ["Partial source coverage; no provider effective decisions."]);
            return Pending?.Task ?? Task.FromResult(Snapshot);
        }
    }

    private sealed partial class TestMonitoringApi : ISharedTrustCredentialMonitoringApi
    {
        private TrustCredentialSnapshot? _trustSnapshot;
        public int TrustUploads { get; private set; }
        public CloudProvider LastTrustReadProvider { get; private set; }
        public TaskCompletionSource<IReadOnlyList<TrustCredentialSnapshotSummary>>? PendingTrustHistory { get; set; }
        public TaskCompletionSource<TrustCredentialSnapshot>? PendingTrustSnapshot { get; set; }
        public Task<TrustCredentialSnapshot> UploadTrustCredentialSnapshotAsync(Guid organizationId, TrustCredentialSnapshot snapshot, CancellationToken token = default)
        {
            TrustUploads++;
            _trustSnapshot = snapshot;
            return Task.FromResult(snapshot);
        }
        public Task<IReadOnlyList<TrustCredentialSnapshotSummary>> GetTrustCredentialHistoryAsync(Guid organizationId, string accountId, CancellationToken token = default)
            => GetTrustCredentialHistoryAsync(organizationId, CloudProvider.Azure, accountId, token);
        public Task<IReadOnlyList<TrustCredentialSnapshotSummary>> GetTrustCredentialHistoryAsync(Guid organizationId, CloudProvider provider, string accountId, CancellationToken token = default)
        {
            LastTrustReadProvider = provider;
            return PendingTrustHistory?.Task ?? Task.FromResult<IReadOnlyList<TrustCredentialSnapshotSummary>>(_trustSnapshot is null ? []
                : [new(_trustSnapshot.SnapshotId, _trustSnapshot.StartedAtUtc, _trustSnapshot.CollectedAtUtc, 1, false, false, Guid.NewGuid())]);
        }
        public Task<TrustCredentialSnapshot> GetTrustCredentialSnapshotAsync(Guid organizationId, string accountId, Guid snapshotId, CancellationToken token = default)
            => GetTrustCredentialSnapshotAsync(organizationId, CloudProvider.Azure, accountId, snapshotId, token);
        public Task<TrustCredentialSnapshot> GetTrustCredentialSnapshotAsync(Guid organizationId, CloudProvider provider, string accountId, Guid snapshotId, CancellationToken token = default)
        {
            LastTrustReadProvider = provider;
            return PendingTrustSnapshot?.Task ?? Task.FromResult(_trustSnapshot!);
        }
        public Task<TrustCredentialMonitoringReport> AnalyzeTrustCredentialMonitoringAsync(Guid organizationId, TrustCredentialMonitoringRequest request, CancellationToken token = default)
            => Task.FromResult(new TrustCredentialMonitoringReport([], [], ["Potential only."]));
    }

    private sealed class TrustWorkspaceApi(OrganizationManagementService service, OrganizationIdentity identity) : ISharedOrganizationApi, ISharedTrustCredentialMonitoringApi
    {
        public Task<IReadOnlyList<OrganizationProfile>> GetOrganizationsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(service.GetOrganizations());
        public Task<OrganizationWorkspaceDto> GetWorkspaceAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(new OrganizationWorkspaceDto(service.GetOrganizations().Single(item => item.Id == id),
                service.GetMembers(id, identity).Single(item => item.UserId == identity.UserId), Enum.GetValues<OrganizationAction>(),
                service.GetMembers(id, identity), service.GetTenantBindings(id, identity), [], []));
        public Task<IReadOnlyList<SharedScanSummaryDto>> GetScanSummariesAsync(Guid id, int limit = 30, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SharedScanSummaryDto>>([]);
        public Task<IReadOnlyList<SharedCiemRecordDto>> GetFindingsAsync(Guid id, Guid? scanId = null, int limit = 500, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SharedCiemRecordDto>>([]);
        public Task<OrganizationProfile> CreateOrganizationAsync(string name, string tenantKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OrganizationMember> InviteMemberAsync(Guid id, string email, OrganizationRoleOption role, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OrganizationMember> UpdateMemberRoleAsync(Guid id, Guid memberId, OrganizationRoleOption role, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OrganizationMember> SetMemberActiveAsync(Guid id, Guid memberId, bool isActive, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TenantBinding> BindAccountAsync(Guid id, BindOrganizationAccountRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OrganizationCustomRole> SaveCustomRoleAsync(Guid id, Guid? roleId, string name, IReadOnlyList<OrganizationAction> permissions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteCustomRoleAsync(Guid id, Guid roleId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AccessReviewCampaign>> GetAccessReviewsAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveAccessReviewAsync(Guid id, AccessReviewCampaign campaign, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetEligibleAccessReviewersAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SharedScanSummaryDto> UploadScanSummaryAsync(Guid id, UploadSharedScanSummaryRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SharedCiemRecordDto>> GetScanRecordsAsync(Guid id, Guid scanId, string? kind = null, string? accountId = null, int limit = 500, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public TestMonitoringApi Monitoring { get; set; } = null!;
        public Task<TrustCredentialSnapshot> UploadTrustCredentialSnapshotAsync(Guid organizationId, TrustCredentialSnapshot snapshot, CancellationToken token = default) => Monitoring.UploadTrustCredentialSnapshotAsync(organizationId, snapshot, token);
        public Task<IReadOnlyList<TrustCredentialSnapshotSummary>> GetTrustCredentialHistoryAsync(Guid organizationId, string accountId, CancellationToken token = default) => Monitoring.GetTrustCredentialHistoryAsync(organizationId, accountId, token);
        public Task<IReadOnlyList<TrustCredentialSnapshotSummary>> GetTrustCredentialHistoryAsync(Guid organizationId, CloudProvider provider, string accountId, CancellationToken token = default) => Monitoring.GetTrustCredentialHistoryAsync(organizationId, provider, accountId, token);
        public Task<TrustCredentialSnapshot> GetTrustCredentialSnapshotAsync(Guid organizationId, string accountId, Guid snapshotId, CancellationToken token = default) => Monitoring.GetTrustCredentialSnapshotAsync(organizationId, accountId, snapshotId, token);
        public Task<TrustCredentialSnapshot> GetTrustCredentialSnapshotAsync(Guid organizationId, CloudProvider provider, string accountId, Guid snapshotId, CancellationToken token = default) => Monitoring.GetTrustCredentialSnapshotAsync(organizationId, provider, accountId, snapshotId, token);
        public Task<TrustCredentialMonitoringReport> AnalyzeTrustCredentialMonitoringAsync(Guid organizationId, TrustCredentialMonitoringRequest request, CancellationToken token = default) => Monitoring.AnalyzeTrustCredentialMonitoringAsync(organizationId, request, token);
    }
}
