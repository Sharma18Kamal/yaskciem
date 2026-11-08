using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;

namespace CloudEntitle.Integration.Services;

public sealed class AzureTrustCredentialEvidenceCollector(ICloudCommandRunner runner) : ITrustCredentialEvidenceCollector
{
    private const int MaximumPrincipals = 5_000;
    private const int MaximumObservations = 20_000;
    private const int MaximumPages = 20;
    private const int GraphBatchSize = 20;
    private static readonly TimeSpan CollectionBudget = TimeSpan.FromMinutes(4);
    private const string SourceName = "Azure CLI / Microsoft Graph Entra application credentials and federation";

    public async Task<TrustCredentialSnapshot> CollectAsync(TrustCredentialEvidenceQuery query, CancellationToken token = default)
    {
        ValidateQuery(query);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(CollectionBudget);
        var started = DateTimeOffset.UtcNow;
        try
        {
            using var account = await ReadAsync("az",
                ["account", "show", "--subscription", query.AccountId, "--output", "json"], budget.Token);
            var subscription = Required(account.RootElement, "id");
            if (!subscription.Equals(query.AccountId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Azure CLI subscription differs from the selected binding.");
            var tenantId = Required(account.RootElement, "tenantId");
            if (!Guid.TryParse(tenantId, out _))
                throw new InvalidOperationException("Azure CLI returned an invalid Entra tenant identity.");

            var principals = await ReadServicePrincipalsAsync(budget.Token);
            if (principals.Count > MaximumPrincipals)
                throw new InvalidOperationException("Entra source exceeds 5,000 service principals. No truncated monitoring snapshot was published.");
            var observations = new List<TrustCredentialObservation>();
            foreach (var principal in principals)
                AddCredentials(principal, observations);

            var limitations = new List<string>
            {
                "On-demand Microsoft Graph observation for the Entra tenant attached to the selected Azure subscription. Coverage is partial: it does not collect Azure RBAC trust, external application-owner trust, workload federation providers outside local application registrations, or other cloud providers.",
                "Credential secret values, private keys, tokens and certificate bytes are neither requested nor stored. Metadata differences are observations, not provider change-time attestations or effective-access decisions."
            };
            var localApplications = principals
                .Where(principal => IsOwnedByTenant(principal, tenantId) && !string.IsNullOrWhiteSpace(Text(principal, "appId")))
                .ToArray();
            var trustComplete = true;
            var trustFailures = await AddFederatedCredentialsAsync(localApplications, observations, budget.Token);
            if (trustFailures > 0)
            {
                trustComplete = false;
                limitations.Add($"Federated credential reads failed for {trustFailures} local application(s). No absence/removal inference is valid for this snapshot.");
            }
            return new TrustCredentialSnapshot(Guid.NewGuid(), CloudProvider.Azure, query.AccountId, started,
                DateTimeOffset.UtcNow, SourceName, observations.OrderBy(item => item.PrincipalId, StringComparer.Ordinal)
                    .ThenBy(item => item.Kind, StringComparer.Ordinal).ThenBy(item => item.EvidenceId, StringComparer.Ordinal).ToArray(),
                limitations)
            {
                CredentialInventoryComplete = true,
                TrustInventoryComplete = trustComplete
            };
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Entra trust and credential collection exceeded four minutes. No monitoring snapshot was published.");
        }
    }

    private async Task<List<JsonElement>> ReadServicePrincipalsAsync(CancellationToken token)
    {
        var result = new List<JsonElement>();
        string? next = "https://graph.microsoft.com/v1.0/servicePrincipals?$select=id,appId,appOwnerOrganizationId,displayName,passwordCredentials,keyCredentials&$top=999";
        for (var page = 0; next is not null; page++)
        {
            if (page >= MaximumPages)
                throw new InvalidOperationException("Entra service-principal pagination exceeds 20 pages. No truncated monitoring snapshot was published.");
            ValidateGraphUrl(next);
            using var document = await ReadAsync("az", ["rest", "--method", "GET", "--url", next, "--output", "json"], token);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("value", out var values)
                || values.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Microsoft Graph returned an invalid service-principal page.");
            foreach (var value in values.EnumerateArray())
            {
                if (result.Count >= MaximumPrincipals)
                    throw new InvalidOperationException("Entra source exceeds 5,000 service principals. No truncated monitoring snapshot was published.");
                if (value.ValueKind != JsonValueKind.Object || Text(value, "id") is null
                    || !value.TryGetProperty("passwordCredentials", out var passwords) || passwords.ValueKind != JsonValueKind.Array
                    || !value.TryGetProperty("keyCredentials", out var keys) || keys.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("Microsoft Graph credential metadata is incomplete. No snapshot was published.");
                result.Add(value.Clone());
            }
            next = Text(root, "@odata.nextLink");
        }
        return result;
    }

    private async Task<int> AddFederatedCredentialsAsync(IReadOnlyList<JsonElement> applications,
        List<TrustCredentialObservation> output, CancellationToken token)
    {
        var failures = 0;
        foreach (var batch in applications.Chunk(GraphBatchSize))
        {
            token.ThrowIfCancellationRequested();
            var requests = batch.Select((application, index) =>
            {
                var appId = Required(application, "appId");
                if (!Guid.TryParse(appId, out _)) throw new InvalidOperationException("Microsoft Graph returned an invalid application ID.");
                var appObjectId = Required(application, "id");
                if (!Guid.TryParse(appObjectId, out _)) throw new InvalidOperationException("Microsoft Graph returned an invalid application object ID.");
                return (Id: index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Url: $"/applications/{appObjectId}/federatedIdentityCredentials?$select=id,name,issuer,subject,audiences",
                    PrincipalId: appId, PrincipalName: Text(application, "displayName") ?? appId);
            }).ToArray();
            var payload = JsonSerializer.Serialize(new
            {
                requests = requests.Select(item => new { id = item.Id, method = "GET", url = item.Url })
            });
            using var response = await ReadAsync("az",
                ["rest", "--method", "POST", "--url", "https://graph.microsoft.com/v1.0/$batch",
                    "--body", payload, "--output", "json"], token);
            if (!response.RootElement.TryGetProperty("responses", out var responses) || responses.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Microsoft Graph returned an invalid federated-credential batch.");
            var byId = responses.EnumerateArray().Where(item => Text(item, "id") is not null)
                .ToDictionary(item => Text(item, "id")!, item => item, StringComparer.Ordinal);
            foreach (var request in requests)
            {
                if (!byId.TryGetValue(request.Id, out var item) || !item.TryGetProperty("status", out var status)
                    || !status.TryGetInt32(out var code) || code is < 200 or >= 300
                    || !item.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object
                    || !body.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
                {
                    failures++;
                    continue;
                }
                if (body.TryGetProperty("@odata.nextLink", out _)) failures++;
                foreach (var trust in values.EnumerateArray())
                {
                    var evidenceId = Required(trust, "id");
                    var issuer = Required(trust, "issuer");
                    var subject = Required(trust, "subject");
                    var audiences = ReadStringArray(trust, "audiences");
                    Add(output, CreateObservation(request.PrincipalId, request.PrincipalName,
                        "FederatedIdentityCredential", evidenceId, Text(trust, "name") ?? evidenceId,
                        issuer, subject, audiences, null, null));
                }
            }
        }
        return failures;
    }

    private static void AddCredentials(JsonElement principal, List<TrustCredentialObservation> output)
    {
        var principalId = Required(principal, "appId");
        var principalName = Text(principal, "displayName") ?? principalId;
        foreach (var (property, kind) in new[]
                 {
                     ("passwordCredentials", "PasswordCredential"),
                     ("keyCredentials", "KeyCredential")
                 })
        {
            foreach (var credential in principal.GetProperty(property).EnumerateArray())
            {
                var id = Text(credential, "keyId");
                var name = Text(credential, "displayName") ?? "";
                var start = Date(credential, "startDateTime");
                var end = Date(credential, "endDateTime");
                var credentialType = Text(credential, "type") ?? "";
                var usage = Text(credential, "usage") ?? "";
                if (string.IsNullOrWhiteSpace(id))
                    id = Digest($"{kind}|{name}|{start:O}|{end:O}|{credentialType}|{usage}");
                Add(output, CreateObservation(principalId, principalName, kind, id, name, null, null, [],
                    start, end, credentialType, usage));
            }
        }
    }

    private static TrustCredentialObservation CreateObservation(string principalId, string principalName, string kind,
        string evidenceId, string name, string? issuer, string? subject, IReadOnlyList<string> audiences,
        DateTimeOffset? start, DateTimeOffset? end, string credentialType = "", string usage = "")
    {
        var stable = JsonSerializer.Serialize(new { principalId, kind, evidenceId, name, issuer, subject, audiences, start, end, credentialType, usage });
        return new(principalId, principalName, kind, evidenceId, name, issuer, subject, audiences, start, end,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stable))).ToLowerInvariant());
    }

    private static void Add(List<TrustCredentialObservation> output, TrustCredentialObservation observation)
    {
        if (output.Count >= MaximumObservations)
            throw new InvalidOperationException("Trust and credential source exceeds 20,000 observations. No truncated snapshot was published.");
        output.Add(observation);
    }

    private async Task<JsonDocument> ReadAsync(string command, IReadOnlyList<string> arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result = await runner.RunAsync(command, arguments, token);
        token.ThrowIfCancellationRequested();
        if (!result.IsSuccess)
            throw new InvalidOperationException("Azure CLI trust/credential read failed. Verify the client connection and Microsoft Graph read consent; no snapshot was published.");
        if (result.StandardOutput.Length > 16 * 1024 * 1024)
            throw new InvalidOperationException("Microsoft Graph response exceeds 16 MiB. No snapshot was published.");
        return JsonDocument.Parse(result.StandardOutput);
    }

    private static bool IsOwnedByTenant(JsonElement principal, string tenantId)
        => Text(principal, "appOwnerOrganizationId")?.Equals(tenantId, StringComparison.OrdinalIgnoreCase) == true;

    private static DateTimeOffset? Date(JsonElement element, string property)
        => DateTimeOffset.TryParse(Text(element, property), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var value) ? value.ToUniversalTime() : null;

    private static string[] ReadStringArray(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array
            || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            throw new InvalidOperationException($"Microsoft Graph returned invalid {property} values.");
        return value.EnumerateArray().Select(item => item.GetString()!).ToArray();
    }

    private static string Required(JsonElement element, string property)
        => Text(element, property) ?? throw new InvalidOperationException($"Microsoft Graph response is missing {property}.");

    private static string? Text(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void ValidateGraphUrl(string url)
    {
        if (url.Length > 8192 || !Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps
            || !parsed.Host.Equals("graph.microsoft.com", StringComparison.OrdinalIgnoreCase)
            || parsed.Port != 443 || !parsed.AbsolutePath.StartsWith("/v1.0/", StringComparison.Ordinal)
            || parsed.UserInfo.Length != 0 || parsed.Fragment.Length != 0)
            throw new InvalidOperationException("Microsoft Graph returned an unsafe pagination URL.");
    }

    private static string Digest(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void ValidateQuery(TrustCredentialEvidenceQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Provider != CloudProvider.Azure || !Guid.TryParseExact(query.AccountId, "D", out _))
            throw new ArgumentException("Trust and credential monitoring currently supports only an exact Azure subscription binding.");
    }
}
