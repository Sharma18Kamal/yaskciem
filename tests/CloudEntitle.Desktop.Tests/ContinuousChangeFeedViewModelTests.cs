using System.IO;
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
    [Fact]
    public async Task ContinuousFeeds_NeverReadAtSignInRequireExplicitConsentAndPauseSavedPolling()
    {
        using var fixture = new ContinuousFixture();
        var vm = fixture.ViewModel;
        await vm.SignInAsync();
        Assert.Equal(0, fixture.Api.StatusReads);
        Assert.Equal(0, fixture.Api.Connects);
        Assert.True(vm.CanManageContinuousFeeds);
        await vm.RefreshContinuousFeedsAsync();
        vm.SelectedContinuousFeed = Assert.Single(vm.ContinuousFeeds);
        await vm.ConnectContinuousFeedAsync();
        Assert.Equal(0, fixture.Api.Connects);
        Assert.Contains("explicitly consent", vm.ContinuousFeedStatus);
        vm.ContinuousFeedConsent = true;
        await vm.ConnectContinuousFeedAsync();
        Assert.Equal(1, fixture.Api.Connects);
        Assert.False(vm.ContinuousFeedConsent);
        Assert.True(vm.SelectedContinuousFeed!.Connected);
        Assert.All(vm.ContinuousFeedCoverage, c => Assert.False(c.VerifiedComplete));
        await vm.LoadContinuousChangesAsync();
        Assert.Equal(1, fixture.Api.ChangeReads);
        Assert.Equal(1, fixture.Api.Connects);
        await vm.PauseContinuousFeedAsync();
        Assert.Equal(1, fixture.Api.Pauses);
        Assert.False(vm.SelectedContinuousFeed!.Connected);
        Assert.Contains("creates no provider resources", vm.ContinuousFeedSetupInstructions);
    }

    [Fact]
    public async Task ContinuousFeeds_DiscardLateStatusOnSignOutAndUncertainWritesNeverClaimSuccess()
    {
        using var fixture = new ContinuousFixture();
        var vm = fixture.ViewModel;
        await vm.SignInAsync();
        await vm.RefreshContinuousFeedsAsync();
        vm.SelectedContinuousFeed = Assert.Single(vm.ContinuousFeeds);
        fixture.Api.FailConnect = true;
        vm.ContinuousFeedConsent = true;
        await vm.ConnectContinuousFeedAsync();
        Assert.False(vm.SelectedContinuousFeed!.Connected);
        Assert.Contains("uncertain", vm.ContinuousFeedStatus);
        fixture.Api.PendingStatus = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = vm.RefreshContinuousFeedsAsync();
        await vm.SignOutAsync();
        fixture.Api.PendingStatus.SetResult([fixture.Api.State]);
        await pending;
        Assert.Empty(vm.ContinuousFeeds);
        Assert.Empty(vm.ContinuousChanges);
        Assert.Null(vm.SelectedContinuousFeed);
        Assert.False(vm.ContinuousFeedConsent);
    }

    private sealed class ContinuousFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Environment.CurrentDirectory, ".test-continuous-" + Guid.NewGuid().ToString("N"));
        public OrganizationViewModel ViewModel { get; }
        public ContinuousWorkspaceApi Api { get; }
        public ContinuousFixture()
        {
            var identity = new OrganizationIdentity(Guid.NewGuid(), "owner", Guid.NewGuid().ToString(), "owner@example.invalid");
            var service = new OrganizationManagementService(new LocalOrganizationStore(Path.Combine(_directory, "organizations.json")));
            service.CreateOrganization("Continuous", "continuous", identity.TenantId, identity);
            Api = new(service, identity);
            ViewModel = new(new TestDemoDataService(), service, new TestSsoService(identity),
                new TestLiveCloudScanService(CreateResult(Guid.NewGuid(), Guid.NewGuid())), new AuthenticatedOrganizationContext(),
                sharedOrganizationApi: Api, sharedMonitoringApi: new TestMonitoringApi());
            ViewModel.SignInTenantId = identity.TenantId;
        }
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    }

    private sealed class ContinuousWorkspaceApi(OrganizationManagementService service, OrganizationIdentity identity)
        : ISharedOrganizationApi, ISharedContinuousChangeFeedApi
    {
        public ContinuousChangeFeedStatus State { get; private set; } = new("iam", CloudProvider.Aws, "123456789012", "client-bridge",
            Guid.NewGuid(), false, false, null, "Paused", 0, null, null, null, ContinuousChangeFeedValidation.UnknownCoverage());
        public int StatusReads { get; private set; }
        public int Connects { get; private set; }
        public int Pauses { get; private set; }
        public int ChangeReads { get; private set; }
        public bool FailConnect { get; set; }
        public TaskCompletionSource<IReadOnlyList<ContinuousChangeFeedStatus>>? PendingStatus { get; set; }
        public Task<IReadOnlyList<ContinuousChangeFeedStatus>> GetContinuousChangeFeedsAsync(Guid organizationId, CancellationToken token = default)
        { StatusReads++; return PendingStatus?.Task ?? Task.FromResult<IReadOnlyList<ContinuousChangeFeedStatus>>([State]); }
        public Task<ContinuousChangeFeedStatus> ConfigureContinuousChangeFeedAsync(Guid organizationId, string alias,
            ConfigureContinuousChangeFeedRequest request, CancellationToken token = default)
        {
            Connects++;
            if (FailConnect) throw new IOException("raw private transport failure");
            State = State with { Enabled = request.Enabled, Connected = request.Connected, Health = "Pending" };
            return Task.FromResult(State);
        }
        public Task PauseContinuousChangeFeedAsync(Guid organizationId, string alias, CancellationToken token = default)
        { Pauses++; State = State with { Enabled = false, Connected = false, Health = "Paused" }; return Task.CompletedTask; }
        public Task<IReadOnlyList<StoredContinuousChange>> GetContinuousCloudChangesAsync(Guid organizationId, string alias, CancellationToken token = default)
        { ChangeReads++; return Task.FromResult<IReadOnlyList<StoredContinuousChange>>([]); }
        public Task<IReadOnlyList<OrganizationProfile>> GetOrganizationsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(service.GetOrganizations());
        public Task<OrganizationWorkspaceDto> GetWorkspaceAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(new OrganizationWorkspaceDto(service.GetOrganizations().Single(o => o.Id == id),
                service.GetMembers(id, identity).Single(m => m.UserId == identity.UserId), Enum.GetValues<OrganizationAction>(),
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
    }
}
