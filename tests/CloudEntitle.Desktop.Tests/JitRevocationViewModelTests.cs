using System.IO;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Desktop.ViewModels;
using CloudEntitle.OrganizationManagement.Models;
using CloudEntitle.OrganizationManagement.Services;
using Xunit;

namespace CloudEntitle.Desktop.Tests;

public sealed partial class OrganizationViewModelTests
{
    [Fact]
    public async Task Jit_EarlyEndingRequiresSeparateConsentReasonAndClearsConsentOnContextChanges()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), ".jit-tests-" + Guid.NewGuid().ToString("N"));
        var tenant = Guid.NewGuid().ToString("D");
        var identity = new OrganizationIdentity(Guid.NewGuid(), "owner", tenant, "owner@example.invalid");
        var service = new OrganizationManagementService(new LocalOrganizationStore(Path.Combine(directory, "organizations.json")));
        service.CreateOrganization("Shared", "shared", tenant, identity);
        try
        {
            var api = new TestMonitoringApi { JitRequesterId = identity.UserId };
            var viewModel = new OrganizationViewModel(new TestDemoDataService(), service, new TestSsoService(identity),
                new TestLiveCloudScanService(CreateResult(Guid.NewGuid(), Guid.NewGuid())), new AuthenticatedOrganizationContext(), sharedMonitoringApi: api);
            await viewModel.SignInAsync();
            viewModel.JitTarget = "gcp-investigation";
            viewModel.JitReason = "Incident";
            await viewModel.RunPriorityTwoAsync("request");
            viewModel.SelectedJitRequest = Assert.Single(viewModel.JitRequests);
            viewModel.JitLiveConsent = true;
            await viewModel.RunPriorityTwoAsync("revoke");
            Assert.Equal(1, api.JitWrites);
            Assert.Contains("explicit consent", viewModel.PriorityTwoStatus);
            viewModel.JitRevokeConsent = true;
            viewModel.JitReason = "End incident";
            Assert.False(viewModel.JitRevokeConsent);
            viewModel.JitRevokeConsent = true;
            await viewModel.RunPriorityTwoAsync("revoke");
            Assert.Equal(2, api.JitWrites);
            Assert.False(viewModel.JitRevokeConsent);
            Assert.False(viewModel.JitLiveConsent);
            Assert.Null(viewModel.SelectedJitRequest);
            viewModel.SelectedJitRequest = Assert.Single(viewModel.JitRequests);
            viewModel.JitRevokeConsent = true;
            viewModel.SelectedJitRequest = null;
            Assert.False(viewModel.JitRevokeConsent);
            await viewModel.RunPriorityTwoAsync("handoff");
            Assert.Equal(2, api.JitWrites);
            viewModel.JitRevokeConsent = true;
            await viewModel.SignOutAsync();
            Assert.False(viewModel.JitRevokeConsent);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
