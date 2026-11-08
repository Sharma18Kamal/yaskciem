using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;

namespace CloudEntitle.Integration.Services;

public sealed class MultiCloudTrustCredentialEvidenceCollector(ICloudCommandRunner runner) : ITrustCredentialEvidenceCollector
{
    private readonly AzureTrustCredentialEvidenceCollector _azure = new(runner);
    private readonly AwsTrustCredentialEvidenceCollector _aws = new(runner);
    private readonly GcpTrustCredentialEvidenceCollector _gcp = new(runner);

    public Task<TrustCredentialSnapshot> CollectAsync(TrustCredentialEvidenceQuery query, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.Provider switch
        {
            CloudProvider.Azure => _azure.CollectAsync(query, token),
            CloudProvider.Aws => _aws.CollectAsync(query, token),
            CloudProvider.Gcp => _gcp.CollectAsync(query, token),
            _ => throw new ArgumentException("Trust and credential monitoring supports only bound Azure, AWS and GCP accounts.")
        };
    }
}

internal sealed class AwsTrustCredentialEvidenceCollector(ICloudCommandRunner runner)
{
    private const int MaximumRoles = 5_000;
    private const int MaximumObservations = 20_000;
    private const int MaximumOutputCharacters = 16 * 1024 * 1024;
    private static readonly TimeSpan CollectionBudget = TimeSpan.FromMinutes(4);
    private const string SourceName = "AWS IAM role trust policies and credential report metadata";

    public async Task<TrustCredentialSnapshot> CollectAsync(TrustCredentialEvidenceQuery query, CancellationToken token)
    {
        if (query.Provider != CloudProvider.Aws || string.IsNullOrWhiteSpace(query.AccountId)
            || !Regex.IsMatch(query.AccountId, "^[0-9]{12}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("AWS trust and credential monitoring requires an exact 12-digit bound account ID.");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(CollectionBudget);
        var started = DateTimeOffset.UtcNow;
        try
        {
            using var identity = await ReadAsync(["sts", "get-caller-identity"], budget.Token);
            if (Text(identity.RootElement, "Account") != query.AccountId)
                throw new InvalidOperationException("AWS CLI identity does not match the selected bound account.");

            var roles = await ReadRolesAsync(budget.Token);
            var observations = new List<TrustCredentialObservation>();
            foreach (var role in roles)
                AddRoleTrust(role, observations);

            var limitations = new List<string>
            {
                "AWS coverage is partial: this reads IAM role AssumeRolePolicyDocument trust statements in the selected account. It does not enumerate SCPs, resource policies, service-specific trust, external identity-provider configuration, organization/folder scope, or all possible authorization paths.",
                "No credential report is generated or refreshed. IAM get-credential-report is read-only and can return a previously generated report; its GeneratedTime is retained as metadata. Missing or unavailable reports leave credential inventory incomplete. Secret access-key values are never requested or stored."
            };
            var credentialsComplete = false;
            using (var report = await TryReadAsync(["iam", "get-credential-report"], budget.Token))
            {
                if (report is not null && Text(report.RootElement, "State") == "COMPLETE"
                    && Text(report.RootElement, "Content") is { Length: > 0 } content)
                {
                    var generated = Text(report.RootElement, "GeneratedTime");
                    var csv = Encoding.UTF8.GetString(Convert.FromBase64String(content));
                    var rows = ParseCsv(csv);
                    if (rows.Count < 2 || rows.Count > MaximumRoles + 2
                        || !rows.Skip(1).Any(row => Cell(row, 0) == "<root_account>"))
                        throw new InvalidOperationException("AWS IAM credential report is empty or malformed.");
                    AddCredentialReport(rows, observations);
                    credentialsComplete = true;
                    limitations.Add($"The read-only IAM credential report was generated at {generated ?? "an unspecified time"}; it may be stale relative to this observation.");
                }
                else
                {
                    limitations.Add("No COMPLETE pre-existing AWS IAM credential report was available; access-key/password metadata coverage is incomplete. Generate or refresh reports outside this read-only monitoring flow, then collect again.");
                }
            }

            return new TrustCredentialSnapshot(Guid.NewGuid(), CloudProvider.Aws, query.AccountId, started,
                DateTimeOffset.UtcNow, SourceName, observations, limitations)
            {
                CredentialInventoryComplete = credentialsComplete,
                TrustInventoryComplete = true
            };
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("AWS trust and credential collection exceeded four minutes. No monitoring snapshot was published.");
        }
    }

    private async Task<List<JsonElement>> ReadRolesAsync(CancellationToken token)
    {
        var result = new List<JsonElement>();
        string? next = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var args = new List<string> { "iam", "list-roles", "--max-items", (MaximumRoles + 1).ToString(CultureInfo.InvariantCulture) };
            if (next is not null) args.AddRange(["--starting-token", next]);
            using var page = await ReadAsync(args, token);
            if (!page.RootElement.TryGetProperty("Roles", out var roles) || roles.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("AWS IAM returned an invalid role inventory.");
            foreach (var role in roles.EnumerateArray())
            {
                if (result.Count >= MaximumRoles)
                    throw new InvalidOperationException("AWS IAM role inventory exceeds 5,000 roles. No truncated monitoring snapshot was published.");
                result.Add(role.Clone());
            }
            next = Text(page.RootElement, "NextToken");
            if (next is not null && !seen.Add(next))
                throw new InvalidOperationException("AWS IAM repeated a role pagination token.");
        } while (next is not null);
        return result;
    }

    private static void AddRoleTrust(JsonElement role, List<TrustCredentialObservation> output)
    {
        var roleName = RequiredText(role, "RoleName");
        var roleId = RequiredText(role, "RoleId");
        if (!role.TryGetProperty("AssumeRolePolicyDocument", out var policy) || policy.ValueKind != JsonValueKind.Object
            || !policy.TryGetProperty("Statement", out var statement))
            throw new InvalidOperationException("AWS IAM role trust policy metadata is missing or malformed.");
        var statements = statement.ValueKind == JsonValueKind.Array ? statement.EnumerateArray().ToArray() : [statement];
        if (statements.Length == 0)
            throw new InvalidOperationException("AWS IAM role trust policy has no enumerable statements.");
        foreach (var item in statements)
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("AWS IAM returned an invalid role trust statement.");
            if (!item.TryGetProperty("Principal", out _) || !item.TryGetProperty("Action", out _))
                throw new InvalidOperationException("AWS IAM role trust statement lacks its principal or action.");
            var principal = PropertyJson(item, "Principal");
            var condition = PropertyJson(item, "Condition");
            var actions = ReadActions(item);
            var sid = Text(item, "Sid") ?? "";
            var statementJson = TrustCredentialJson.CanonicalJson(item);
            var hash = Digest(statementJson);
            var evidenceId = $"{roleId}:{(sid.Length == 0 ? hash[..24] : sid)}";
            Add(output, new(roleId, roleName, "IamRoleTrustPolicy", evidenceId, roleName,
                principal, condition, actions, null, null, hash));
        }
    }

    private static void AddCredentialReport(IReadOnlyList<string[]> rows, List<TrustCredentialObservation> output)
    {
        var header = rows[0].Select((value, index) => (value, index))
            .ToDictionary(item => item.value, item => item.index, StringComparer.Ordinal);
        var required = new[] { "user", "arn", "password_enabled", "password_last_changed", "password_last_used",
            "mfa_active", "access_key_1_active", "access_key_1_last_rotated", "access_key_1_last_used_date",
            "access_key_1_last_used_region", "access_key_1_last_used_service", "access_key_2_active",
            "access_key_2_last_rotated", "access_key_2_last_used_date", "access_key_2_last_used_region",
            "access_key_2_last_used_service" };
        if (required.Any(column => !header.ContainsKey(column)))
            throw new InvalidOperationException("AWS IAM credential report lacks expected metadata columns.");
        for (var rowIndex = 1; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            if (row.Length != rows[0].Length)
                throw new InvalidOperationException("AWS IAM credential report contains a malformed row.");
            var username = Cell(row, header["user"]);
            var arn = Cell(row, header["arn"]);
            if (username.Length == 0 || arn.Length == 0) continue;
            AddReportObservation(output, username, arn, "password", row, header,
                ["password_enabled", "password_last_changed", "password_last_used", "mfa_active"]);
            for (var key = 1; key <= 2; key++)
            {
                var prefix = $"access_key_{key}_";
                AddReportObservation(output, username, arn, $"access-key-{key}", row, header,
                    [prefix + "active", prefix + "last_rotated", prefix + "last_used_date",
                        prefix + "last_used_region", prefix + "last_used_service"]);
            }
        }
    }

    private static void AddReportObservation(List<TrustCredentialObservation> output, string username, string arn,
        string credentialName, string[] row, IReadOnlyDictionary<string, int> header, IReadOnlyList<string> columns)
    {
        var metadata = columns.ToDictionary(column => column, column => Cell(row, header[column]), StringComparer.Ordinal);
        if (metadata.Values.All(string.IsNullOrEmpty)) return;
        var json = JsonSerializer.Serialize(metadata);
        var key = Digest($"{username}|{credentialName}");
        var name = $"{credentialName}: {json}";
        if (name.Length > 512) throw new InvalidOperationException("AWS credential report metadata exceeds the safe evidence limit.");
        Add(output, new(arn, username, "IamCredentialReport", key, name,
            null, null, [], null, null, Digest(json)));
    }

    private static IReadOnlyList<string[]> ParseCsv(string csv)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < csv.Length; index++)
        {
            var character = csv[index];
            if (quoted)
            {
                if (character == '"' && index + 1 < csv.Length && csv[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else if (character == '"') quoted = false;
                else field.Append(character);
            }
            else if (character == '"') quoted = true;
            else if (character == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (character is '\r' or '\n')
            {
                if (character == '\r' && index + 1 < csv.Length && csv[index + 1] == '\n') index++;
                row.Add(field.ToString());
                field.Clear();
                if (row.Any(value => value.Length > 0)) rows.Add(row.ToArray());
                row.Clear();
            }
            else field.Append(character);
        }
        if (quoted) throw new InvalidOperationException("AWS IAM credential report contains malformed CSV.");
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row.ToArray()); }
        return rows;
    }

    private async Task<JsonDocument?> TryReadAsync(IReadOnlyList<string> args, CancellationToken token)
    {
        var result = await runner.RunAsync("aws", AwsArgs(args), token);
        token.ThrowIfCancellationRequested();
        if (!result.IsSuccess) return null;
        return Parse(result.StandardOutput);
    }

    private async Task<JsonDocument> ReadAsync(IReadOnlyList<string> args, CancellationToken token)
    {
        var result = await runner.RunAsync("aws", AwsArgs(args), token);
        token.ThrowIfCancellationRequested();
        if (!result.IsSuccess)
            throw new InvalidOperationException("AWS IAM read failed. Verify the selected account, CLI connection, and read permissions; no snapshot was published.");
        return Parse(result.StandardOutput);
    }

    private static IReadOnlyList<string> AwsArgs(IReadOnlyList<string> args)
        => args.Concat(["--no-cli-pager", "--output", "json"]).ToArray();

    private static JsonDocument Parse(string output)
    {
        if (output.Length > MaximumOutputCharacters)
            throw new InvalidOperationException("AWS response exceeds 16 MiB. No monitoring snapshot was published.");
        return JsonDocument.Parse(output);
    }

    private static string PropertyJson(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return "{}";
        var json = value.GetRawText();
        if (json.Length > 2048) throw new InvalidOperationException($"AWS role trust {property} exceeds the safe evidence limit.");
        return json;
    }

    private static string[] ReadActions(JsonElement statement)
    {
        if (!statement.TryGetProperty("Action", out var action))
            throw new InvalidOperationException("AWS IAM role trust statement lacks an action.");
        var values = action.ValueKind == JsonValueKind.Array
            ? action.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String
                ? value.GetString()! : throw new InvalidOperationException("AWS IAM trust action is not a string.")).ToArray()
            : action.ValueKind == JsonValueKind.String ? [action.GetString()!] : throw new InvalidOperationException("AWS IAM trust action is malformed.");
        if (values.Length == 0) throw new InvalidOperationException("AWS IAM role trust statement has no action.");
        if (values.Length > 32 || values.Any(value => value.Length > 2048))
            throw new InvalidOperationException("AWS role trust action list exceeds the safe evidence limit.");
        return values;
    }

    private static string Cell(string[] row, int index) => index < row.Length ? row[index] : "";
    private static string? Text(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string RequiredText(JsonElement element, string property)
        => Text(element, property) is { Length: > 0 } value ? value : throw new InvalidOperationException($"AWS response is missing {property}.");
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static void Add(List<TrustCredentialObservation> output, TrustCredentialObservation item)
    {
        if (output.Count >= MaximumObservations)
            throw new InvalidOperationException("AWS trust/credential evidence exceeds 20,000 observations. No truncated snapshot was published.");
        output.Add(item);
    }
}

internal sealed class GcpTrustCredentialEvidenceCollector(ICloudCommandRunner runner)
{
    private const int MaximumServiceAccounts = 1_000;
    private const int MaximumObservations = 20_000;
    private const int MaximumOutputCharacters = 16 * 1024 * 1024;
    private static readonly TimeSpan CollectionBudget = TimeSpan.FromMinutes(4);
    private const string SourceName = "GCP project service-account keys and IAM impersonation policies";
    public async Task<TrustCredentialSnapshot> CollectAsync(TrustCredentialEvidenceQuery query, CancellationToken token)
    {
        if (query.Provider != CloudProvider.Gcp || !ValidProject(query.AccountId))
            throw new ArgumentException("GCP trust and credential monitoring requires a valid exact bound project ID or project number.");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(CollectionBudget);
        var started = DateTimeOffset.UtcNow;
        try
        {
            using var project = await ReadAsync(["projects", "describe", query.AccountId], budget.Token);
            var projectId = Text(project.RootElement, "projectId");
            var projectNumber = Text(project.RootElement, "projectNumber");
            if (projectId is null || query.AccountId != projectId && query.AccountId != projectNumber)
                throw new InvalidOperationException("gcloud project identity does not match the selected bound project.");

            var serviceAccounts = await ReadServiceAccountsAsync(projectId, budget.Token);
            var observations = new List<TrustCredentialObservation>();
            var limitations = new List<string>
            {
                "GCP coverage is partial: this reads every direct IAM binding in one selected project and its service accounts, including service-account impersonation bindings, plus user-managed service-account key metadata. It does not enumerate inherited organization/folder policies, workforce identity pools, key use, organization constraints, or other projects.",
                "Key inventory is limited to user-managed service-account keys; system-managed keys and non-service-account credentials are outside this source. Custom role definitions, group membership, and external principal membership are not resolved."
            };
            var credentialComplete = true;
            var trustComplete = true;

            using (var policy = await TryReadAsync(["projects", "get-iam-policy", projectId], budget.Token))
            {
                if (policy is null) trustComplete = false;
                else AddPolicyBindings(policy.RootElement, "GcpProjectIamBinding", projectId, observations);
            }

            var keyFailures = 0;
            var policyFailures = 0;
            foreach (var account in serviceAccounts)
            {
                budget.Token.ThrowIfCancellationRequested();
                var email = Text(account, "email");
                if (email is null || !email.Contains('@'))
                    throw new InvalidOperationException("gcloud returned a service account without a valid email address.");
                using (var keys = await TryReadAsync(
                           ["iam", "service-accounts", "keys", "list", "--iam-account", email, "--project", projectId,
                                          "--managed-by=user", "--limit=101",
                                          "--format=json(name,keyType,keyOrigin,validAfterTime,validBeforeTime,disabled)"],
                           budget.Token))
                {
                    if (keys is null) keyFailures++;
                    else AddUserManagedKeys(keys.RootElement, email, observations);
                }
                using (var policy = await TryReadAsync(
                           ["iam", "service-accounts", "get-iam-policy", email, "--project", projectId], budget.Token))
                {
                    if (policy is null) policyFailures++;
                    else AddPolicyBindings(policy.RootElement, "GcpServiceAccountIamBinding", email, observations);
                }
            }
            if (keyFailures > 0)
            {
                credentialComplete = false;
                limitations.Add($"gcloud could not read user-managed key metadata for {keyFailures} service account(s); missing keys must not be interpreted as removals.");
            }
            if (policyFailures > 0)
            {
                trustComplete = false;
                limitations.Add($"gcloud could not read direct IAM policy for {policyFailures} service account(s); missing policy evidence must not be interpreted as removal.");
            }
            if (!trustComplete)
                limitations.Add("The selected project's IAM impersonation policy could not be completely enumerated; no absence/removal inference is valid for this source.");
            return new TrustCredentialSnapshot(Guid.NewGuid(), CloudProvider.Gcp, query.AccountId, started,
                DateTimeOffset.UtcNow, SourceName, observations, limitations)
            {
                CredentialInventoryComplete = credentialComplete,
                TrustInventoryComplete = trustComplete
            };
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("GCP trust and credential collection exceeded four minutes. No monitoring snapshot was published.");
        }
    }

    private async Task<List<JsonElement>> ReadServiceAccountsAsync(string projectId, CancellationToken token)
    {
        using var document = await ReadAsync(
            ["iam", "service-accounts", "list", "--project", projectId, "--limit", (MaximumServiceAccounts + 1).ToString(CultureInfo.InvariantCulture)],
            token);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("gcloud returned an invalid service-account inventory.");
        var accounts = document.RootElement.EnumerateArray().Select(item => item.Clone()).ToList();
        if (accounts.Count > MaximumServiceAccounts)
            throw new InvalidOperationException("GCP project exceeds 1,000 service accounts. No truncated monitoring snapshot was published.");
        return accounts;
    }

    private static void AddPolicyBindings(JsonElement policy, string kind, string resource, List<TrustCredentialObservation> output)
    {
        var bindings = ReadBindings(policy);
        foreach (var binding in bindings.EnumerateArray())
        {
            var role = Text(binding, "role");
            if (role is null) throw new InvalidOperationException("GCP IAM binding lacks a role name.");
            var condition = binding.TryGetProperty("condition", out var value) ? value.GetRawText() : "{}";
            foreach (var member in Members(binding))
                AddPolicyBinding(output, kind, resource, member, role, condition);
        }
    }

    private static void AddPolicyBinding(List<TrustCredentialObservation> output, string kind, string resource,
        string member, string role, string condition)
    {
        if (resource.Length > 512 || condition.Length > 2048 || member.Length > 2048 || role.Length > 256)
            throw new InvalidOperationException("GCP IAM binding metadata exceeds the safe evidence limit.");
        var principal = resource;
        var name = $"{role} granted to {member}";
        if (name.Length > 512)
            throw new InvalidOperationException("GCP IAM binding description exceeds the safe evidence limit.");
        var evidenceId = Digest($"{kind}|{resource}|{role}|{member}|{condition}");
        var metadata = JsonSerializer.Serialize(new { resource, role, member, condition });
        Add(output, new(principal, resource, kind, evidenceId, name, member, condition, [role], null, null, Digest(metadata)));
    }

    private static IEnumerable<string> Members(JsonElement binding)
    {
        if (!binding.TryGetProperty("members", out var members) || members.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("GCP IAM binding lacks a valid members list.");
        foreach (var item in members.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } member)
                yield return member;
            else throw new InvalidOperationException("GCP IAM binding contains an invalid member.");
    }

    private static JsonElement ReadBindings(JsonElement policy)
    {
        if (policy.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("GCP returned an invalid IAM policy.");
        if (!policy.TryGetProperty("bindings", out var bindings) || bindings.ValueKind == JsonValueKind.Null)
        {
            using var empty = JsonDocument.Parse("[]");
            return empty.RootElement.Clone();
        }
        if (bindings.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("GCP returned an invalid IAM policy bindings list.");
        return bindings;
    }

    private static void AddUserManagedKeys(JsonElement keys, string email, List<TrustCredentialObservation> output)
    {
        if (keys.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("gcloud returned an invalid service-account key list.");
        if (keys.GetArrayLength() > 100)
            throw new InvalidOperationException("A GCP service account exceeds 100 user-managed keys. No truncated monitoring snapshot was published.");
        foreach (var key in keys.EnumerateArray())
        {
            var resourceName = Text(key, "name") ?? throw new InvalidOperationException("gcloud returned key metadata without a resource name.");
            var evidenceId = resourceName.Split('/').LastOrDefault();
            if (string.IsNullOrWhiteSpace(evidenceId) || resourceName.Contains("private", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("gcloud returned an invalid service-account key identity.");
            var keyType = Text(key, "keyType") ?? "";
            var origin = Text(key, "keyOrigin") ?? "";
            var disabled = Text(key, "disabled") ?? "false";
            var name = $"{keyType}/{origin}; disabled={disabled}";
            var validFrom = Date(key, "validAfterTime");
            var validUntil = Date(key, "validBeforeTime");
            var metadata = JsonSerializer.Serialize(new { keyType, origin, disabled, validFrom, validUntil });
            Add(output, new(email, email, "GcpUserManagedServiceAccountKey", evidenceId, name,
                null, null, [], validFrom, validUntil, Digest(metadata)));
        }
    }

    private async Task<JsonDocument?> TryReadAsync(IReadOnlyList<string> args, CancellationToken token)
    {
        var result = await runner.RunAsync("gcloud", GcloudArgs(args), token);
        token.ThrowIfCancellationRequested();
        if (!result.IsSuccess) return null;
        return Parse(result.StandardOutput);
    }

    private async Task<JsonDocument> ReadAsync(IReadOnlyList<string> args, CancellationToken token)
    {
        var result = await runner.RunAsync("gcloud", GcloudArgs(args), token);
        token.ThrowIfCancellationRequested();
        if (!result.IsSuccess)
            throw new InvalidOperationException("gcloud trust/credential read failed. Verify the selected project, active CLI connection, and read permissions; no snapshot was published.");
        return Parse(result.StandardOutput);
    }

    private static IReadOnlyList<string> GcloudArgs(IReadOnlyList<string> args)
        => args.Concat(["--quiet"]).Concat(args.Any(value => value.StartsWith("--format=", StringComparison.Ordinal))
            ? [] : ["--format=json"]).ToArray();

    private static JsonDocument Parse(string output)
    {
        if (output.Length > MaximumOutputCharacters)
            throw new InvalidOperationException("GCP response exceeds 16 MiB. No monitoring snapshot was published.");
        return JsonDocument.Parse(output);
    }

    private static DateTimeOffset? Date(JsonElement element, string property)
        => DateTimeOffset.TryParse(Text(element, property), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var value) ? value.ToUniversalTime() : null;

    private static string? Text(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString()
                : value.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number ? value.ToString() : null
            : null;

    private static bool ValidProject(string value)
        => !string.IsNullOrWhiteSpace(value) && value.Length is >= 6 and <= 30
            && Regex.IsMatch(value, "^(?:[a-z][a-z0-9-]{4,28}[a-z0-9]|[0-9]{6,30})$", RegexOptions.CultureInvariant);

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static void Add(List<TrustCredentialObservation> output, TrustCredentialObservation item)
    {
        if (output.Count >= MaximumObservations)
            throw new InvalidOperationException("GCP trust/credential evidence exceeds 20,000 observations. No truncated snapshot was published.");
        output.Add(item);
    }
}

internal static class TrustCredentialJson
{
    public static string CanonicalJson(JsonElement element)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            WriteCanonical(writer, element);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
