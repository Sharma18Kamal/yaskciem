using System.Globalization;
using System.Text.Json;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;

namespace CloudEntitle.Integration.Services;

public sealed partial class CloudActivityCollector(ICloudCommandRunner runner) : ICloudActivityCollector
{
    private const int MaximumEvents = 5000;
    private const int MaximumResponseCharacters = 16 * 1024 * 1024;

    public async Task<CloudActivitySnapshot> CollectAsync(CloudActivityQuery query, CancellationToken cancellationToken = default)
    {
        Validate(query);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var end = query.ThroughUtc.ToUniversalTime();
        var start = end.AddDays(-90);
        var events = new List<CloudActivityEvent>();
        var truncated = false;
        string source;
        string details;
        try
        {
            if (query.SourceKind != CloudActivitySource.Default)
                return await CollectDataPlaneAsync(query, start, end, timeout.Token);
            switch (query.Provider)
            {
                case CloudProvider.Aws:
                    source = $"CloudTrail event history ({query.Region})";
                    details = "Partial: selected region, management events only; data events, other regions and attribution gaps are not covered. API event names are not assumed to equal IAM actions.";
                    var identity = await RunAsync("aws", AwsArguments(query, ["sts", "get-caller-identity", "--output", "json"]), timeout.Token);
                    using (identity)
                        if (Text(identity.RootElement, "Account") != query.AccountId)
                            throw new InvalidOperationException("AWS CLI identity does not match the selected bound account.");
                    string? next = null;
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    do
                    {
                        var args = new List<string> { "cloudtrail", "lookup-events", "--start-time", Iso(start),
                            "--end-time", Iso(end), "--max-results", "50", "--no-paginate", "--output", "json" };
                        if (next is not null) args.AddRange(["--next-token", next]);
                        using var page = await RunAsync("aws", AwsArguments(query, args), timeout.Token);
                        foreach (var item in RequiredArray(page.RootElement, "Events").EnumerateArray())
                        {
                            var raw = Text(item, "CloudTrailEvent") ?? throw new InvalidOperationException("CloudTrail returned an event without audit evidence.");
                            using var audit = JsonDocument.Parse(raw);
                            var entry = audit.RootElement;
                            if (Text(entry, "recipientAccountId") != query.AccountId)
                                throw new InvalidOperationException("CloudTrail returned evidence outside the selected account.");
                            var principal = NestedText(entry, "userIdentity", "arn") ?? NestedText(entry, "userIdentity", "principalId") ?? "Unattributed";
                            Add(events, Text(entry, "eventID"), principal,
                                $"{RequiredText(entry, "eventSource")}/{RequiredText(entry, "eventName")}", null,
                                null, RequiredTime(entry, "eventTime"), !entry.TryGetProperty("errorCode", out _), start, end);
                        }
                        next = Text(page.RootElement, "NextToken");
                        if (next is not null && !seen.Add(next)) throw new InvalidOperationException("CloudTrail repeated a pagination token.");
                        truncated = events.Count >= MaximumEvents && next is not null;
                        if (next is not null && !truncated) await Task.Delay(TimeSpan.FromMilliseconds(500), timeout.Token);
                    } while (next is not null && !truncated);
                    break;
                case CloudProvider.Azure:
                    source = "Azure subscription Activity Log";
                    details = "Partial: subscription control-plane Activity Log only; Entra sign-ins and resource data-plane logs are not covered. Only terminal Succeeded/Failed events are counted.";
                    var path = $"/subscriptions/{query.AccountId}/providers/Microsoft.Insights/eventtypes/management/values";
                    var filter = Uri.EscapeDataString($"eventTimestamp ge '{Iso(start)}' and eventTimestamp le '{Iso(end)}'");
                    var url = $"https://management.azure.com{path}?api-version=2015-04-01&$filter={filter}";
                    var urls = new HashSet<string>(StringComparer.Ordinal);
                    do
                    {
                        if (!urls.Add(url)) throw new InvalidOperationException("Azure repeated a pagination URL.");
                        using var page = await RunAsync("az", ["rest", "--method", "get", "--url", url, "--output", "json"], timeout.Token);
                        foreach (var entry in RequiredArray(page.RootElement, "value").EnumerateArray())
                        {
                            if (!string.Equals(Text(entry, "subscriptionId"), query.AccountId, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException("Azure returned evidence outside the selected subscription.");
                            var status = NestedText(entry, "status", "value");
                            if (status is not ("Succeeded" or "Failed")) continue;
                            var action = NestedText(entry, "operationName", "value")
                                ?? throw new InvalidOperationException("Azure audit event has no operation name.");
                            var principal = NestedText(entry, "claims", "http://schemas.microsoft.com/identity/claims/objectidentifier")
                                ?? Text(entry, "caller") ?? "Unattributed";
                            var permission = NestedText(entry, "authorization", "action");
                            Add(events, Text(entry, "eventDataId"), principal, action, permission,
                                Text(entry, "resourceId"), RequiredTime(entry, "eventTimestamp"), status == "Succeeded", start, end);
                            events[^1] = events[^1] with { PermissionEvidenceSource = permission is null ? null : "Azure authorization.action" };
                        }
                        var link = Text(page.RootElement, "nextLink");
                        if (string.IsNullOrEmpty(link)) break;
                        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != "https"
                            || uri.Host != "management.azure.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0
                            || uri.Fragment.Length != 0 || !uri.AbsolutePath.Equals(path, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Azure returned an unsafe or out-of-scope pagination URL.");
                        url = link;
                        truncated = events.Count >= MaximumEvents;
                    } while (!truncated);
                    break;
                case CloudProvider.Gcp:
                    source = "GCP Cloud Audit Logs";
                    details = "Partial: retained project audit logs only; disabled Data Access logs, excluded/routed logs and IAM attribution gaps are not covered. Permission evidence comes from authorizationInfo, not inferred method names.";
                    var gcpFilter = $"logName:\"projects/{query.AccountId}/logs/cloudaudit.googleapis.com\" AND timestamp>=\"{Iso(start)}\" AND timestamp<=\"{Iso(end)}\"";
                    using (var page = await RunAsync("gcloud", ["logging", "read", gcpFilter, "--project", query.AccountId,
                        "--limit", (MaximumEvents + 1).ToString(CultureInfo.InvariantCulture), "--order", "desc", "--format", "json", "--quiet"], timeout.Token))
                    {
                        if (page.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("GCP returned an invalid audit page.");
                        truncated = page.RootElement.GetArrayLength() > MaximumEvents;
                        foreach (var entry in page.RootElement.EnumerateArray().Take(MaximumEvents))
                        {
                            var logName = RequiredText(entry, "logName");
                            if (!logName.StartsWith($"projects/{query.AccountId}/logs/cloudaudit.googleapis.com", StringComparison.Ordinal))
                                throw new InvalidOperationException("GCP returned evidence outside the selected project.");
                            if (!entry.TryGetProperty("protoPayload", out var payload))
                                throw new InvalidOperationException("GCP audit evidence is missing.");
                            var principal = NestedText(payload, "authenticationInfo", "principalEmail")
                                ?? NestedText(payload, "authenticationInfo", "principalSubject") ?? "Unattributed";
                            var method = RequiredText(payload, "methodName");
                            var success = !payload.TryGetProperty("status", out var status)
                                || !status.TryGetProperty("code", out var code) || code.GetInt32() == 0;
                            var id = $"{logName}|{RequiredText(entry, "insertId")}|{RequiredText(entry, "timestamp")}";
                            var time = RequiredTime(entry, "timestamp");
                            Add(events, id, principal, method, null, Text(payload, "resourceName"), time, success, start, end);
                            if (payload.TryGetProperty("authorizationInfo", out var permissions))
                            {
                                if (permissions.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("GCP permission evidence is invalid.");
                                foreach (var permission in permissions.EnumerateArray())
                                {
                                    var action = RequiredText(permission, "permission");
                                    var granted = permission.TryGetProperty("granted", out var grant) && grant.ValueKind == JsonValueKind.True;
                                    Add(events, id, principal, action, action, Text(permission, "resource") ?? Text(payload, "resourceName"),
                                        time, success && granted, start, end);
                                    events[^1] = events[^1] with { PermissionEvidenceSource = "GCP authorizationInfo.permission" };
                                }
                            }
                        }
                    }
                    break;
                default:
                    throw new ArgumentException("Unsupported audit provider.");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Activity collection timed out; no successful snapshot was published.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The cloud CLI returned malformed audit JSON; no successful snapshot was published.");
        }
        return new(Guid.NewGuid(), query.Provider, query.AccountId, start, end, source, details, truncated,
            events.DistinctBy(item => (item.EventId, item.Principal, item.Action, item.PermissionAction, item.Resource)).OrderByDescending(item => item.Timestamp).ToArray());
    }

    public static void Validate(CloudActivityQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!Enum.IsDefined(query.SourceKind)) throw new ArgumentException("Unsupported activity source.");
        if (query.ThroughUtc > DateTimeOffset.UtcNow.AddMinutes(1) || query.ThroughUtc < DateTimeOffset.UtcNow.AddDays(-1))
            throw new ArgumentException("Choose a current UTC collection end time.");
        var valid = query.Provider switch
        {
            CloudProvider.Aws => query.AccountId is { Length: 12 } && query.AccountId.All(char.IsAsciiDigit)
                && SafeArgument(query.Region, 64) && SafeOptionalArgument(query.Profile, 128),
            CloudProvider.Azure => Guid.TryParse(query.AccountId, out _),
            CloudProvider.Gcp => query.AccountId is { Length: >= 6 and <= 63 } && char.IsAsciiLetterLower(query.AccountId[0])
                && query.AccountId.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'),
            _ => false
        };
        if (!valid) throw new ArgumentException("A valid bound account and cloud context are required. AWS also requires an explicit region.");
        if (query.SourceKind != CloudActivitySource.Default) ValidateDataPlane(query);
    }

    private static bool SafeArgument(string? value, int max)
        => value is { Length: > 0 } && value.Length <= max && char.IsAsciiLetterOrDigit(value[0])
            && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    private static bool SafeOptionalArgument(string? value, int max) => string.IsNullOrEmpty(value) || SafeArgument(value, max);

    private async Task<JsonDocument> RunAsync(string command, IReadOnlyList<string> args, CancellationToken token)
    {
        var result = await runner.RunAsync(command, args, token);
        if (!result.IsSuccess) throw new InvalidOperationException($"{command} audit read failed (exit {result.ExitCode}). Check CLI authentication and read permissions; raw provider errors are not retained.");
        if (result.StandardOutput.Length > MaximumResponseCharacters) throw new InvalidOperationException("Audit response exceeds the collection size limit.");
        return JsonDocument.Parse(result.StandardOutput);
    }

    private static IReadOnlyList<string> AwsArguments(CloudActivityQuery query, IReadOnlyList<string> args)
        => args.Concat(["--region", query.Region, "--no-cli-pager"]).Concat(string.IsNullOrEmpty(query.Profile) ? [] : new[] { "--profile", query.Profile }).ToArray();
    private static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    private static JsonElement RequiredArray(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value : throw new InvalidOperationException($"Audit response has no valid {name} array.");
    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string? NestedText(JsonElement element, string parent, string name)
        => element.TryGetProperty(parent, out var value) && value.ValueKind == JsonValueKind.Object ? Text(value, name) : null;
    private static string RequiredText(JsonElement element, string name)
        => Text(element, name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"Audit evidence is missing {name}.");
    private static DateTimeOffset RequiredTime(JsonElement element, string name)
        => DateTimeOffset.TryParse(RequiredText(element, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value.ToUniversalTime() : throw new InvalidOperationException("Audit evidence has an invalid timestamp.");
    private static void Add(List<CloudActivityEvent> events, string? id, string principal, string action, string? permission,
        string? resource, DateTimeOffset time, bool success, DateTimeOffset start, DateTimeOffset end)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("Audit evidence has no stable event identifier.");
        if (time < start || time > end) throw new InvalidOperationException("Audit evidence falls outside the requested time window.");
        if (events.Count >= 20_000) throw new InvalidOperationException("Normalized audit evidence exceeds 20000 rows. Narrow collection scope before retrying.");
        events.Add(new(id, principal, action, permission, resource, time, success));
    }
}
