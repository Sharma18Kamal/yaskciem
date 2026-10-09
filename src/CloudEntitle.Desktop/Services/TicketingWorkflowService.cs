using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;

namespace CloudEntitle.Desktop.Services;

public sealed record TicketingSubmissionResult(TicketingRequestRecord Ticket, bool AlreadyExists);

public interface ITicketingWorkflowService
{
    TicketingProvider SelectedProvider { get; }
    bool CanSubmit(RecommendationModel? recommendation);
    Task<TicketingRequestRecord?> FindExistingAsync(RecommendationModel recommendation, CancellationToken cancellationToken = default);
    Task<TicketingSubmissionResult> CreateOrGetAsync(RecommendationModel recommendation, CancellationToken cancellationToken = default);
    Task<TicketingRequestRecord> RefreshStatusAsync(TicketingRequestRecord ticket, CancellationToken cancellationToken = default);
}

public sealed class TicketingWorkflowService : ITicketingWorkflowService
{
    private static readonly HttpClient SharedClient = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly IJiraConnectionSettingsStore _settingsStore;
    private readonly ITicketingCredentialStore _credentialStore;
    private readonly ITicketingRequestStore _requestStore;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<(TicketingProvider Provider, string DedupeKey), SemaphoreSlim> _submissionLocks = new();

    public TicketingWorkflowService(
        IJiraConnectionSettingsStore settingsStore,
        ITicketingCredentialStore credentialStore,
        ITicketingRequestStore requestStore,
        HttpClient? httpClient = null)
    {
        _settingsStore = settingsStore;
        _credentialStore = credentialStore;
        _requestStore = requestStore;
        _httpClient = httpClient ?? SharedClient;
    }

    public TicketingProvider SelectedProvider => _settingsStore.Load().SelectedProvider;

    public bool CanSubmit(RecommendationModel? recommendation)
    {
        if (recommendation is null) return false;
        var settings = _settingsStore.Load();
        var provider = settings.SelectedProvider;
        var configuration = GetProviderConfiguration(settings, provider);
        var endpoint = UsesNativeConnector(provider) ? settings.GetBaseUrl(provider) : configuration.WebhookUrl;
        if (!IsSafeHttpsUrl(endpoint)) return false;
        if (provider is TicketingProvider.Jira or TicketingProvider.JiraServiceManagement
            && (string.IsNullOrWhiteSpace(configuration.ProjectKey) || string.IsNullOrWhiteSpace(configuration.RequestType))) return false;
        if (provider == TicketingProvider.ServiceNow && string.IsNullOrWhiteSpace(configuration.RequestType)) return false;
        try
        {
            var credentials = _credentialStore.Read(provider);
            return credentials is { Secret.Length: > 0 }
                && (provider is not (TicketingProvider.Jira or TicketingProvider.JiraServiceManagement or TicketingProvider.ServiceNow)
                    || !string.IsNullOrWhiteSpace(credentials.UserName));
        }
        catch { return false; }
    }

    public Task<TicketingRequestRecord?> FindExistingAsync(RecommendationModel recommendation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recommendation);
        var provider = SelectedProvider;
        return _requestStore.FindAsync(provider, CreateDedupeKey(provider, recommendation), cancellationToken);
    }

    public async Task<TicketingSubmissionResult> CreateOrGetAsync(RecommendationModel recommendation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recommendation);
        var settings = _settingsStore.Load();
        var provider = settings.SelectedProvider;
        var fingerprint = string.IsNullOrWhiteSpace(recommendation.Fingerprint) ? recommendation.Id.ToString("D") : recommendation.Fingerprint;
        var dedupeKey = CreateDedupeKey(provider, recommendation);
        var submissionLock = _submissionLocks.GetOrAdd((provider, dedupeKey), _ => new SemaphoreSlim(1, 1));
        await submissionLock.WaitAsync(cancellationToken);
        try
        {
            await using var processLock = await AcquireSubmissionFileLockAsync(dedupeKey, cancellationToken);
            var existing = await _requestStore.FindAsync(provider, dedupeKey, cancellationToken);
            if (existing is not null) return new TicketingSubmissionResult(existing, AlreadyExists: true);

            var credentials = _credentialStore.Read(provider)
                ?? throw new InvalidOperationException($"Credentials are not configured for {provider}.");
            var configuration = GetProviderConfiguration(settings, provider);
            var requestSummary = recommendation.Title.Trim();
            if (requestSummary.Length > 250) requestSummary = requestSummary[..250];
            var evidencePayload = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                sourceProduct = "YASK-CIEM",
                sourceProvider = recommendation.Provider.ToString(),
                recommendationId = recommendation.Id,
                recommendationFingerprint = fingerprint,
                riskFindingId = recommendation.RiskFindingId,
                effectiveAccessId = recommendation.EffectiveAccessId,
                accessPathId = recommendation.AccessPathId,
                recommendationType = recommendation.RecommendationType.ToString(),
                evaluationState = recommendation.EvaluationState.ToString(),
                currentState = recommendation.CurrentState,
                proposedState = recommendation.SuggestedState,
                impact = recommendation.Impact,
                reason = recommendation.Reason,
                evidence = recommendation.Evidence,
                createdAtUtc = DateTime.UtcNow
            }, JsonOptions);

            var ticket = provider switch
            {
                TicketingProvider.Jira =>
                    await CreateJiraIssueAsync(provider, settings.GetBaseUrl(provider), configuration, credentials, recommendation, requestSummary, evidencePayload, dedupeKey, cancellationToken),
                TicketingProvider.JiraServiceManagement =>
                    await CreateJiraServiceRequestAsync(settings.GetBaseUrl(provider), configuration, credentials, recommendation, requestSummary, evidencePayload, dedupeKey, cancellationToken),
                TicketingProvider.ServiceNow =>
                    await CreateServiceNowIncidentAsync(settings.GetBaseUrl(provider), configuration, credentials, recommendation, requestSummary, evidencePayload, dedupeKey, cancellationToken),
                _ => await CreateWebhookTicketAsync(provider, configuration.WebhookUrl, credentials, recommendation, requestSummary, evidencePayload, dedupeKey, cancellationToken)
            };

            await _requestStore.SaveAsync(ticket, cancellationToken);
            return new TicketingSubmissionResult(ticket, AlreadyExists: false);
        }
        finally
        {
            submissionLock.Release();
        }
    }

    public async Task<TicketingRequestRecord> RefreshStatusAsync(TicketingRequestRecord ticket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var settings = _settingsStore.Load();
        var configuration = GetProviderConfiguration(settings, ticket.Provider);
        var credentials = _credentialStore.Read(ticket.Provider)
            ?? throw new InvalidOperationException($"Credentials are not configured for {ticket.Provider}.");
        var updated = ticket.Provider switch
        {
            TicketingProvider.Jira or TicketingProvider.JiraServiceManagement =>
                await RefreshJiraStatusAsync(ticket, settings.GetBaseUrl(ticket.Provider), configuration, credentials, cancellationToken),
            TicketingProvider.ServiceNow =>
                await RefreshServiceNowStatusAsync(ticket, settings.GetBaseUrl(ticket.Provider), configuration, credentials, cancellationToken),
            _ => await RefreshWebhookStatusAsync(ticket, configuration.WebhookUrl, credentials, cancellationToken)
        };
        await _requestStore.SaveAsync(updated, cancellationToken);
        return updated;
    }

    private async Task<TicketingRequestRecord> CreateJiraIssueAsync(
        TicketingProvider provider, string siteUrl, TicketingProviderConfiguration configuration, TicketingCredentials credentials,
        RecommendationModel recommendation, string summary, string evidence, string dedupeKey, CancellationToken cancellationToken)
    {
        var site = LocalJiraConnectionSettingsStore.NormalizeBaseUrl(siteUrl);
        var descriptionText = $"{recommendation.Description}\n\nCurrent: {recommendation.CurrentState}\nProposed: {recommendation.SuggestedState}\nReason: {recommendation.Reason}\n\nYASK-CIEM evidence: {TicketingEvidenceLink.ForRecommendation(recommendation.Id)}\n\nYASK-CIEM evidence snapshot:\n{evidence}";
        var requestBody = JsonSerializer.Serialize(new
        {
            fields = new
            {
                project = new { key = configuration.ProjectKey },
                issuetype = new { name = configuration.RequestType },
                summary,
                description = new
                {
                    type = "doc",
                    version = 1,
                    content = new[] { new { type = "paragraph", content = new[] { new { type = "text", text = descriptionText } } } }
                },
                labels = new[] { "yask-ciem", $"ciem-{recommendation.Provider.ToString().ToLowerInvariant()}" }
            }
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{site}/rest/api/3/issue");
        request.Headers.Authorization = Basic(credentials.UserName, credentials.Secret);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", dedupeKey);
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var key = ReadString(json.RootElement, "key") ?? throw new InvalidDataException("Jira returned no issue key.");
        return new TicketingRequestRecord(Guid.NewGuid(), provider, dedupeKey, key, $"{site}/browse/{Uri.EscapeDataString(key)}", "Created", $"{site}/rest/api/3/issue/{Uri.EscapeDataString(key)}?fields=status", DateTime.UtcNow, DateTime.UtcNow);
    }

    private async Task<TicketingRequestRecord> CreateServiceNowIncidentAsync(
        string siteUrl, TicketingProviderConfiguration configuration, TicketingCredentials credentials,
        RecommendationModel recommendation, string summary, string evidence, string dedupeKey, CancellationToken cancellationToken)
    {
        var site = LocalJiraConnectionSettingsStore.NormalizeBaseUrl(siteUrl);
        var body = JsonSerializer.Serialize(new
        {
            short_description = summary,
            description = $"{recommendation.Description}\n\nCurrent: {recommendation.CurrentState}\nProposed: {recommendation.SuggestedState}\nReason: {recommendation.Reason}\n\nYASK-CIEM evidence: {TicketingEvidenceLink.ForRecommendation(recommendation.Id)}\n\nYASK-CIEM evidence snapshot:\n{evidence}",
            category = configuration.RequestType
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{site}/api/now/table/incident");
        request.Headers.Authorization = Basic(credentials.UserName, credentials.Secret);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", dedupeKey);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var result = json.RootElement.TryGetProperty("result", out var resultElement) ? resultElement : json.RootElement;
        var sysId = ReadString(result, "sys_id") ?? throw new InvalidDataException("ServiceNow returned no incident sys_id.");
        var number = ReadString(result, "number") ?? sysId;
        var statusUrl = $"{site}/api/now/table/incident/{Uri.EscapeDataString(sysId)}?sysparm_fields=state,number";
        return new TicketingRequestRecord(Guid.NewGuid(), TicketingProvider.ServiceNow, dedupeKey, sysId,
            $"{site}/nav_to.do?uri=incident.do?sys_id={Uri.EscapeDataString(sysId)}", $"Created ({number})", statusUrl, DateTime.UtcNow, DateTime.UtcNow);
    }

    private async Task<TicketingRequestRecord> CreateJiraServiceRequestAsync(
        string siteUrl, TicketingProviderConfiguration configuration, TicketingCredentials credentials,
        RecommendationModel recommendation, string summary, string evidence, string dedupeKey, CancellationToken cancellationToken)
    {
        var site = LocalJiraConnectionSettingsStore.NormalizeBaseUrl(siteUrl);
        var description = $"{recommendation.Description}\n\nCurrent: {recommendation.CurrentState}\nProposed: {recommendation.SuggestedState}\nReason: {recommendation.Reason}\n\nYASK-CIEM evidence: {TicketingEvidenceLink.ForRecommendation(recommendation.Id)}\n\nYASK-CIEM evidence snapshot:\n{evidence}";
        var body = JsonSerializer.Serialize(new
        {
            serviceDeskId = configuration.ProjectKey,
            requestTypeId = configuration.RequestType,
            requestFieldValues = new { summary, description }
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{site}/rest/servicedeskapi/request");
        request.Headers.Authorization = Basic(credentials.UserName, credentials.Secret);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", dedupeKey);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var issueKey = ReadString(json.RootElement, "issueKey") ?? ReadString(json.RootElement, "issueId")
            ?? throw new InvalidDataException("Jira Service Management returned no request identifier.");
        var webUrl = json.RootElement.TryGetProperty("_links", out var links) ? ReadString(links, "web") : null;
        var ticketUrl = ReadHttpsUrl(webUrl) ?? $"{site}/browse/{Uri.EscapeDataString(issueKey)}";
        var statusUrl = $"{site}/rest/api/3/issue/{Uri.EscapeDataString(issueKey)}?fields=status";
        return new TicketingRequestRecord(Guid.NewGuid(), TicketingProvider.JiraServiceManagement, dedupeKey, issueKey,
            ticketUrl, "Created", statusUrl, DateTime.UtcNow, DateTime.UtcNow);
    }

    private async Task<TicketingRequestRecord> CreateWebhookTicketAsync(
        TicketingProvider provider, string webhookUrl, TicketingCredentials credentials, RecommendationModel recommendation,
        string summary, string evidence, string dedupeKey, CancellationToken cancellationToken)
    {
        var endpoint = new Uri(LocalJiraConnectionSettingsStore.NormalizeBaseUrl(webhookUrl));
        var body = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            eventName = "TicketCreateRequested",
            provider = provider.ToString(),
            idempotencyKey = dedupeKey,
            summary,
            evidenceUrl = TicketingEvidenceLink.ForRecommendation(recommendation.Id),
            recommendation = new
            {
                recommendation.Provider,
                type = recommendation.RecommendationType.ToString(),
                recommendation.EvaluationState,
                recommendation.CurrentState,
                recommendation.SuggestedState,
                recommendation.Impact,
                recommendation.Reason,
                evidence
            }
        });
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", dedupeKey);
        request.Headers.TryAddWithoutValidation("X-YASK-CIEM-Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("X-YASK-CIEM-Signature", CreateWebhookSignature(credentials.Secret, "POST", endpoint, timestamp, bodyBytes));
        request.Content = new ByteArrayContent(bodyBytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var responseJson = string.IsNullOrWhiteSpace(responseBody) ? (JsonDocument?)null : JsonDocument.Parse(responseBody);
        using (responseJson)
        {
            var root = responseJson?.RootElement ?? default;
            var externalId = ReadString(root, "ticketId") ?? ReadString(root, "id") ?? dedupeKey;
            var ticketUrl = ReadHttpsUrl(ReadString(root, "ticketUrl") ?? ReadString(root, "url")) ?? endpoint.AbsoluteUri;
            var statusUrl = ReadSameOriginUrl(endpoint, ReadString(root, "statusUrl"));
            var status = ReadString(root, "status") ?? "Submitted";
            return new TicketingRequestRecord(Guid.NewGuid(), provider, dedupeKey, externalId, ticketUrl, status, statusUrl, DateTime.UtcNow, DateTime.UtcNow);
        }
    }

    private async Task<TicketingRequestRecord> RefreshJiraStatusAsync(TicketingRequestRecord ticket, string siteUrl, TicketingProviderConfiguration configuration, TicketingCredentials credentials, CancellationToken cancellationToken)
    {
        var site = LocalJiraConnectionSettingsStore.NormalizeBaseUrl(siteUrl);
        var key = Uri.UnescapeDataString(ticket.TicketUrl[(ticket.TicketUrl.LastIndexOf('/') + 1)..]);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{site}/rest/api/3/issue/{Uri.EscapeDataString(key)}?fields=status");
        request.Headers.Authorization = Basic(credentials.UserName, credentials.Secret);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var statusElement = json.RootElement.TryGetProperty("fields", out var fields) && fields.TryGetProperty("status", out var parsedStatus)
            ? parsedStatus : default;
        var status = ReadString(statusElement, "name");
        var category = statusElement.ValueKind == JsonValueKind.Object && statusElement.TryGetProperty("statusCategory", out var statusCategory)
            ? ReadString(statusCategory, "key") : null;
        return ApplyVerifiedStatus(ticket, status ?? ticket.Status, IsClosedStatus(status, category));
    }

    private async Task<TicketingRequestRecord> RefreshServiceNowStatusAsync(TicketingRequestRecord ticket, string siteUrl, TicketingProviderConfiguration configuration, TicketingCredentials credentials, CancellationToken cancellationToken)
    {
        var site = LocalJiraConnectionSettingsStore.NormalizeBaseUrl(siteUrl);
        var statusUrl = ticket.StatusUrl ?? $"{site}/api/now/table/incident/{Uri.EscapeDataString(ticket.ExternalId)}?sysparm_fields=state,number";
        using var request = new HttpRequestMessage(HttpMethod.Get, statusUrl);
        request.Headers.Authorization = Basic(credentials.UserName, credentials.Secret);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var result = json.RootElement.TryGetProperty("result", out var resultElement) ? resultElement : json.RootElement;
        var status = ReadString(result, "state");
        var number = ReadString(result, "number");
        var displayStatus = number is null ? status ?? ticket.Status : $"{status ?? ticket.Status} ({number})";
        return ApplyVerifiedStatus(ticket, displayStatus, IsServiceNowClosedStatus(status));
    }

    private async Task<TicketingRequestRecord> RefreshWebhookStatusAsync(TicketingRequestRecord ticket, string webhookUrl, TicketingCredentials credentials, CancellationToken cancellationToken)
    {
        if (ticket.StatusUrl is null) throw new InvalidOperationException("This webhook did not return a status URL; status synchronization is unavailable for this ticket.");
        var endpoint = new Uri(LocalJiraConnectionSettingsStore.NormalizeBaseUrl(webhookUrl));
        var statusUrl = ReadSameOriginUrl(endpoint, ticket.StatusUrl)
            ?? throw new InvalidOperationException("The webhook status URL must use the configured HTTPS host.");
        var statusUri = new Uri(statusUrl);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(HttpMethod.Get, statusUri);
        request.Headers.TryAddWithoutValidation("X-YASK-CIEM-Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("X-YASK-CIEM-Signature", CreateWebhookSignature(credentials.Secret, "GET", statusUri, timestamp, Array.Empty<byte>()));
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var status = ReadString(json.RootElement, "status");
        return ApplyVerifiedStatus(ticket, status ?? ticket.Status, IsClosedStatus(status));
    }

    private static TicketingRequestRecord ApplyVerifiedStatus(TicketingRequestRecord ticket, string status, bool? isClosed)
        => ticket with
        {
            Status = status,
            UpdatedAtUtc = DateTime.UtcNow,
            ClosureVerifiedAtUtc = isClosed switch
            {
                true => ticket.ClosureVerifiedAtUtc ?? DateTime.UtcNow,
                false => null,
                _ => ticket.ClosureVerifiedAtUtc
            }
        };

    private static bool? IsClosedStatus(string? status, string? category = null)
    {
        if (string.Equals(category, "done", StringComparison.OrdinalIgnoreCase)) return true;
        if (category is not null && (category.Equals("new", StringComparison.OrdinalIgnoreCase)
            || category.Equals("indeterminate", StringComparison.OrdinalIgnoreCase))) return false;
        if (status is null) return null;
        if (new[] { "closed", "resolved", "done", "complete", "completed" }
            .Contains(status.Trim(), StringComparer.OrdinalIgnoreCase)) return true;
        if (new[] { "open", "new", "created", "to do", "in progress", "reopened", "on hold" }
            .Contains(status.Trim(), StringComparer.OrdinalIgnoreCase)) return false;
        return null;
    }

    private static bool? IsServiceNowClosedStatus(string? status)
    {
        if (status?.Trim() is "6" or "7") return true;
        if (status?.Trim() is "1" or "2" or "3" or "8") return false;
        return IsClosedStatus(status);
    }

    private static async Task<FileStream> AcquireSubmissionFileLockAsync(string dedupeKey, CancellationToken cancellationToken)
    {
        var lockDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YASK-CIEM", "ticketing", "locks");
        var lockPath = Path.Combine(lockDirectory, dedupeKey + ".lock");
        return await TicketingFileLock.AcquireAsync(lockPath, cancellationToken);
    }

    private static TicketingProviderConfiguration GetProviderConfiguration(JiraConnectionSettings settings, TicketingProvider provider)
        => settings.ProviderConfigurations?.GetValueOrDefault(provider) ?? new TicketingProviderConfiguration();

    private static string CreateDedupeKey(TicketingProvider provider, RecommendationModel recommendation)
    {
        var fingerprint = string.IsNullOrWhiteSpace(recommendation.Fingerprint) ? recommendation.Id.ToString("D") : recommendation.Fingerprint;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{provider}|{recommendation.Provider}|{fingerprint}")));
    }

    private static bool IsSafeHttpsUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && !string.IsNullOrWhiteSpace(uri.Host)
            && string.IsNullOrEmpty(uri.UserInfo);

    private static bool UsesNativeConnector(TicketingProvider provider)
        => provider is TicketingProvider.Jira or TicketingProvider.JiraServiceManagement or TicketingProvider.ServiceNow;

    private static AuthenticationHeaderValue Basic(string userName, string secret)
        => new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{secret}")));

    private static string CreateWebhookSignature(string secret, string method, Uri endpoint, string timestamp, byte[] body)
    {
        var bodyHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        var canonical = Encoding.UTF8.GetBytes($"{method}\n{endpoint.AbsolutePath}\n{timestamp}\n{bodyHash}");
        return "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), canonical)).ToLowerInvariant();
    }

    private static string? ReadString(JsonElement root, string property)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? ReadHttpsUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) ? uri.AbsoluteUri : null;

    private static string? ReadSameOriginUrl(Uri configuredEndpoint, string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && uri.Host.Equals(configuredEndpoint.Host, StringComparison.OrdinalIgnoreCase)
            && uri.Port == configuredEndpoint.Port
            && string.IsNullOrEmpty(uri.UserInfo)
                ? uri.AbsoluteUri
                : null;
}
