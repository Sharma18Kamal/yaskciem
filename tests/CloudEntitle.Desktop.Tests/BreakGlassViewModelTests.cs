using System.Globalization;
using System.IO;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Desktop.ViewModels;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.OrganizationManagement.Models;
using CloudEntitle.OrganizationManagement.Services;
using Xunit;

namespace CloudEntitle.Desktop.Tests;

public sealed partial class OrganizationViewModelTests
{
    [Fact]
    public async Task BreakGlass_RequiresExplicitCompletedReportAndDiscardsLateResponsesAfterSignOut()
    {
        var directory = Path.Combine(Path.GetTempPath(), "YaskCiemEmergencyReportTests", Guid.NewGuid().ToString("N"));
        var tenant = Guid.NewGuid().ToString("D");
        var identity = new OrganizationIdentity(Guid.NewGuid(), "owner", tenant, "owner@example.invalid");
        var service = new OrganizationManagementService(new LocalOrganizationStore(Path.Combine(directory, "organizations.json")));
        var organization = service.CreateOrganization("Emergency", "emergency", tenant, identity);
        service.BindCloudAccount(organization.Id, identity, CloudProvider.Aws, Guid.NewGuid(), "123456789012", "AWS");
        try
        {
            var api = new TestMonitoringApi { JitRequesterId = identity.UserId };
            var viewModel = new OrganizationViewModel(new TestDemoDataService(), service, new TestSsoService(identity),
                new TestLiveCloudScanService(CreateResult(Guid.NewGuid(), Guid.NewGuid())), new AuthenticatedOrganizationContext(), sharedMonitoringApi: api);
            await viewModel.SignInAsync();
            Assert.Equal(0, api.EmergencyWrites);
            await viewModel.RunBreakGlassAsync("report");
            Assert.Equal(0, api.EmergencyWrites);
            viewModel.SelectedActivityBinding = Assert.Single(viewModel.Bindings);
            viewModel.EmergencyPrincipal = "client-emergency";
            viewModel.EmergencyReason = "Client SSO outage";
            viewModel.EmergencyIncidentReference = "INC-12345";
            var now = DateTimeOffset.UtcNow;
            viewModel.EmergencyStartUtc = now.AddHours(-2).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            viewModel.EmergencyEndUtc = now.AddHours(1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            await viewModel.RunBreakGlassAsync("report");
            Assert.Equal(0, api.EmergencyWrites);
            viewModel.EmergencyEndUtc = now.AddHours(-1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            await viewModel.RunBreakGlassAsync("report");
            Assert.Equal(1, api.EmergencyWrites);
            viewModel.SelectedEmergencyReport = Assert.Single(viewModel.EmergencyReports);
            await viewModel.RunBreakGlassAsync("review");
            Assert.Equal(1, api.EmergencyWrites);
            Assert.Contains("different identity", viewModel.EmergencyReportStatus);
            viewModel.EmergencyClientVerifiedClosure = true;
            viewModel.EmergencyReviewReason = "Changed text clears closure attestation";
            Assert.False(viewModel.EmergencyClientVerifiedClosure);
            api.PendingEmergencyReports = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = viewModel.RunBreakGlassAsync("refresh");
            var late = Assert.Single(viewModel.EmergencyReports);
            await viewModel.SignOutAsync();
            api.PendingEmergencyReports.SetResult([late]);
            await pending;
            Assert.Empty(viewModel.EmergencyReports);
            Assert.Null(viewModel.SelectedEmergencyReport);
            Assert.Empty(viewModel.EmergencyPrincipal);
            Assert.False(viewModel.EmergencyClientVerifiedClosure);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
