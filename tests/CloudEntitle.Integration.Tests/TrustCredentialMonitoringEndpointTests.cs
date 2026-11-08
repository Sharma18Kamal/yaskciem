using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.LicenseServer.Services;
using CloudEntitle.OrganizationManagement.Models;
using CloudEntitle.OrganizationManagement.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed partial class SharedWorkspaceEndpointTests
{
    [PostgreSqlFact]
    public async Task TrustCredentialEndpointsPersistImmutableEvidenceAndReportPotentialPrivilegeChanges()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = new SharedWorkspaceStore(database.ConnectionString);
        await store.InitializeAsync();
        var tenant = Guid.NewGuid();
        var owner = new OrganizationIdentity(Guid.NewGuid(), "owner", tenant.ToString(), "owner@example.invalid");
        var viewer = new OrganizationIdentity(Guid.NewGuid(), "viewer", tenant.ToString(), "viewer@example.invalid");
        var organizationService = new OrganizationManagementService(store.CreateOrganizationStore(tenant));
        var organization = organizationService.CreateOrganization("Monitoring", "trust-monitoring", tenant.ToString(), owner);
        var subscription = Guid.NewGuid().ToString();
        organizationService.BindCloudAccount(organization.Id, owner, CloudProvider.Azure, Guid.NewGuid(), subscription, "Azure");
        organizationService.AddMember(organization.Id, owner, viewer.Email, OrganizationMemberRole.Viewer);
        organizationService.AcceptInvitation(organization.Id, viewer);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication(TestAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("CiemApiAccess", policy => policy
            .RequireAuthenticatedUser().RequireClaim("tid").RequireClaim("oid")
            .RequireAssertion(context => context.User.FindFirst("scp")?.Value.Split(' ').Contains("access_as_user") == true)));
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapTrustCredentialMonitoringEndpoints(store);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var prefix = $"/api/v1/organizations/{organization.Id:D}";
        async Task<HttpResponseMessage> Send(HttpMethod method, string path, OrganizationIdentity identity, object? body = null)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = JsonContent.Create(body);
            SetIdentityHeaders(request, Guid.Parse(identity.TenantId), identity.UserId, identity.Email);
            return await client.SendAsync(request);
        }

        using var deniedUpload = await Send(HttpMethod.Post, $"{prefix}/trust-credential-evidence",
            viewer, MakeTrustSnapshot(subscription, DateTimeOffset.UtcNow.AddMinutes(-2), "credential"));
        Assert.Equal(HttpStatusCode.Forbidden, deniedUpload.StatusCode);

        var now = DateTimeOffset.UtcNow;
        var baseline = MakeTrustSnapshot(subscription, now.AddMinutes(-4), "credential");
        var current = MakeTrustSnapshot(subscription, now.AddMinutes(-2), "credential", "new-trust");
        using var baselineUpload = await Send(HttpMethod.Post, $"{prefix}/trust-credential-evidence", owner, baseline);
        using var currentUpload = await Send(HttpMethod.Post, $"{prefix}/trust-credential-evidence", owner, current);
        Assert.Equal(HttpStatusCode.Created, baselineUpload.StatusCode);
        Assert.Equal(HttpStatusCode.Created, currentUpload.StatusCode);
        using var historyResponse = await Send(HttpMethod.Get,
            $"{prefix}/trust-credential-evidence?accountId={Uri.EscapeDataString(subscription)}", viewer);
        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        var history = (await historyResponse.Content.ReadFromJsonAsync<ApiEnvelope<IReadOnlyList<TrustCredentialSnapshotSummary>>>())!.Data;
        Assert.Equal(2, history.Count);

        var beforeCompleted = now.AddMinutes(-5);
        var afterCompleted = now.AddMinutes(-1);
        var baseScan = CreateScan(owner, organization.Id, tenant, subscription, beforeCompleted.UtcDateTime, false);
        var currentScan = CreateScan(owner, organization.Id, tenant, subscription, afterCompleted.UtcDateTime, true);
        await store.SaveCiemScanAsync(baseScan.Summary, baseScan.Records, Guid.NewGuid().ToString("N"));
        await store.SaveCiemScanAsync(currentScan.Summary, currentScan.Records, Guid.NewGuid().ToString("N"));
        var request = new TrustCredentialMonitoringRequest(CloudProvider.Azure, subscription, current.SnapshotId,
            baseScan.Summary.ScanId, currentScan.Summary.ScanId);
        using var analysisResponse = await Send(HttpMethod.Post,
            $"{prefix}/trust-credential-monitoring/analyze", owner, request);
        Assert.Equal(HttpStatusCode.OK, analysisResponse.StatusCode);
        var report = (await analysisResponse.Content.ReadFromJsonAsync<ApiEnvelope<TrustCredentialMonitoringReport>>())!.Data;
        Assert.Contains(report.TrustCredentialChanges, item => item.Change == "Added" && item.Kind == "FederatedIdentityCredential");
        var escalation = Assert.Single(report.PrivilegeEscalations);
        Assert.Equal("Potential", escalation.Confidence);
        Assert.Contains("not authoritative comparable", escalation.Evidence, StringComparison.OrdinalIgnoreCase);

        var pointTime = new DateTimeOffset(now.UtcTicks / 10 * 10, TimeSpan.Zero).AddMinutes(-1).AddTicks(7);
        var equalTime = MakeTrustSnapshot(subscription, pointTime.AddSeconds(-15), "equal-time-only");
        var point = MakeTrustSnapshot(subscription, pointTime, "credential", "new-trust") with { CollectedAtUtc = pointTime };
        using var equalTimeUpload = await Send(HttpMethod.Post, $"{prefix}/trust-credential-evidence", owner, equalTime);
        using var pointUpload = await Send(HttpMethod.Post, $"{prefix}/trust-credential-evidence", owner, point);
        Assert.Equal(HttpStatusCode.Created, equalTimeUpload.StatusCode);
        Assert.Equal(HttpStatusCode.Created, pointUpload.StatusCode);
        using var pointAnalysis = await Send(HttpMethod.Post, $"{prefix}/trust-credential-monitoring/analyze", owner,
            new TrustCredentialMonitoringRequest(CloudProvider.Azure, subscription, point.SnapshotId, null, null));
        Assert.Equal(HttpStatusCode.OK, pointAnalysis.StatusCode);
        var pointReport = (await pointAnalysis.Content.ReadFromJsonAsync<ApiEnvelope<TrustCredentialMonitoringReport>>())!.Data;
        Assert.Empty(pointReport.TrustCredentialChanges);

        using var unbound = await Send(HttpMethod.Get,
            $"{prefix}/trust-credential-evidence?accountId={Guid.NewGuid():D}", owner);
        Assert.Equal(HttpStatusCode.Forbidden, unbound.StatusCode);

        foreach (var (provider, account, source, kind) in new[]
                 {
                     (CloudProvider.Aws, "123456789012", "AWS IAM role trust policies and credential report metadata", "IamCredentialReport"),
                     (CloudProvider.Gcp, "sample-project-123", "GCP project service-account keys and IAM impersonation policies", "GcpUserManagedServiceAccountKey")
                 })
        {
            organizationService.BindCloudAccount(organization.Id, owner, provider, Guid.NewGuid(), account, provider.ToString());
            var multiCloud = new TrustCredentialSnapshot(Guid.NewGuid(), provider, account,
                now.AddMinutes(-1), now.AddSeconds(-30), source,
                [new("principal", "workload", kind, "credential", "metadata", null, null, [], null, null, new string('a', 64))],
                ["Partial bounded metadata coverage."])
            {
                CredentialInventoryComplete = true,
                TrustInventoryComplete = true
            };
            using var upload = await Send(HttpMethod.Post, $"{prefix}/trust-credential-evidence", owner, multiCloud);
            Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
            var location = Assert.IsType<Uri>(upload.Headers.Location);
            using var savedRead = await Send(HttpMethod.Get, location.ToString(), viewer);
            Assert.Equal(HttpStatusCode.OK, savedRead.StatusCode);
            var read = (await savedRead.Content.ReadFromJsonAsync<ApiEnvelope<TrustCredentialSnapshot>>())!.Data;
            Assert.Equal(multiCloud.SnapshotId, read.SnapshotId);
            Assert.Equal(provider, read.Provider);
            Assert.Equal(account, read.AccountId);
            Assert.Equal(source, read.Source);
            Assert.Equal(kind, Assert.Single(read.Observations).Kind);
        }
        await app.StopAsync();
    }

    private static TrustCredentialSnapshot MakeTrustSnapshot(string subscription, DateTimeOffset started,
        params string[] evidenceIds)
    {
        var end = started.AddSeconds(15);
        var observations = evidenceIds.Select(id => new TrustCredentialObservation("app-id", "application",
            "FederatedIdentityCredential", id, id, "issuer", "subject", ["audience"], null, null,
            new string('a', 64))).ToArray();
        return new(Guid.NewGuid(), CloudProvider.Azure, subscription, started, end,
            "Azure CLI / Microsoft Graph Entra application credentials and federation", observations,
            ["Partial source coverage."])
        {
            CredentialInventoryComplete = true,
            TrustInventoryComplete = true
        };
    }

    private static (SharedScanSummaryDto Summary, IReadOnlyList<SharedCiemRecordDto> Records) CreateScan(
        OrganizationIdentity owner, Guid organizationId, Guid tenant, string subscription, DateTime completed, bool includePrivileged)
    {
        var scanId = Guid.NewGuid();
        var records = includePrivileged
            ? new[]
            {
                new SharedCiemRecordDto(CloudProvider.Azure, "effective-access", "assignment", subscription,
                    """{"principal":"app","role":"Owner","scope":"/subscriptions/test","source":"Azure RBAC","isPrivileged":true}""")
            }
            : Array.Empty<SharedCiemRecordDto>();
        var summary = new SharedScanSummaryDto(scanId, organizationId, tenant, owner.UserId, completed.AddSeconds(-1),
            completed, "Azure", [new(CloudProvider.Azure, subscription, "Azure")], 1, 0, 0, 0, records.Length,
            0, 0, 0, [new("Azure", "Effective access", includePrivileged ? "Collected" : "NoData", records.Length, "")])
        {
            MonitoringScopes = [new(CloudProvider.Azure, subscription, "effective-access", "effective-access-v1")]
        };
        return (summary, records);
    }
}
