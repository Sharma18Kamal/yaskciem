using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Desktop.ViewModels;
using CloudEntitle.Domain;
using CloudEntitle.IdentityGovernance.Models;
using CloudEntitle.IdentityGovernance.Services;
using CloudEntitle.Integration.Clients;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.OrganizationManagement.Models;
using Xunit;

namespace CloudEntitle.Desktop.Tests;

public sealed partial class AccessReviewViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedGovernance_ShouldUseCentralReportAndVersionedAssignmentWithoutClientLifecycleSaves(bool conflict)
    {
        using var handler = new GovernanceHandler { Conflict = conflict };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.invalid/") };
        var api = new SharedOrganizationApi(new CiemApiClient(http), new GovernanceTokenProvider());
        var context = new AuthenticatedOrganizationContext();
        context.Set(new OrganizationIdentity(Guid.NewGuid(), "manager", Guid.NewGuid().ToString(), "manager@example.invalid"), Guid.NewGuid());
        var viewModel = new AccessReviewViewModel(new EmptyDemoDataService(), new SharedAccessReviewStore(api, context));
        await viewModel.RefreshAsync();
        Assert.Null(viewModel.ErrorMessage);
        Assert.Equal(0, handler.FullSaves);
        Assert.Equal(0, viewModel.Campaigns[0].Items[0].ReminderCount);
        Assert.Equal(1, Assert.Single(viewModel.GovernanceGroups).Overdue);
        Assert.Equal("Unknown account", Assert.Single(viewModel.GovernanceWork).Account);
        Assert.Contains("owner@example.invalid", viewModel.EligibleOwners);
        Assert.Contains("server", viewModel.ReminderMessage, StringComparison.OrdinalIgnoreCase);
        viewModel.AssignedReviewerName = "reviewer@example.invalid";
        viewModel.ItemDueAt = DateTime.Today.AddDays(7);
        Assert.False(viewModel.CanAssignReviewer);
        viewModel.AssignedOwner = "owner@example.invalid";
        viewModel.AssignmentTeam = "Platform";
        viewModel.AssignmentAccount = "123456789012";
        viewModel.AssignmentReason = "Owner rotation";
        Assert.True(viewModel.CanAssignReviewer);
        await viewModel.AssignReviewerAsync();
        var request = Assert.Single(handler.Assignments);
        Assert.Equal(0, request.ExpectedAssignmentVersion);
        Assert.Equal("owner@example.invalid", request.Owner);
        Assert.Equal("Platform", request.Team);
        Assert.Equal("123456789012", request.ExternalAccountId);
        Assert.Equal("Owner rotation", request.Reason);
        Assert.Equal(0, handler.FullSaves);
        if (conflict) Assert.False(string.IsNullOrWhiteSpace(viewModel.ErrorMessage));
        else
        {
            Assert.Null(viewModel.ErrorMessage);
            Assert.Equal(1, viewModel.SelectedItem!.AssignmentVersion);
            Assert.Equal("owner@example.invalid", viewModel.SelectedItem.AssignedOwner);
        }
    }

    private sealed class GovernanceTokenProvider : IApiAccessTokenProvider
    {
        public Task<string> GetApiAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("test-token");
    }

    private sealed class GovernanceHandler : HttpMessageHandler
    {
        private AccessReviewCampaign _campaign = new IdentityGovernanceService().CreateCampaign("Central review",
            new[] { new AccessReviewCandidate(Guid.NewGuid(), Guid.NewGuid(), null, CloudProvider.Aws, "Admin", "Evidence") },
            TimeSpan.FromDays(1), assignedReviewer: "reviewer@example.invalid", asOf: DateTime.UtcNow.AddDays(-2));
        public bool Conflict { get; init; }
        public int FullSaves { get; private set; }
        public List<ReassignReviewItemRequest> Assignments { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Put) { FullSaves++; return new(HttpStatusCode.BadRequest); }
            if (path.EndsWith("/assignment", StringComparison.Ordinal) && request.Method == HttpMethod.Patch)
            {
                var assignment = (await request.Content!.ReadFromJsonAsync<ReassignReviewItemRequest>(cancellationToken))!;
                Assignments.Add(assignment);
                if (Conflict) return new(HttpStatusCode.Conflict);
                _campaign = _campaign with { Items = new[] { _campaign.Items[0] with { AssignedOwner = assignment.Owner,
                    AssignedReviewer = assignment.Reviewer, Team = assignment.Team, ExternalAccountId = assignment.ExternalAccountId,
                    DueAt = assignment.DueAtUtc, AssignmentVersion = 1 } } };
                return Response(_campaign);
            }
            if (path.EndsWith("/access-reviews/reviewers", StringComparison.Ordinal)) return Response(new[] { "reviewer@example.invalid" });
            if (path.EndsWith("/access-reviews", StringComparison.Ordinal)) return Response(new[] { _campaign });
            if (path.EndsWith("/governance/assignees", StringComparison.Ordinal))
                return Response(new GovernanceAssigneesDto(new[] { "owner@example.invalid" }, new[] { "reviewer@example.invalid" }));
            if (path.EndsWith("/governance/report", StringComparison.Ordinal))
                return Response(new GovernanceReportDto(DateTime.UtcNow, new[]
                {
                    new GovernanceWorkItemDto(_campaign.Id, _campaign.Name, _campaign.Items[0].Id, CloudProvider.Aws,
                        "Unknown account", "Unassigned", null, "reviewer@example.invalid", ReviewDecision.Pending,
                        _campaign.DueAt, false, true, null, false, null, 0, 0, null)
                }, new[] { new GovernanceGroupDto(CloudProvider.Aws, "Unknown account", "Unassigned", 1, 0, 1, 1, 0, 0, 0, 0, 0, 0, null) },
                    Array.Empty<GovernanceHistoryDto>()));
            return new(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Response<T>(T data)
            => new(HttpStatusCode.OK) { Content = JsonContent.Create(new ApiEnvelope<T>(data, Guid.NewGuid().ToString("N"), ApiContractVersion.Current)) };
    }
}
