using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using Xunit;

namespace CloudEntitle.Desktop.Tests;

public sealed class TicketingWorkflowTests
{
    [Fact]
    public async Task JiraCreate_ShouldEmbedEvidenceDeduplicateAndSynchronizeStatus()
    {
        var handler = new RecordingHandler(request => request.Method == HttpMethod.Post
            ? JsonResponse("{\"id\":\"1001\",\"key\":\"CIEM-1\"}")
            : JsonResponse("{\"fields\":{\"status\":{\"name\":\"In Progress\"}}}"));
        var settings = new InMemorySettingsStore(new JiraConnectionSettings(
            "https://acme.atlassian.net", SelectedProvider: TicketingProvider.Jira,
            ProviderConfigurations: new Dictionary<TicketingProvider, TicketingProviderConfiguration>
            {
                [TicketingProvider.Jira] = new("api-user@acme.invalid", "CIEM", "Task")
            }));
        var credentials = new InMemoryCredentialStore(new TicketingCredentials("api-user@acme.invalid", "test-token"));
        var requestDirectory = Path.Combine(Path.GetTempPath(), "YaskCiemTicketingTests", Guid.NewGuid().ToString("N"));
        var workflow = new TicketingWorkflowService(settings, credentials,
            new LocalTicketingRequestStore(Path.Combine(requestDirectory, "requests.json")), new HttpClient(handler));
        var recommendation = CreateRecommendation("aws-unused-role-fingerprint");

        try
        {
            Assert.True(workflow.CanSubmit(recommendation));
            var created = await workflow.CreateOrGetAsync(recommendation);
            Assert.False(created.AlreadyExists);
            Assert.Equal("CIEM-1", created.Ticket.ExternalId);
            Assert.Contains("/browse/CIEM-1", created.Ticket.TicketUrl, StringComparison.Ordinal);

            var body = handler.Requests[0].Body;
            Assert.Contains("ticket evidence payload", body, StringComparison.Ordinal);
            Assert.Contains("aws-unused-role-fingerprint", body, StringComparison.Ordinal);
            Assert.Equal("Basic", handler.Requests[0].AuthorizationScheme);
            Assert.False(string.IsNullOrWhiteSpace(handler.Requests[0].IdempotencyKey));

            var duplicate = await workflow.CreateOrGetAsync(recommendation);
            Assert.True(duplicate.AlreadyExists);
            Assert.Single(handler.Requests);
            Assert.Equal(created.Ticket.Id, (await workflow.FindExistingAsync(recommendation))?.Id);

            var refreshed = await workflow.RefreshStatusAsync(created.Ticket);
            Assert.Equal("In Progress", refreshed.Status);
            Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        }
        finally
        {
            if (Directory.Exists(requestDirectory)) Directory.Delete(requestDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task JiraServiceManagement_ShouldUseServiceRequestEndpoint()
    {
        var handler = new RecordingHandler(_ => JsonResponse("{\"issueId\":\"4021\",\"issueKey\":\"HELP-21\",\"_links\":{\"web\":\"https://acme.atlassian.net/servicedesk/customer/portal/1/HELP-21\"}}"));
        var workflow = new TicketingWorkflowService(
            new InMemorySettingsStore(new JiraConnectionSettings("https://acme.atlassian.net", SelectedProvider: TicketingProvider.JiraServiceManagement,
                ProviderUrls: new Dictionary<TicketingProvider, string> { [TicketingProvider.JiraServiceManagement] = "https://acme.atlassian.net" },
                ProviderConfigurations: new Dictionary<TicketingProvider, TicketingProviderConfiguration>
                {
                    [TicketingProvider.JiraServiceManagement] = new("api-user@acme.invalid", "12", "34")
                })),
            new InMemoryCredentialStore(new TicketingCredentials("api-user@acme.invalid", "test-token")),
            new InMemoryRequestStore(), new HttpClient(handler));

        var result = await workflow.CreateOrGetAsync(CreateRecommendation("jsm-fingerprint"));

        Assert.Equal("Created", result.Ticket.Status);
        Assert.Contains("/rest/servicedeskapi/request", handler.Requests[0].Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("https://acme.atlassian.net/servicedesk/customer/portal/1/HELP-21", result.Ticket.TicketUrl);
    }

    [Fact]
    public async Task ServiceNow_ShouldCreateIncidentAndRefreshItsState()
    {
        var handler = new RecordingHandler(request => request.Method == HttpMethod.Post
            ? JsonResponse("{\"result\":{\"sys_id\":\"abc123\",\"number\":\"INC0010001\"}}")
            : JsonResponse("{\"result\":{\"state\":\"2\",\"number\":\"INC0010001\"}}"));
        var workflow = new TicketingWorkflowService(
            new InMemorySettingsStore(new JiraConnectionSettings(string.Empty, SelectedProvider: TicketingProvider.ServiceNow,
                ProviderUrls: new Dictionary<TicketingProvider, string> { [TicketingProvider.ServiceNow] = "https://acme.service-now.com" },
                ProviderConfigurations: new Dictionary<TicketingProvider, TicketingProviderConfiguration>
                {
                    [TicketingProvider.ServiceNow] = new("", "", "software")
                })),
            new InMemoryCredentialStore(new TicketingCredentials("api-user", "test-token")),
            new InMemoryRequestStore(), new HttpClient(handler));

        Assert.True(workflow.CanSubmit(CreateRecommendation("servicenow-fingerprint")));
        var created = await workflow.CreateOrGetAsync(CreateRecommendation("servicenow-fingerprint"));
        var refreshed = await workflow.RefreshStatusAsync(created.Ticket);

        Assert.Equal("abc123", created.Ticket.ExternalId);
        Assert.Equal("2 (INC0010001)", refreshed.Status);
        Assert.Contains("/api/now/table/incident", handler.Requests[0].Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("api-user:test-token", Encoding.UTF8.GetString(Convert.FromBase64String(handler.Requests[0].AuthorizationParameter!)));
        Assert.Contains("ticket evidence payload", handler.Requests[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentCreateRequests_ShouldSubmitOnlyOnce()
    {
        var handler = new RecordingHandler(_ => JsonResponse("{\"key\":\"CIEM-2\"}"));
        var workflow = new TicketingWorkflowService(
            new InMemorySettingsStore(new JiraConnectionSettings("https://acme.atlassian.net", SelectedProvider: TicketingProvider.Jira,
                ProviderConfigurations: new Dictionary<TicketingProvider, TicketingProviderConfiguration>
                {
                    [TicketingProvider.Jira] = new("api-user", "CIEM", "Task")
                })),
            new InMemoryCredentialStore(new TicketingCredentials("api-user", "test-token")),
            new InMemoryRequestStore(), new HttpClient(handler));

        var results = await Task.WhenAll(
            workflow.CreateOrGetAsync(CreateRecommendation("concurrent-fingerprint")),
            workflow.CreateOrGetAsync(CreateRecommendation("concurrent-fingerprint")));

        Assert.Single(handler.Requests);
        Assert.Equal(1, results.Count(result => result.AlreadyExists));
    }

    [Fact]
    public async Task WebhookFallback_ShouldSignRequestAndRejectCrossOriginStatusUrl()
    {
        var handler = new RecordingHandler(_ => JsonResponse("{\"ticketId\":\"INC-1\",\"ticketUrl\":\"https://support.example/tickets/INC-1\",\"statusUrl\":\"https://evil.example/status/INC-1\",\"status\":\"open\"}"));
        var workflow = new TicketingWorkflowService(
            new InMemorySettingsStore(new JiraConnectionSettings(string.Empty, SelectedProvider: TicketingProvider.BmcHelixItsm,
                ProviderUrls: new Dictionary<TicketingProvider, string> { [TicketingProvider.BmcHelixItsm] = "https://bmc.example/" },
                ProviderConfigurations: new Dictionary<TicketingProvider, TicketingProviderConfiguration>
                {
                    [TicketingProvider.BmcHelixItsm] = new(WebhookUrl: "https://hooks.example/tickets")
                })),
            new InMemoryCredentialStore(new TicketingCredentials(string.Empty, "webhook-secret")),
            new InMemoryRequestStore(), new HttpClient(handler));

        Assert.True(workflow.CanSubmit(CreateRecommendation("bmc-fingerprint")));
        var created = await workflow.CreateOrGetAsync(CreateRecommendation("bmc-fingerprint"));
        Assert.Equal("INC-1", created.Ticket.ExternalId);
        Assert.StartsWith("sha256=", handler.Requests[0].Signature, StringComparison.Ordinal);
        Assert.Equal("ticket evidence payload", handler.Requests[0].Body.Contains("ticket evidence payload", StringComparison.Ordinal) ? "ticket evidence payload" : string.Empty);

        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.RefreshStatusAsync(created.Ticket));
        Assert.Single(handler.Requests);
    }

    private static RecommendationModel CreateRecommendation(string fingerprint)
        => new()
        {
            Id = Guid.NewGuid(),
            RiskFindingId = Guid.NewGuid(),
            Provider = CloudProvider.Aws,
            CloudAccountId = Guid.NewGuid(),
            RecommendationType = RecommendationType.RemoveUnusedRole,
            Title = "Review unused role",
            Description = "AWS reports no role use in its usage history.",
            CurrentState = "Role has ReadOnlyAccess",
            SuggestedState = "Review removal",
            Reason = "Role-level evidence requires owner validation.",
            Evidence = "ticket evidence payload",
            Fingerprint = fingerprint,
            EvaluationState = RiskEvaluationState.Confirmed,
            ImpactState = RecommendationImpactState.RequiresReview
        };

    private static HttpResponseMessage JsonResponse(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class InMemorySettingsStore(JiraConnectionSettings settings) : IJiraConnectionSettingsStore
    {
        public JiraConnectionSettings Load() => settings;
        public void Save(string baseUrl) { }
        public void Save(JiraConnectionSettings newSettings) => settings = newSettings;
    }

    private sealed class InMemoryCredentialStore(TicketingCredentials credentials) : ITicketingCredentialStore
    {
        public TicketingCredentials? Read(TicketingProvider provider) => credentials;
        public void Save(TicketingProvider provider, TicketingCredentials value) => credentials = value;
        public void Delete(TicketingProvider provider) { }
    }

    private sealed class InMemoryRequestStore : ITicketingRequestStore
    {
        private readonly Dictionary<(TicketingProvider Provider, string Key), TicketingRequestRecord> _records = new();
        public Task<TicketingRequestRecord?> FindAsync(TicketingProvider provider, string dedupeKey, CancellationToken cancellationToken = default)
            => Task.FromResult(_records.GetValueOrDefault((provider, dedupeKey)));
        public Task SaveAsync(TicketingRequestRecord request, CancellationToken cancellationToken = default)
        {
            _records[(request.Provider, request.DedupeKey)] = request;
            return Task.CompletedTask;
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, Uri Uri, string Body, string? AuthorizationScheme, string? AuthorizationParameter, string? IdempotencyKey, string? Signature);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri!, body, request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter,
                request.Headers.TryGetValues("Idempotency-Key", out var key) ? key.Single() : null,
                request.Headers.TryGetValues("X-YASK-CIEM-Signature", out var signature) ? signature.Single() : null));
            return responseFactory(request);
        }
    }
}
