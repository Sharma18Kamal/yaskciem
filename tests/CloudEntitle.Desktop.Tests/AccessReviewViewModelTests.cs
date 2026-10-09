using System.IO;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Desktop.ViewModels;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.IdentityGovernance.Models;
using Xunit;

namespace CloudEntitle.Desktop.Tests;

public sealed class AccessReviewViewModelTests
{
    [Fact]
    public async Task ViewModel_ShouldCreateAssignDecideAndPersistReviewWithEvidence()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new LocalAccessReviewStore(Path.Combine(directory, "access-reviews.json"));
        var provider = new ReviewCloudProvider();
        var scan = new LiveCloudScanService(new[] { provider });
        await scan.ScanAsync();
        var viewModel = new AccessReviewViewModel(new EmptyDemoDataService(), store, scan);

        try
        {
            await viewModel.RefreshAsync();
            viewModel.CampaignName = "Quarterly production access";
            viewModel.SelectedTemplate = viewModel.Templates.Single(item => item.Id == "privileged-workloads");
            viewModel.AssignedReviewerName = "fallback-reviewer@example.invalid";
            viewModel.ActionActor = "security-admin@example.invalid";
            await viewModel.CreateCampaignAsync();

            var campaign = Assert.Single(viewModel.Campaigns);
            var item = Assert.Single(campaign.Items);
            Assert.Equal("privileged-workloads", campaign.TemplateId);
            Assert.Equal(7, (campaign.DueAt - campaign.CreatedAt).Days);
            Assert.Equal("security-admin@example.invalid", campaign.CreatedBy);
            Assert.Equal("owner@example.invalid", item.AssignedReviewer);
            Assert.Equal("owner@example.invalid", item.OwnerSnapshot);
            Assert.Contains("Azure RBAC", item.EvidenceSnapshot);
            Assert.Contains(campaign.AuditTrail, entry => entry.Action == "CampaignCreated");

            viewModel.SelectedCampaign = campaign;
            viewModel.SelectedItem = item;
            viewModel.AssignedReviewerName = "reviewer@example.invalid";
            viewModel.ItemDueAt = DateTime.Today.AddDays(2);
            await viewModel.AssignReviewerAsync();

            viewModel.SelectedItem = Assert.Single(viewModel.SelectedCampaign!.Items);
            viewModel.ReviewerName = "reviewer@example.invalid";
            viewModel.SelectedDecision = ReviewDecision.ExceptionGranted;
            viewModel.ExceptionExpiresAt = DateTime.Today.AddDays(3);
            viewModel.ReviewComment = "Temporary production exception; owner approval attached.";
            viewModel.ActionActor = "security-admin@example.invalid";
            await viewModel.RecordDecisionAsync();

            var persisted = Assert.Single(await store.GetAsync());
            var persistedItem = Assert.Single(persisted.Items);
            Assert.Equal(ReviewDecision.ExceptionGranted, persistedItem.Decision);
            Assert.Equal("reviewer@example.invalid", persistedItem.Reviewer);
            Assert.Contains("owner approval attached", persistedItem.ReviewComment, StringComparison.Ordinal);
            Assert.NotNull(persistedItem.ExceptionExpiresAt);
            Assert.Contains(persistedItem.AuditTrail, entry => entry.Action == "ReviewerAssigned");
            Assert.Contains(persistedItem.AuditTrail, entry => entry.Action == "ExceptionGranted");
            var attestation = Assert.Single(persistedItem.Attestations);
            Assert.Equal("reviewer@example.invalid", attestation.Reviewer);
            Assert.Equal("security-admin@example.invalid", attestation.Actor);
            Assert.Equal(persistedItem.EvidenceSnapshot, attestation.EvidenceSnapshot);
            Assert.Equal(64, attestation.EvidenceSha256.Length);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ReviewCloudProvider : ICloudProvider, ICloudEffectiveAccessProvider
    {
        private readonly Guid _accountId = Guid.NewGuid();
        public CloudProviderType ProviderType => CloudProviderType.Azure;
        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<IReadOnlyList<CloudAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudAccount>>(new[] { new CloudAccount { Id = _accountId, Name = "Production", SubscriptionId = "sub-production", CloudProvider = CloudProviderType.Azure } });
        public Task<IReadOnlyList<CloudPrincipal>> DiscoverIdentitiesAsync(Guid accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudPrincipal>>(new[]
            {
                new CloudPrincipal
                {
                    Id = Guid.NewGuid(), AccountId = accountId, CloudProvider = CloudProviderType.Azure,
                    Name = "admin@example.invalid", PrincipalId = "admin@example.invalid", Email = "admin@example.invalid",
                    IdentityType = IdentityType.ServiceAccount, IsPrivileged = true, IsActive = true,
                    OwnerIds = new List<string> { "owner@example.invalid" }, OwnerInventoryComplete = true
                }
            });
        public Task<IReadOnlyList<CloudRole>> DiscoverRolesAsync(Guid accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudRole>>(Array.Empty<CloudRole>());
        public Task<IReadOnlyList<CloudPermission>> DiscoverPermissionsAsync(Guid accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudPermission>>(Array.Empty<CloudPermission>());
        public Task<IReadOnlyList<CloudEffectiveAccessAssignment>> DiscoverEffectiveAccessAsync(Guid accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CloudEffectiveAccessAssignment>>(new[]
            {
                new CloudEffectiveAccessAssignment("admin@example.invalid", "Owner", "/subscriptions/sub-production", "Azure RBAC", true)
            });
    }

    private sealed class EmptyDemoDataService : IDemoDataService
    {
        public event EventHandler? DataChanged { add { } remove { } }
        public bool IsDemoMode => false;
        public Task<DemoStatistics> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DemoStatistics(0, 0, 0, 0, 0, 0, 0, null));
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DemoSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DemoSnapshot(Array.Empty<CloudAccount>(), Array.Empty<CloudPrincipal>(), Array.Empty<CloudResource>(), Array.Empty<Entitlement>(), Array.Empty<CloudEntitle.Domain.Entities.EffectiveAccess>(), Array.Empty<RiskFindingModel>(), Array.Empty<RecommendationModel>(), Array.Empty<Scan>()));
    }
}
