using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.Integration.Services;

namespace CloudEntitle.LicenseServer.Services;

public sealed record EffectiveDecisionConnection
{
    public Guid TenantId { get; init; }
    public Guid OrganizationId { get; init; }
    public string SourceId { get; init; } = "";
    public CloudProvider Provider { get; init; } = CloudProvider.Gcp;
    public string AccountId { get; init; } = "";
    public string CollectorIdentity { get; init; } = "";
    public string ExpectedOAuthSubjectId { get; init; } = "";
    public string OAuthTokenEnvironmentVariable { get; init; } = "";
    public bool Enabled { get; init; }
    public bool AcceptGcpPreview { get; init; }
    public string[] Principals { get; init; } = [];
    public string[] PrivilegedPermissions { get; init; } = [];

    public string FullResourceName => $"//cloudresourcemanager.googleapis.com/projects/{AccountId}";
    public string ConfigurationSha256 => GcpEffectiveDecisionEvaluator.Sha256(JsonSerializer.Serialize(new
    {
        TenantId, OrganizationId, SourceId, Provider, AccountId, CollectorIdentity, ExpectedOAuthSubjectId, OAuthTokenEnvironmentVariable,
        Enabled, AcceptGcpPreview, Principals = Principals.Order(StringComparer.Ordinal).ToArray(),
        PrivilegedPermissions = PrivilegedPermissions.Order(StringComparer.Ordinal).ToArray(),
        profile = GcpEffectiveDecisionEvaluator.VerificationProfile
    }));
    public EffectiveDecisionSource Describe() => new(SourceId, Provider, AccountId, CollectorIdentity,
        ConfigurationSha256, Enabled, Provider != CloudProvider.Gcp
            ? "Unsupported: Azure/AWS simulations do not establish a complete native authorization decision."
            : !AcceptGcpPreview ? "Unsupported until the administrator explicitly accepts the GCP v3beta PAB preview."
            : "One exact unconditional project IAM decision, including allow/deny/PAB; not proof of service execution, VPC access or all permissions.",
        Principals.ToArray(), PrivilegedPermissions.ToArray(), FullResourceName);

    public void Validate()
    {
        if (TenantId == Guid.Empty || OrganizationId == Guid.Empty
            || !Regex.IsMatch(SourceId, "^[a-zA-Z0-9_-]{1,64}$")
            || !Enum.IsDefined(Provider) || AccountId.Length is < 1 or > 128
            || CollectorIdentity.Length is < 3 or > 254
            || Principals is null || Principals.Length is < 1 or > 100
            || PrivilegedPermissions is null || PrivilegedPermissions.Length is < 1 or > 16
            || Principals.Any(p => p is null || !Regex.IsMatch(p, @"^[a-zA-Z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}$"))
            || PrivilegedPermissions.Any(p => p != "resourcemanager.projects.setIamPolicy"))
            throw new ArgumentException("Invalid bounded authoritative-decision source configuration.");
        if (Provider == CloudProvider.Gcp && (!Regex.IsMatch(AccountId, "^[a-z][a-z0-9-]{4,28}[a-z0-9]$")
            || !Regex.IsMatch(ExpectedOAuthSubjectId, "^[0-9]{1,32}$")
            || !Regex.IsMatch(OAuthTokenEnvironmentVariable, "^YASK_GCP_[A-Z0-9_]{1,100}$")))
            throw new ArgumentException("GCP project and protected OAuth environment reference are required.");
    }
}

public sealed record NativeEffectiveDecisionCapture(
    EffectiveDecisionSnapshot Snapshot, string NativeResponseJson, IReadOnlyList<string> ResourceHierarchy);

public interface IGcpAuthoritativeDecisionCollector
{
    Task<NativeEffectiveDecisionCapture> CaptureAsync(EffectiveDecisionConnection source,
        CaptureEffectiveDecisionRequest request, CancellationToken token);
}

public sealed class GcpAuthoritativeDecisionCollector(
    IHttpClientFactory clients, Func<string, string?> credentialReader) : IGcpAuthoritativeDecisionCollector
{
    private const int MaxResponse = 1024 * 1024;

    public async Task<NativeEffectiveDecisionCapture> CaptureAsync(EffectiveDecisionConnection source,
        CaptureEffectiveDecisionRequest request, CancellationToken token)
    {
        source.Validate();
        AuthoritativeDecisionService.ValidateSample(source, request);
        if (source.Provider != CloudProvider.Gcp || !source.AcceptGcpPreview)
            throw new NotSupportedException("Native complete source evaluation is unsupported.");
        var accessToken = credentialReader(source.OAuthTokenEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(accessToken) || accessToken.Length > 8192
            || accessToken.Any(char.IsWhiteSpace)) throw new InvalidOperationException("Provider OAuth connection is unavailable.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var cancellation = timeout.Token;
        var started = DateTimeOffset.UtcNow;
        var client = clients.CreateClient("AuthoritativeGcpDecision");

        // The native userinfo response verifies the configured OAuth actor, not a client-uploaded email.
        var identityJson = await SendAsync(client, HttpMethod.Get, "https://www.googleapis.com/oauth2/v2/userinfo",
            accessToken, null, null, cancellation);
        using (var identity = JsonDocument.Parse(identityJson))
            if (!identity.RootElement.TryGetProperty("email", out var email) || email.GetString() != source.CollectorIdentity
                || !identity.RootElement.TryGetProperty("id", out var subject) || subject.GetString() != source.ExpectedOAuthSubjectId
                || !identity.RootElement.TryGetProperty("verified_email", out var verified) || verified.ValueKind != JsonValueKind.True)
                throw new UnauthorizedAccessException("Authenticated provider identity differs from the configured source.");

        var projectJson = await SendAsync(client, HttpMethod.Get,
            $"https://cloudresourcemanager.googleapis.com/v3/projects/{source.AccountId}",
            accessToken, null, source.AccountId, cancellation);
        var hierarchy = new List<string>();
        string parent;
        using (var project = JsonDocument.Parse(projectJson))
        {
            var root = project.RootElement;
            if (!root.TryGetProperty("projectId", out var projectId) || projectId.GetString() != source.AccountId
                || !root.TryGetProperty("name", out var name)
                || !Regex.IsMatch(name.GetString() ?? "", "^projects/[0-9]{1,24}$")
                || !root.TryGetProperty("state", out var state) || state.GetString() != "ACTIVE")
                throw new UnauthorizedAccessException("Provider project scope could not be verified.");
            hierarchy.Add(source.FullResourceName);
            parent = root.TryGetProperty("parent", out var parentValue) ? parentValue.GetString() ?? "" : "";
        }
        while (parent.Length > 0)
        {
            if (hierarchy.Count >= 12 || !Regex.IsMatch(parent, "^(folders|organizations)/[0-9]{1,24}$"))
                throw new InvalidOperationException("The provider resource hierarchy is not bounded.");
            hierarchy.Add("//cloudresourcemanager.googleapis.com/" + parent);
            if (parent.StartsWith("organizations/", StringComparison.Ordinal)) break;
            var folderJson = await SendAsync(client, HttpMethod.Get,
                "https://cloudresourcemanager.googleapis.com/v3/" + parent, accessToken, null, source.AccountId, cancellation);
            using var folder = JsonDocument.Parse(folderJson);
            var root = folder.RootElement;
            if (!root.TryGetProperty("name", out var name) || name.GetString() != parent
                || !root.TryGetProperty("state", out var state) || state.GetString() != "ACTIVE"
                || !root.TryGetProperty("parent", out var next))
                throw new InvalidOperationException("The provider resource hierarchy is unavailable.");
            parent = next.GetString() ?? "";
        }

        var raw = await SendAsync(client, HttpMethod.Post, GcpEffectiveDecisionEvaluator.Endpoint, accessToken,
            new { accessTuple = new { principal = request.Principal, permission = request.Permission,
                fullResourceName = request.FullResourceName } }, source.AccountId, cancellation);
        var completed = DateTimeOffset.UtcNow;
        var evaluation = GcpEffectiveDecisionEvaluator.Evaluate(raw, request, hierarchy);
        var snapshot = new EffectiveDecisionSnapshot(Guid.NewGuid(), source.Provider, source.AccountId, source.SourceId,
            source.ConfigurationSha256, source.CollectorIdentity, request.Principal, request.Permission,
            request.FullResourceName, evaluation.ContextSha256, completed, started, completed, evaluation.Decision,
            evaluation.Coverage, GcpEffectiveDecisionEvaluator.VerificationProfile,
            GcpEffectiveDecisionEvaluator.Sha256(raw), evaluation.PolicyVersions, evaluation.Blockers);
        return new(snapshot, raw, hierarchy);
    }

    private static async Task<string> SendAsync(HttpClient client, HttpMethod method, string endpoint, string credential,
        object? payload, string? project, CancellationToken token)
    {
        using var message = new HttpRequestMessage(method, endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        if (project is not null) message.Headers.Add("x-goog-user-project", project);
        if (payload is not null) message.Content = JsonContent.Create(payload);
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Read-only provider decision request failed.", null, response.StatusCode);
        if (response.RequestMessage?.RequestUri?.AbsoluteUri != endpoint)
            throw new InvalidOperationException("Provider endpoint provenance changed.");
        if (response.Content.Headers.ContentLength > MaxResponse) throw new InvalidOperationException("Native response exceeds the bounded limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var bytes = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int length;
        while ((length = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (bytes.Length + length > MaxResponse) throw new InvalidOperationException("Native response exceeds the bounded limit.");
            bytes.Write(buffer, 0, length);
        }
        return new UTF8Encoding(false, true).GetString(bytes.ToArray());
    }
}
