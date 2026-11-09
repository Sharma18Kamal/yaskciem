using System.IO;
using System.Net.Http;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Desktop.ViewModels;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Integration.Contracts;
using ProviderActivityEvent = CloudEntitle.Integration.Contracts.CloudActivityEvent;
using CloudEntitle.OrganizationManagement.Models;
using CloudEntitle.OrganizationManagement.Services;
using Xunit;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;

namespace CloudEntitle.Desktop.Tests;

public sealed partial class OrganizationViewModelTests
{
    [Fact]
    public async Task PriorityTwo_RequiresExplicitActivationAndClearsConsentAndLateResultsOnContextChange()
    {
        var directory = Path.Combine(Path.GetTempPath(), "YaskCiemPriorityTwoTests", Guid.NewGuid().ToString("N"));
        var tenant = Guid.NewGuid().ToString();
        var identity = new OrganizationIdentity(Guid.NewGuid(), "owner", tenant, "owner@example.invalid");
        var service = new OrganizationManagementService(new LocalOrganizationStore(Path.Combine(directory, "organizations.json")));
        var organization = service.CreateOrganization("Shared", "shared", tenant, identity);
        try
        {
            var api = new TestMonitoringApi { JitRequesterId = identity.UserId };
            var viewModel = new OrganizationViewModel(new TestDemoDataService(), service, new TestSsoService(identity),
                new TestLiveCloudScanService(CreateResult(Guid.NewGuid(), Guid.NewGuid())), new AuthenticatedOrganizationContext(), sharedMonitoringApi: api);
            await viewModel.SignInAsync();
            Assert.Equal(0, api.JitWrites);
            viewModel.JitTarget = "target";
            viewModel.JitReason = "Investigation";
            await viewModel.RunPriorityTwoAsync("request");
            Assert.Equal(1, api.JitWrites);
            viewModel.SelectedJitRequest = Assert.Single(viewModel.JitRequests);
            await viewModel.RunPriorityTwoAsync("activate");
            Assert.Equal(1, api.JitWrites);
            Assert.Contains("explicit live consent", viewModel.PriorityTwoStatus);
            viewModel.JitLiveConsent = true;
            await viewModel.RunPriorityTwoAsync("activate");
            Assert.Equal(2, api.JitWrites);
            Assert.False(viewModel.JitLiveConsent);
            api.PendingJit = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = viewModel.RunPriorityTwoAsync("refresh");
            await viewModel.SignOutAsync();
            api.PendingJit.SetResult([new(Guid.NewGuid(), identity.UserId, "foreign-context", "", "", "", 60, false, "Late",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(30), "Approved", 2, [])]);
            await pending;
            Assert.Empty(viewModel.JitRequests);
            Assert.Empty(viewModel.JitAudit);
            Assert.Empty(viewModel.BehaviorAnomalies);
            Assert.False(viewModel.JitLiveConsent);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
    [Fact]
    public async Task EntitlementDocuments_RequireExplicitConsentAndDiscardChangedContextAndNeverReadOnSavedLoad()
    {
        var directory = Path.Combine(Path.GetTempPath(), "YaskCiemEntitlementViewModelTests", Guid.NewGuid().ToString("N"));
        var tenant = Guid.NewGuid().ToString();
        var identity = new OrganizationIdentity(Guid.NewGuid(), "owner", tenant, "owner@example.invalid");
        var service = new OrganizationManagementService(new LocalOrganizationStore(Path.Combine(directory, "organizations.json")));
        var organization = service.CreateOrganization("Shared", "shared", tenant, identity);
        service.BindCloudAccount(organization.Id, identity, CloudProvider.Gcp, Guid.NewGuid(), "client-project", "GCP");
        try
        {
            var api = new TestMonitoringApi();
            var collector = new TestEntitlementCollector();
            var viewModel = new OrganizationViewModel(new TestDemoDataService(), service, new TestSsoService(identity),
                new TestLiveCloudScanService(CreateResult(Guid.NewGuid(), Guid.NewGuid())), new AuthenticatedOrganizationContext(),
                sharedMonitoringApi: api, entitlementCollector: collector);
            await viewModel.SignInAsync();
            Assert.Equal(0, collector.Reads);
            viewModel.SelectedActivityBinding = Assert.Single(viewModel.Bindings);
            await viewModel.CollectEntitlementEvidenceAsync();
            Assert.Equal(0, collector.Reads);
            Assert.Contains("explicit on-demand", viewModel.EntitlementEvidenceStatus);
            viewModel.EntitlementReadConsent = true;
            await viewModel.CollectEntitlementEvidenceAsync();
            Assert.Equal(1, collector.Reads);
            Assert.Equal(1, api.EntitlementUploads);
            Assert.Single(viewModel.ObservedGrants);
            Assert.False(viewModel.EntitlementReadConsent);
            await viewModel.LoadEntitlementHistoryAsync();
            viewModel.SelectedEntitlementSnapshot = Assert.Single(viewModel.EntitlementHistory);
            await viewModel.LoadEntitlementSnapshotAsync();
            Assert.Single(viewModel.ObservedGrants);
            Assert.Equal(1, collector.Reads);
            Assert.Contains("No live cloud call", viewModel.EntitlementEvidenceStatus);
            await viewModel.EvaluateDocumentRiskAsync();
            Assert.Contains("Matching non-demo", viewModel.EntitlementEvidenceStatus);
            Assert.Empty(viewModel.DocumentRiskCandidates);
            collector.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            viewModel.EntitlementReadConsent = true;
            var pending = viewModel.CollectEntitlementEvidenceAsync();
            viewModel.ActivityProfile = "different-context";
            collector.Pending.SetResult(collector.Snapshot!);
            await pending;
            Assert.Equal(1, api.EntitlementUploads);
            Assert.Empty(viewModel.ObservedGrants);
            Assert.Empty(viewModel.EntitlementHistory);
            Assert.False(viewModel.EntitlementReadConsent);
            collector.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            collector.Pending.SetException(new InvalidOperationException("Provider document read unavailable."));
            viewModel.EntitlementReadConsent = true;
            await viewModel.CollectEntitlementEvidenceAsync();
            Assert.Contains("Provider document read unavailable", viewModel.EntitlementEvidenceStatus);
            Assert.Empty(viewModel.ObservedGrants);
            await viewModel.SignOutAsync();
            Assert.Empty(viewModel.ObservedGrants);
            Assert.Empty(viewModel.HistoricalGrantCorrelations);
            Assert.Empty(viewModel.DocumentRiskCandidates);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class TestEntitlementCollector : IEntitlementEvidenceCollector
    {
        public int Reads { get; private set; }
        public TaskCompletionSource<EntitlementEvidenceSnapshot>? Pending { get; set; }
        public EntitlementEvidenceSnapshot? Snapshot { get; private set; }
        public Task<EntitlementEvidenceSnapshot> CollectAsync(EntitlementEvidenceQuery query, CancellationToken token = default)
        {
            Reads++;
            var now = DateTimeOffset.UtcNow;
            Snapshot ??= new(Guid.NewGuid(), query.Provider, query.AccountId, now.AddMinutes(-1), now,
                [new("serviceAccount:reader@example.invalid", "serviceAccount:reader@example.invalid", "roles/viewer", "project-iam",
                    "v1", new string('A', 64), "Read", "projects/client-project", "storage.objects.get", "projects/client-project",
                    "Allow", false, false, false)], ["Partial document evidence only"]);
            return Pending?.Task ?? Task.FromResult(Snapshot);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InboundAlerts_ReadOnlyOnDemandAndDiscardResponsesAfterContextChanges(bool signOut)
    {
        var directory = Path.Combine(Path.GetTempPath(), "YaskCiemInboundViewModelTests", Guid.NewGuid().ToString("N"));
        var tenant = Guid.NewGuid().ToString();
        var identity = new OrganizationIdentity(Guid.NewGuid(), "owner", tenant, "owner@example.invalid");
        var service = new OrganizationManagementService(new LocalOrganizationStore(Path.Combine(directory, "organizations.json")));
        var organization = service.CreateOrganization("Shared", "shared", tenant, identity);
        try
        {
            var api = new TestMonitoringApi();
            var viewModel = new OrganizationViewModel(new TestDemoDataService(), service, new TestSsoService(identity),
                new TestLiveCloudScanService(CreateResult(Guid.NewGuid(), Guid.NewGuid())), new AuthenticatedOrganizationContext(),
                sharedMonitoringApi: api);
            await viewModel.SignInAsync();
            Assert.Equal(0, api.AlertReads);
            viewModel.SecurityAlertChannel = "https://invalid.example.invalid";
            await viewModel.RefreshSecurityAlertsAsync();
            Assert.Equal(0, api.AlertReads);
            Assert.Contains("safe configured channel", viewModel.SecurityAlertStatus);
            viewModel.SecurityAlertChannel = "client-security";
            viewModel.SecurityAlertProduct = "Sentinel";
            await viewModel.RefreshSecurityAlertsAsync();
            Assert.Equal((organization.Id, "Sentinel", "client-security"), api.LastAlertScope);
            Assert.Single(viewModel.SecurityAlerts);
            Assert.Contains("Untrusted SIEM evidence", viewModel.SecurityAlertStatus);
            api.PendingAlerts = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var refresh = viewModel.RefreshSecurityAlertsAsync();
            if (signOut) await viewModel.SignOutAsync();
            else viewModel.SecurityAlertProduct = "QRadar";
            api.PendingAlerts.SetResult(api.SavedAlerts);
            await refresh;
            Assert.Empty(viewModel.SecurityAlerts);
            Assert.DoesNotContain("1 saved", viewModel.SecurityAlertStatus);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InboundAlerts_ReportApiFailureWithoutSuccessFallback()
    {
        var directory = Path.Combine(Path.GetTempPath(), "YaskCiemInboundFailureTests", Guid.NewGuid().ToString("N"));
        var tenant = Guid.NewGuid().ToString();
        var identity = new OrganizationIdentity(Guid.NewGuid(), "owner", tenant, "owner@example.invalid");
        var service = new OrganizationManagementService(new LocalOrganizationStore(Path.Combine(directory, "organizations.json")));
        service.CreateOrganization("Shared", "shared", tenant, identity);
        try
        {
            var api = new TestMonitoringApi { PendingAlerts = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            api.PendingAlerts.SetException(new HttpRequestException("Shared server is unavailable."));
            var viewModel = new OrganizationViewModel(new TestDemoDataService(), service, new TestSsoService(identity),
                new TestLiveCloudScanService(CreateResult(Guid.NewGuid(), Guid.NewGuid())), new AuthenticatedOrganizationContext(),
                sharedMonitoringApi: api);
            await viewModel.SignInAsync();
            await viewModel.RefreshSecurityAlertsAsync();
            Assert.Contains("Inbound alert read failed", viewModel.SecurityAlertStatus);
            Assert.Contains("Shared server is unavailable", viewModel.SecurityAlertStatus);
            Assert.Empty(viewModel.SecurityAlerts);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("verified", true)]
    [InlineData("mismatch", false)]
    [InlineData("denied", false)]
    public async Task Binding_ShouldRequireFreshMatchingVerification(string outcome, bool expectedBinding)
    {
        var directory = Path.Combine(Path.GetTempPath(), "YaskCiemBindingTests", Guid.NewGuid().ToString("N"));
        var tenant = Guid.NewGuid().ToString();
        var identity = new OrganizationIdentity(Guid.NewGuid(), "owner", tenant, "owner@example.invalid");
        var service = new OrganizationManagementService(new LocalOrganizationStore(Path.Combine(directory, "organizations.json")));
        var organization = service.CreateOrganization("Test", "test", tenant, identity);
        try
        {
            var scan = new TestLiveCloudScanService(CreateResult(Guid.NewGuid(), Guid.NewGuid()));
            var launcher = new VerificationLauncher(outcome);
            var viewModel = new OrganizationViewModel(new TestDemoDataService(), service, new TestSsoService(identity),
                scan, new AuthenticatedOrganizationContext(), cloudAuthentication: launcher);
            await viewModel.SignInAsync();
            viewModel.SelectedAccount = viewModel.AvailableAccounts.First();
            await viewModel.BindAccountAsync();
            Assert.Equal(1, launcher.Checks);
            Assert.Equal(expectedBinding ? 1 : 0, service.GetTenantBindings(organization.Id, identity).Count);
            if (!expectedBinding) Assert.Contains("Unable to bind", viewModel.ErrorMessage);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class VerificationLauncher(string outcome) : ICloudAuthenticationLauncher
    {
        public int Checks { get; private set; }
        public void LaunchAuthentication(CloudProviderType provider) => throw new NotSupportedException();
        public void LaunchDisconnection(CloudProviderType provider) => throw new NotSupportedException();
        public Task<bool> IsConnectedAsync(CloudProviderType provider, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task DisconnectAsync(CloudProviderType provider, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<CloudAccountVerification> VerifyAccountAsync(LiveScanAccountRecord account, CancellationToken cancellationToken = default)
        {
            Checks++;
            if (outcome == "denied") throw new InvalidOperationException("Read permissions denied.");
            return Task.FromResult(new CloudAccountVerification(account.Provider, outcome == "mismatch" ? "other-account" : account.AccountId,
                "fixture", "", DateTime.UtcNow));
        }
    }

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
            Assert.Single(viewModel.ConnectorHealth);
            viewModel.SelectedOperationalEvent = Assert.Single(viewModel.DriftEvents);
            Assert.Equal(canEdit, viewModel.CanRequestOperationalTicket);
            viewModel.TicketChannel = "jira";
            await viewModel.RequestOperationalTicketAsync();
            Assert.Equal(canEdit ? 1 : 0, monitor.TicketRequests);
            viewModel.NotificationConnectorKind = "Jira";
            viewModel.AllowAutomaticTickets = true;
            viewModel.OperationalTypes = "review.overdue, scan.failed";
            await viewModel.SaveNotificationRuleAsync();
            Assert.Equal(0, monitor.Saves);
            if (canEdit) Assert.Contains("create/configure", viewModel.MonitoringStatus);
            viewModel.NotificationEndpointConfirmed = true;
            await viewModel.SaveNotificationRuleAsync();
            Assert.Equal(canEdit ? 1 : 0, monitor.Saves);
            if (canEdit)
            {
                Assert.Equal("Jira", monitor.LastRule!.ConnectorKind);
                Assert.True(monitor.LastRule.AllowAutomaticTickets);
                Assert.Equal(new[] { "review.overdue", "scan.failed" }, monitor.LastRule.EventTypes);
            }

            foreach (var kind in new[] { SecurityConnectorCatalog.Splunk, SecurityConnectorCatalog.QRadar, SecurityConnectorCatalog.Sentinel })
            {
                Assert.Contains(kind, viewModel.ConnectorKinds);
                viewModel.NotificationConnectorKind = kind;
                viewModel.NotificationChannel = "client-security";
                viewModel.AllowAutomaticTickets = false;
                Assert.Contains("Please create", viewModel.NotificationEndpointInstructions);
                viewModel.NotificationEndpointConfirmed = true;
                await viewModel.SaveNotificationRuleAsync();
                if (canEdit)
                {
                    Assert.Equal(kind, monitor.LastRule!.ConnectorKind);
                    Assert.Equal("client-security", monitor.LastRule.Channel);
                    viewModel.SelectedNotificationRule = monitor.LastRule;
                    Assert.Equal(kind, viewModel.NotificationConnectorKind);
                }
            }
            Assert.Equal(canEdit ? 4 : 0, monitor.Saves);
            Assert.DoesNotContain("CarbonBlackCloud", viewModel.ConnectorKinds);

            monitor.PendingRefresh = new TaskCompletionSource<IReadOnlyList<SharedNotificationRuleDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var inFlight = viewModel.RefreshMonitoringAsync();
            await viewModel.SignOutAsync();
            monitor.PendingRefresh.SetResult(Array.Empty<SharedNotificationRuleDto>());
            await inFlight;
            Assert.Empty(viewModel.NotificationDeliveries);
            Assert.Empty(viewModel.DeliveryAttempts);
            Assert.Empty(viewModel.ConnectorHealth);
            Assert.Null(viewModel.SelectedOperationalEvent);
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

    [Fact]
    public async Task Activity_IsExplicitlyConsentedSavedAndNeverReadOnSignInOrSavedLoad()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"yask-activity-ui-{Guid.NewGuid():N}");
        var tenant = Guid.NewGuid().ToString();
        var identity = new OrganizationIdentity(Guid.NewGuid(), "owner", tenant, "owner@example.invalid");
        var service = new OrganizationManagementService(new LocalOrganizationStore(Path.Combine(directory, "organizations.json")));
        var organization = service.CreateOrganization("Activity", "activity", tenant, identity);
        service.BindCloudAccount(organization.Id, identity, CloudProvider.Aws, Guid.NewGuid(), "123456789012", "AWS");
        try
        {
            var api = new TestMonitoringApi();
            var collector = new TestActivityCollector();
            var viewModel = new OrganizationViewModel(new TestDemoDataService(), service, new TestSsoService(identity),
                new TestLiveCloudScanService(CreateResult(Guid.NewGuid(), Guid.NewGuid())), new AuthenticatedOrganizationContext(),
                sharedMonitoringApi: api, activityCollector: collector);
            await viewModel.SignInAsync();
            Assert.Equal(0, collector.Reads);
            Assert.Equal(0, api.ActivityHistoryReads);
            viewModel.SelectedActivityBinding = Assert.Single(viewModel.Bindings);
            viewModel.ActivityRegion = "us-east-1";
            await viewModel.CollectCloudActivityAsync();
            Assert.Equal(0, collector.Reads);
            Assert.Contains("authorize", viewModel.ActivityStatus);
            viewModel.ActivityReadConsent = true;
            await viewModel.CollectCloudActivityAsync();
            Assert.Equal(1, collector.Reads);
            Assert.Equal(1, api.ActivityUploads);
            Assert.False(viewModel.ActivityReadConsent);
            Assert.Equal(1, Assert.Single(viewModel.ActivityActions).Successful30Days);
            await viewModel.LoadCloudActivityAsync();
            Assert.Equal(1, collector.Reads);
            Assert.Contains("No live cloud call", viewModel.ActivityStatus);
            await viewModel.LoadCloudActivityHistoryAsync();
            viewModel.SelectedActivitySnapshot = Assert.Single(viewModel.ActivityHistory);
            await viewModel.LoadCloudActivityAsync();
            Assert.Equal(collector.LastSnapshot!.SnapshotId, api.LastHistoricalSnapshotId);
            Assert.Equal(1, collector.Reads);
            viewModel.ActivityReadConsent = true;
            viewModel.ActivityRegion = "us-west-2";
            Assert.False(viewModel.ActivityReadConsent);
            Assert.Contains(CloudActivitySource.AwsCloudTrailLake, viewModel.ActivitySources);
            Assert.Contains(CloudActivitySource.AwsS3Archive, viewModel.ActivitySources);
            viewModel.ActivityReadConsent = true;
            viewModel.ActivitySourceKind = CloudActivitySource.AwsCloudTrailLake;
            Assert.False(viewModel.ActivityReadConsent);
            Assert.Contains("BILLABLE", viewModel.ActivitySetupInstructions);
            viewModel.ActivityReadConsent = true;
            viewModel.ActivityDestination = Guid.NewGuid().ToString();
            Assert.False(viewModel.ActivityReadConsent);
            await viewModel.CollectCloudActivityAsync();
            Assert.Equal(1, collector.Reads);
            viewModel.ActivityReadConsent = true;
            await viewModel.CollectCloudActivityAsync();
            Assert.Equal(CloudActivitySource.AwsCloudTrailLake, collector.LastQuery!.SourceKind);
            Assert.Equal(viewModel.ActivityDestination, collector.LastQuery.Destination);
            Assert.Equal(2, collector.Reads);
            Assert.Contains("no matching non-demo", viewModel.ActivityAttributionStatus);
            await viewModel.LoadCloudActivityHistoryAsync();
            Assert.True(viewModel.HasOlderActivityHistory);
            await viewModel.LoadCloudActivityHistoryAsync(true);
            Assert.Equal(2, viewModel.ActivityHistory.Count);
            Assert.False(viewModel.HasOlderActivityHistory);
            api.PendingHistory = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var pendingHistory = viewModel.LoadCloudActivityHistoryAsync();
            viewModel.ActivityPrefix = "changed-history-context";
            api.PendingHistory.SetResult(new CloudActivityHistoryPage(
                [new(Guid.NewGuid(), DateTimeOffset.UtcNow, "Stale response", null, false, 0, Guid.Empty)], null));
            await pendingHistory;
            Assert.Empty(viewModel.ActivityHistory);
            api.PendingHistory = new(TaskCreationOptions.RunContinuationsAsynchronously);
            api.PendingHistory.SetException(new HttpRequestException("History endpoint unavailable."));
            await viewModel.LoadCloudActivityHistoryAsync();
            Assert.Contains("Saved activity history read failed", viewModel.ActivityStatus);
            Assert.Empty(viewModel.ActivityHistory);
            api.PendingHistory = null;
            collector.PendingRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
            viewModel.ActivityReadConsent = true;
            var pending = viewModel.CollectCloudActivityAsync();
            viewModel.ActivityDestination = "changed-destination";
            collector.PendingRead.SetResult(collector.LastSnapshot!);
            await pending;
            Assert.Equal(2, api.ActivityUploads);
            Assert.Empty(viewModel.ActivityActions);
            await viewModel.SignOutAsync();
            Assert.Empty(viewModel.ActivityActions);
            Assert.Empty(viewModel.ActivityCorrelations);
            Assert.Empty(viewModel.ActivityProvenance);
            Assert.Empty(viewModel.ActivityHistory);
            Assert.Null(viewModel.SelectedActivitySnapshot);
            Assert.Null(viewModel.SelectedActivityBinding);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class TestActivityCollector : ICloudActivityCollector
    {
        public int Reads { get; private set; }
        public CloudActivityQuery? LastQuery { get; private set; }
        public TaskCompletionSource<CloudActivitySnapshot>? PendingRead { get; set; }
        public CloudActivitySnapshot? LastSnapshot { get; private set; }
        public Task<CloudActivitySnapshot> CollectAsync(CloudActivityQuery query, CancellationToken cancellationToken = default)
        {
            Reads++;
            LastQuery = query;
            LastSnapshot = new CloudActivitySnapshot(Guid.NewGuid(), query.Provider, query.AccountId,
                query.ThroughUtc.AddDays(-90), query.ThroughUtc, "CloudTrail regional event history", "Partial management only", false,
                new[] { new ProviderActivityEvent("event", "reader", "GetObject", null, null, query.ThroughUtc.AddDays(-1), true) });
            return PendingRead?.Task ?? Task.FromResult(LastSnapshot);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Administration_RequiresOptInExactPreviewReviewAndConfirmationThenResetsOnSignOut(bool confirmation)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"yask-admin-ui-{Guid.NewGuid():N}");
        var identity = new OrganizationIdentity(Guid.NewGuid(), "owner", Guid.NewGuid().ToString(), "owner@example.invalid");
        var organizations = new OrganizationManagementService(new LocalOrganizationStore(Path.Combine(directory, "organizations.json")));
        var organization = organizations.CreateOrganization("Administration", "administration", identity.TenantId, identity);
        organizations.BindCloudAccount(organization.Id, identity, CloudProvider.Aws, Guid.NewGuid(), "123456789012", "AWS");
        try
        {
            var admin = new TestAdministration(organization.Id, identity.UserId);
            var dialogs = new AdministrationDialogs(confirmation);
            var vm = new OrganizationViewModel(new TestDemoDataService(), organizations, new TestSsoService(identity),
                new TestLiveCloudScanService(CreateResult(Guid.NewGuid(), Guid.NewGuid())), new AuthenticatedOrganizationContext(),
                administration: admin, administrationDialogs: dialogs);
            await vm.SignInAsync();
            vm.SelectedAdministrationBinding = Assert.Single(vm.Bindings);
            Assert.False(vm.AdministratorWriteMode);
            await vm.PreviewAdministrationAsync();
            Assert.Equal(0, admin.Previews);
            vm.AdministratorWriteMode = true;
            vm.AdminReason = "Approved maintenance";
            await vm.PreviewAdministrationAsync();
            Assert.NotNull(vm.SelectedAdministrationPlan);
            vm.AdminRoleOrPolicy = "changed-policy";
            Assert.Null(vm.SelectedAdministrationPlan);
            await vm.PreviewAdministrationAsync();
            vm.AdminApplyCommand.Execute(null);
            Assert.Equal(0, dialogs.Confirmations);
            Assert.Equal(0, admin.Writes);
            vm.DependenciesReviewed = true;
            vm.AdminApplyCommand.Execute(null);
            Assert.Equal(1, dialogs.Confirmations);
            Assert.Equal(confirmation ? 1 : 0, admin.Writes);
            if (confirmation)
            {
                Assert.Equal("Applied", vm.SelectedAdministrationPlan!.Status);
                admin.Fail = true;
                vm.DependenciesReviewed = true;
                vm.AdminRollbackCommand.Execute(null);
                Assert.Equal("RollbackUnknown", vm.SelectedAdministrationPlan!.Status);
                Assert.Equal("RollbackUnknown", Assert.Single(vm.AdministrationPlans,
                    item => item.Id == vm.SelectedAdministrationPlan.Id).Status);
                vm.AdminReconcileCommand.Execute(null);
                Assert.Equal("Applied", vm.SelectedAdministrationPlan!.Status);
                Assert.Equal(2, dialogs.Confirmations);
            }
            await vm.SignOutAsync();
            Assert.False(vm.AdministratorWriteMode);
            Assert.Empty(vm.AdministrationPlans);
            Assert.Null(vm.SelectedAdministrationPlan);
            Assert.False(vm.DependenciesReviewed);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Administration_DiscardsPreviewWhenInputsChangeDuringRead()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"yask-admin-preview-ui-{Guid.NewGuid():N}");
        var identity = new OrganizationIdentity(Guid.NewGuid(), "owner", Guid.NewGuid().ToString(), "owner@example.invalid");
        var organizations = new OrganizationManagementService(new LocalOrganizationStore(Path.Combine(directory, "organizations.json")));
        var organization = organizations.CreateOrganization("Administration", "administration", identity.TenantId, identity);
        organizations.BindCloudAccount(organization.Id, identity, CloudProvider.Aws, Guid.NewGuid(), "123456789012", "AWS");
        try
        {
            var admin = new TestAdministration(organization.Id, identity.UserId);
            var vm = new OrganizationViewModel(new TestDemoDataService(), organizations, new TestSsoService(identity),
                new TestLiveCloudScanService(CreateResult(Guid.NewGuid(), Guid.NewGuid())), new AuthenticatedOrganizationContext(),
                administration: admin);
            await vm.SignInAsync();
            vm.SelectedAdministrationBinding = Assert.Single(vm.Bindings);
            vm.AdministratorWriteMode = true;
            await vm.PreviewAdministrationAsync();
            var old = vm.SelectedAdministrationPlan!;
            admin.PendingPreview = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var reading = vm.PreviewAdministrationAsync();
            vm.AdminReason = "Different review";
            admin.PendingPreview.SetResult(old);
            await reading;
            Assert.Null(vm.SelectedAdministrationPlan);
            Assert.False(vm.DependenciesReviewed);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private sealed class TestAdministration(Guid organization, Guid actor) : ICloudAdministrationService
    {
        private CloudAdministrationPlan? _plan;
        public int Previews { get; private set; }
        public int Writes { get; private set; }
        public bool Fail { get; set; }
        public TaskCompletionSource<CloudAdministrationPlan>? PendingPreview { get; set; }
        public Task<CloudAdministrationPlan> PreviewAsync(CloudAdministrationRequest request, CancellationToken token = default)
        {
            Previews++;
            _plan = new(Guid.NewGuid(), organization, actor, request,
                new("Aws", request.AccountId, "operator", "", DateTime.UtcNow), "{}", "{\"attached\":true}", DateTimeOffset.UtcNow, "Prepared");
            return PendingPreview?.Task ?? Task.FromResult(_plan);
        }
        public Task<CloudAdministrationPlan> ApplyAsync(Guid planId, bool dependenciesReviewed, CancellationToken token = default)
        {
            Assert.True(dependenciesReviewed);
            Assert.Equal(_plan!.Id, planId);
            Writes++;
            return Task.FromResult(_plan = _plan with { Status = "Applied" });
        }
        public Task<CloudAdministrationPlan> RollbackAsync(Guid planId, bool dependenciesReviewed, CancellationToken token = default)
        {
            Assert.True(dependenciesReviewed);
            if (Fail)
            {
                _plan = _plan! with { Status = "RollbackUnknown" };
                throw new CloudAdministrationException(_plan, new InvalidOperationException("Provider timeout"));
            }
            return Task.FromResult(_plan! with { Status = "RolledBack" });
        }
        public Task<CloudAdministrationPlan> ReconcileAsync(Guid planId, CancellationToken token = default)
            => Task.FromResult(_plan = _plan! with { Status = "Applied" });
    }

    private sealed class AdministrationDialogs(bool confirmation) : IDialogService
    {
        public int Confirmations { get; private set; }
        public bool Confirm(string message, string title = "YASK-CIEM")
        {
            Assert.Contains("REAL cloud write", message);
            Confirmations++;
            return confirmation;
        }
        public void ShowInformation(string message, string title = "YASK-CIEM") { }
        public void ShowError(string message, string title = "YASK-CIEM") { }
        public string? ChooseReportPath() => null;
        public string? ChoosePolicyDraftPath() => null;
        public string? ChooseComplianceBundlePath() => null;
        public string? ChooseLocalBackupSavePath() => null;
        public string? ChooseLocalBackupOpenPath() => null;
    }

    private sealed partial class TestMonitoringApi : ISharedMonitoringApi, ISharedOperationalApi, ISharedCloudActivityApi, ISharedSecurityAlertApi, ISharedCloudActivityHistoryApi, ISharedEntitlementEvidenceApi, ISharedPriorityTwoApi, ISharedBreakGlassApi
    {
        public int EmergencyWrites { get; private set; }
        public TaskCompletionSource<IReadOnlyList<BreakGlassReport>>? PendingEmergencyReports { get; set; }
        private BreakGlassReport? _emergencyReport;
        public Task<IReadOnlyList<BreakGlassReport>> GetBreakGlassReportsAsync(Guid organization, CancellationToken token = default)
            => PendingEmergencyReports?.Task ?? Task.FromResult<IReadOnlyList<BreakGlassReport>>(_emergencyReport is null ? [] : [_emergencyReport]);
        public Task<BreakGlassReport> ReportBreakGlassAsync(Guid organization, CreateBreakGlassReportRequest request, CancellationToken token = default)
        {
            EmergencyWrites++;
            _emergencyReport = new(request.ReportId, request.Provider, request.AccountId, request.ExternalPrincipal, request.StartedAtUtc,
                request.EndedAtUtc, request.Reason, request.ClientIncidentReference, JitRequesterId, DateTimeOffset.UtcNow,
                request.ActivitySnapshotId, [], "ClientAttestationOnly");
            return Task.FromResult(_emergencyReport);
        }
        public Task<BreakGlassReport> ReviewBreakGlassAsync(Guid organization, Guid reportId, ReviewBreakGlassReportRequest request, CancellationToken token = default)
        {
            EmergencyWrites++;
            return Task.FromResult(_emergencyReport! with { Review = new(Guid.NewGuid(), DateTimeOffset.UtcNow, request.Reason, request.ClientClosureReference, request.ClientVerifiedClosure) });
        }
        public int JitWrites { get; private set; }
        private PrivilegedAccessRequest? _jitRequest;
        public TaskCompletionSource<IReadOnlyList<PrivilegedAccessRequest>>? PendingJit { get; set; }
        public Task<IReadOnlyList<PrivilegedAccessRequest>> GetPrivilegedAccessAsync(Guid organization, CancellationToken token = default)
            => PendingJit?.Task ?? Task.FromResult<IReadOnlyList<PrivilegedAccessRequest>>(_jitRequest is null ? [] : [_jitRequest]);
        public Task<PrivilegedAccessRequest> RequestPrivilegedAccessAsync(Guid organization, CreatePrivilegedAccessRequest request, CancellationToken token = default)
        {
            JitWrites++;
            var now = DateTimeOffset.UtcNow;
            _jitRequest = new(Guid.NewGuid(), JitRequesterId, request.Target, "", "", "", request.DurationMinutes, request.Emergency,
                request.Reason, now, now.AddMinutes(30), "Approved", 2, [Guid.NewGuid(), Guid.NewGuid()]);
            return Task.FromResult(_jitRequest);
        }
        public Guid JitRequesterId { get; set; }
        public Task<PrivilegedAccessRequest> DecidePrivilegedAccessAsync(Guid organization, Guid requestId, string action, PrivilegedAccessDecision request, CancellationToken token = default)
        {
            JitWrites++;
            Assert.True(request.LiveConsent);
            return Task.FromResult(_jitRequest! with { Status = "Queued", Version = 3 });
        }
        public Task<IReadOnlyList<PrivilegedAccessAudit>> GetPrivilegedAccessAuditAsync(Guid organization, Guid requestId, CancellationToken token = default)
            => Task.FromResult<IReadOnlyList<PrivilegedAccessAudit>>([]);
        public Task<SecurityAnalysisReport> AnalyzeSecurityEvidenceAsync(Guid organization, SecurityAnalysisRequest request, CancellationToken token = default)
            => Task.FromResult(new SecurityAnalysisReport([], [], ["Partial saved evidence"]));
        private EntitlementEvidenceSnapshot? _entitlement;
        public int EntitlementUploads { get; private set; }
        public Task<EntitlementEvidenceSnapshot> UploadEntitlementEvidenceAsync(Guid organizationId, EntitlementEvidenceSnapshot snapshot, CancellationToken token = default)
        {
            EntitlementUploads++;
            _entitlement = snapshot;
            return Task.FromResult(snapshot);
        }
        public Task<IReadOnlyList<EntitlementEvidenceHistoryEntry>> GetEntitlementEvidenceHistoryAsync(Guid organizationId, CloudProvider provider, string account, CancellationToken token = default)
            => Task.FromResult<IReadOnlyList<EntitlementEvidenceHistoryEntry>>(_entitlement is null ? [] :
                [new(_entitlement.SnapshotId, _entitlement.StartedAtUtc, _entitlement.CollectedAtUtc, _entitlement.Grants.Count, Guid.Empty)]);
        public Task<EntitlementEvidenceSnapshot> GetEntitlementEvidenceAsync(Guid organizationId, CloudProvider provider, string account, Guid snapshotId, CancellationToken token = default)
            => Task.FromResult(_entitlement ?? throw new InvalidOperationException("No saved entitlement documents."));
        public int AlertReads { get; private set; }
        public (Guid Organization, string Product, string Channel) LastAlertScope { get; private set; }
        public TaskCompletionSource<IReadOnlyList<StoredSecurityAlert>>? PendingAlerts { get; set; }
        public IReadOnlyList<StoredSecurityAlert> SavedAlerts { get; } =
            [new("Sentinel", "client-security", Guid.NewGuid(), DateTimeOffset.UtcNow,
                new("incident-1", DateTimeOffset.UtcNow, "Review", "High", "Open", null, null, null))];
        public Task<IReadOnlyList<StoredSecurityAlert>> GetSecurityAlertsAsync(Guid organizationId, string product, string channel, CancellationToken token = default)
        {
            AlertReads++;
            LastAlertScope = (organizationId, product, channel);
            return PendingAlerts?.Task ?? Task.FromResult(SavedAlerts);
        }
        private CloudActivitySnapshot? _activity;
        private readonly List<CloudActivitySnapshot> _savedActivity = new();
        public int ActivityHistoryReads { get; private set; }
        public Guid? LastHistoricalSnapshotId { get; private set; }
        public TaskCompletionSource<CloudActivityHistoryPage>? PendingHistory { get; set; }
        public Task<CloudActivityHistoryPage> GetCloudActivityHistoryAsync(Guid organizationId, CloudProvider provider,
            string accountId, Guid? beforeSnapshotId, CancellationToken token = default)
        {
            ActivityHistoryReads++;
            if (PendingHistory is not null) return PendingHistory.Task;
            var index = beforeSnapshotId is null ? _savedActivity.Count - 1 : _savedActivity.FindIndex(item => item.SnapshotId == beforeSnapshotId) - 1;
            if (index < 0) return Task.FromResult(new CloudActivityHistoryPage([], null));
            var item = _savedActivity[index];
            return Task.FromResult(new CloudActivityHistoryPage(
                [new(item.SnapshotId, item.ThroughUtc, item.Source, item.SourceScope, item.Truncated, item.Events.Count, Guid.Empty)],
                index > 0 ? item.SnapshotId : null));
        }
        public Task<CloudActivitySnapshot> GetCloudActivitySnapshotAsync(Guid organizationId, CloudProvider provider,
            string accountId, Guid snapshotId, CancellationToken token = default)
        {
            LastHistoricalSnapshotId = snapshotId;
            return Task.FromResult(_savedActivity.Single(item => item.SnapshotId == snapshotId));
        }
        public int ActivityUploads { get; private set; }
        public Task<CloudActivitySnapshot> UploadCloudActivityAsync(Guid organizationId, CloudActivitySnapshot snapshot, CancellationToken cancellationToken = default)
        {
            _activity = snapshot;
            _savedActivity.Add(snapshot);
            ActivityUploads++;
            return Task.FromResult(snapshot);
        }
        public Task<CloudActivitySnapshot> GetCloudActivityAsync(Guid organizationId, CloudProvider provider, string accountId, CancellationToken cancellationToken = default)
            => Task.FromResult(_activity ?? throw new InvalidOperationException("No saved activity."));
        public int TicketRequests { get; private set; }
        public SharedNotificationRuleDto? LastRule { get; private set; }
        public Task<IReadOnlyList<ConnectorHealthDto>> GetConnectorHealthAsync(Guid organizationId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ConnectorHealthDto>>(new[]
                { new ConnectorHealthDto("jira", "Jira", "Configured", "Degraded", 0, 0, 0, 1, 0, DateTimeOffset.UtcNow, null, 25) });
        public Task<SharedDeliveryStatusDto> RequestOperationalTicketAsync(Guid organizationId, Guid eventId, string channel, CancellationToken cancellationToken = default)
        {
            TicketRequests++;
            return Task.FromResult(new SharedDeliveryStatusDto(_delivery with { Channel = channel, ConnectorKind = "Jira" }, Array.Empty<SharedDeliveryAttemptDto>()));
        }
        private readonly SharedDeliveryDto _delivery = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "security",
            "Failed", 5, DateTime.UtcNow, null, DateTime.UtcNow, "http_rejected");
        public int Saves { get; private set; }
        public TaskCompletionSource<IReadOnlyList<SharedNotificationRuleDto>>? PendingRefresh { get; set; }
        public Task<IReadOnlyList<SharedNotificationRuleDto>> GetNotificationRulesAsync(Guid organizationId, CancellationToken cancellationToken = default)
            => PendingRefresh?.Task ?? Task.FromResult<IReadOnlyList<SharedNotificationRuleDto>>(Array.Empty<SharedNotificationRuleDto>());
        public Task<SharedNotificationRuleDto> SaveNotificationRuleAsync(Guid organizationId, SharedNotificationRuleDto rule, CancellationToken cancellationToken = default)
        {
            Saves++;
            LastRule = rule;
            return Task.FromResult(rule);
        }
        public Task<IReadOnlyList<SharedDriftEventDto>> GetDriftEventsAsync(Guid organizationId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SharedDriftEventDto>>(new[] { new SharedDriftEventDto(Guid.NewGuid(), Guid.Empty, Guid.Empty,
                CloudProvider.Aws, "123456789012", "review", "Changed", "review-item", SharedAlertSeverity.High, null, null, DateTime.UtcNow) });
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
