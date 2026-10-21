using System.IO;
using System.Net.Http;
using System.Net;
using System.Text;
using System.Text.Json;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Domain.Entities;
using CloudEntitle.IdentityGovernance.Models;
using CloudEntitle.Integration.Clients;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.OrganizationManagement.Models;
using CloudEntitle.OrganizationManagement.Services;
using CloudEntitle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CloudEntitle.Desktop.Tests;

public sealed class CentralAuditSyncServiceTests
{
    [Fact]
    public async Task SyncOrganizationAsync_ShouldUploadOnlySelectedOrganizationEventsWithBearerAndStableIds()
    {
        var directory = Path.Combine(Path.GetTempPath(), "YaskCiemCentralAuditSyncTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var options = new DbContextOptionsBuilder<CloudEntitleDbContext>()
            .UseSqlite($"Data Source={Path.Combine(directory, "audit.db")};Pooling=False").Options;
        var factory = new TestDbContextFactory(options);
        var otherOrganizationId = Guid.NewGuid();
        var firstEventId = Guid.NewGuid();
        var secondEventId = Guid.NewGuid();
        var identity = new OrganizationIdentity(Guid.NewGuid(), Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("D"), "owner@example.invalid");
        var organizationId = Guid.NewGuid();
        var session = new AuthenticatedOrganizationContext();
        session.Set(identity, organizationId);
        var handler = new RecordingHandler();
        var service = new CentralAuditSyncService(factory, new CiemApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.example.invalid/") }),
            new TestTokenProvider(), new TestSharedOrganizationApi(identity, organizationId), session);

        try
        {
            await using (var context = factory.CreateDbContext())
            {
                await context.Database.EnsureCreatedAsync();
                context.AuditEvents.AddRange(
                    new AuditEvent { Id = firstEventId, OrganizationId = organizationId, UserId = identity.UserId, EventType = "Review.Started", Description = "first", Timestamp = DateTime.UtcNow, Metadata = new Dictionary<string, string>() },
                    new AuditEvent { Id = secondEventId, OrganizationId = organizationId, UserId = identity.UserId, EventType = "Review.Completed", Description = "second", Timestamp = DateTime.UtcNow.AddSeconds(1), Metadata = new Dictionary<string, string>() },
                    new AuditEvent { Id = Guid.NewGuid(), OrganizationId = otherOrganizationId, UserId = identity.UserId, EventType = "Other.Org", Description = "exclude", Timestamp = DateTime.UtcNow, Metadata = new Dictionary<string, string>() });
                await context.SaveChangesAsync();
            }

            var uploaded = await service.SyncOrganizationAsync(organizationId);

            Assert.Equal(2, uploaded);
            Assert.Equal(2, handler.Bodies.Count);
            Assert.All(handler.AuthorizationValues, value => Assert.Equal("Bearer api-test-token", value));
            Assert.Contains(handler.Bodies, body => body.Contains(firstEventId.ToString(), StringComparison.OrdinalIgnoreCase));
            Assert.Contains(handler.Bodies, body => body.Contains(secondEventId.ToString(), StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(handler.Bodies, body => body.Contains("Other.Org", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class TestTokenProvider : IApiAccessTokenProvider
    {
        public Task<string> GetApiAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("api-test-token");
    }

    private sealed class TestSharedOrganizationApi(OrganizationIdentity identity, Guid organizationId) : ISharedOrganizationApi
    {
        public Task<IReadOnlyList<OrganizationProfile>> GetOrganizationsAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<OrganizationWorkspaceDto> GetWorkspaceAsync(Guid id, CancellationToken cancellationToken = default)
            => id == organizationId
                ? Task.FromResult(new OrganizationWorkspaceDto(new OrganizationProfile(id, "Shared", "shared", OrganizationStatus.Active, DateTime.UtcNow) { EntraTenantId = identity.TenantId },
                    new OrganizationMember(Guid.NewGuid(), id, identity.UserId, identity.Email, OrganizationMemberRole.OrganizationAdmin, true, DateTime.UtcNow),
                    new[] { OrganizationAction.ExportReports }, Array.Empty<OrganizationMember>(), Array.Empty<TenantBinding>(), Array.Empty<OrganizationCustomRole>(), Array.Empty<OrganizationAuditEntry>()))
                : throw new InvalidOperationException();
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
        public Task<SharedScanSummaryDto> UploadScanSummaryAsync(Guid id, UploadSharedScanSummaryRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<SharedScanSummaryDto>> GetScanSummariesAsync(Guid id, int limit = 30, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<SharedCiemRecordDto>> GetScanRecordsAsync(Guid id, Guid scanId, string? kind = null, string? accountId = null, int limit = 500, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<SharedCiemRecordDto>> GetFindingsAsync(Guid id, Guid? scanId = null, int limit = 500, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();
        public List<string?> AuthorizationValues { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            AuthorizationValues.Add(request.Headers.Authorization?.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<CloudEntitleDbContext> options) : IDbContextFactory<CloudEntitleDbContext>
    {
        public CloudEntitleDbContext CreateDbContext() => new(options);
    }
}
