using System.IO;
using System.Net.Http;
using System.Net;
using System.Text;
using System.Text.Json;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Integration.Clients;
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
        var organizations = new OrganizationManagementService();
        var organization = organizations.CreateOrganization("Audit Org", "audit-org", identity.TenantId, identity);
        var organizationId = organization.Id;
        var session = new AuthenticatedOrganizationContext();
        session.Set(identity, organizationId);
        var handler = new RecordingHandler();
        var service = new CentralAuditSyncService(factory, new CiemApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.example.invalid/") }),
            new TestTokenProvider(), organizations, session);

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
