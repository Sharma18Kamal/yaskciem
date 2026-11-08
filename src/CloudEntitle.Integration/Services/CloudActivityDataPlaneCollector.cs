using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;

namespace CloudEntitle.Integration.Services;

public sealed partial class CloudActivityCollector
{
    private const int MaximumArchiveFiles = 128;
    private const int MaximumArchiveBytes = 8 * 1024 * 1024;
    private const int MaximumDecodedBytes = 16 * 1024 * 1024;

    private static void ValidateDataPlane(CloudActivityQuery query)
    {
        if (query.Destination is null || query.Container is null || query.Prefix is null)
            throw new ArgumentException("A client-provisioned destination is required.");
        var aws = query.SourceKind is CloudActivitySource.AwsCloudTrailLake or CloudActivitySource.AwsS3Archive;
        if (aws && query.Provider != CloudProvider.Aws || !aws && query.Provider != CloudProvider.Azure)
            throw new ArgumentException("Activity source does not match the bound cloud provider.");
        if (query.SourceKind == CloudActivitySource.AwsCloudTrailLake)
        {
            if (!Guid.TryParseExact(query.Destination, "D", out _))
                throw new ArgumentException("CloudTrail Lake requires the existing event data store UUID (existing customers only).");
        }
        else if (query.SourceKind == CloudActivitySource.AwsS3Archive)
        {
            if (query.Destination.Length is < 3 or > 63 || query.Destination.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.'))
                || !query.Prefix.Contains($"AWSLogs/{query.AccountId}/CloudTrail/", StringComparison.Ordinal))
                throw new ArgumentException("Choose a client bucket and account-specific AWSLogs/<account>/CloudTrail/ prefix.");
        }
        else
        {
            var expected = query.SourceKind == CloudActivitySource.AzureLogAnalytics ? "Microsoft.OperationalInsights/workspaces" : "Microsoft.Storage/storageAccounts";
            var parts = query.Destination.Split('/');
            if (parts.Length != 9 || parts[0] != "" || !parts[1].Equals("subscriptions", StringComparison.OrdinalIgnoreCase)
                || !parts[2].Equals(query.AccountId, StringComparison.OrdinalIgnoreCase)
                || !parts[3].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase)
                || !parts[5].Equals("providers", StringComparison.OrdinalIgnoreCase)
                || !(parts[6] + "/" + parts[7]).Equals(expected, StringComparison.OrdinalIgnoreCase)
                || !SafeArgument(parts[4], 90) || !SafeArgument(parts[8], 63))
                throw new ArgumentException("Destination must be an exact workspace/storage-account ARM resource ID in the bound subscription.");
            if (query.SourceKind == CloudActivitySource.AzureBlobArchive && (!SafeArgument(query.Container, 63)
                || !query.Container.StartsWith("insights-logs-", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(query.Prefix)))
                throw new ArgumentException("Select a client-provisioned insights-logs-* diagnostic archive container.");
        }
        if (query.Prefix.Length > 1024 || query.Prefix.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.' or '='))
            || query.Prefix.Contains("..", StringComparison.Ordinal) || query.Prefix.StartsWith('-'))
            throw new ArgumentException("Archive prefix must be a safe cloud object prefix, not a URL, credential or local path.");
    }

    private async Task<CloudActivitySnapshot> CollectDataPlaneAsync(CloudActivityQuery query, DateTimeOffset start,
        DateTimeOffset end, CancellationToken token)
    {
        if (query.Provider == CloudProvider.Aws)
        {
            using var identity = await RunAsync("aws", AwsArguments(query, ["sts", "get-caller-identity", "--output", "json"]), token);
            if (RequiredText(identity.RootElement, "Account") != query.AccountId)
                throw new InvalidOperationException("AWS CLI identity does not match the bound account.");
        }
        return query.SourceKind switch
        {
            CloudActivitySource.AwsCloudTrailLake => await CollectLakeAsync(query, start, end, token),
            CloudActivitySource.AzureLogAnalytics => await CollectWorkspaceAsync(query, start, end, token),
            _ => await CollectArchiveAsync(query, start, end, token)
        };
    }

    private async Task<CloudActivitySnapshot> CollectLakeAsync(CloudActivityQuery q, DateTimeOffset start, DateTimeOffset end, CancellationToken token)
    {
        using (var store = await RunAsync("aws", AwsArguments(q, ["cloudtrail", "get-event-data-store", "--event-data-store", q.Destination, "--output", "json"]), token))
        {
            var arn = RequiredText(store.RootElement, "EventDataStoreArn");
            if (arn != $"arn:aws:cloudtrail:{q.Region}:{q.AccountId}:eventdatastore/{q.Destination}")
                throw new InvalidOperationException("CloudTrail Lake event data store is outside the selected account/region.");
        }
        var sql = $"SELECT eventID,eventTime,eventSource,eventName,recipientAccountId,userIdentity.arn AS principalArn,errorCode FROM {q.Destination}"
            + $" WHERE eventTime >= '{start.UtcDateTime:yyyy-MM-dd HH:mm:ss}' AND eventTime <= '{end.UtcDateTime:yyyy-MM-dd HH:mm:ss}'"
            + $" AND recipientAccountId = '{q.AccountId}' ORDER BY eventTime DESC LIMIT {MaximumEvents + 1}";
        using var initiated = await RunAsync("aws", AwsArguments(q, ["cloudtrail", "start-query", "--query-statement", sql, "--output", "json"]), token);
        var id = RequiredText(initiated.RootElement, "QueryId");
        if (!Guid.TryParseExact(id, "D", out _)) throw new InvalidOperationException("CloudTrail Lake returned an invalid query identifier.");
        var events = new List<CloudActivityEvent>();
        var provenance = new List<CloudActivityProvenance>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? next = null;
        var sourceCount = 0;
        var truncated = false;
        do
        {
            var args = new List<string> { "cloudtrail", "get-query-results", "--event-data-store", q.Destination, "--query-id", id,
                "--max-query-results", "1000", "--no-paginate", "--output", "json" };
            if (next is not null) args.AddRange(["--next-token", next]);
            using var page = await RunAsync("aws", AwsArguments(q, args), token);
            var status = RequiredText(page.RootElement, "QueryStatus");
            if (status is "QUEUED" or "RUNNING")
            {
                await Task.Delay(TimeSpan.FromSeconds(2), token);
                continue;
            }
            if (status != "FINISHED") throw new InvalidOperationException($"CloudTrail Lake query {id} ended with {status}. No snapshot published.");
            var rows = RequiredArray(page.RootElement, "QueryResultRows");
            foreach (var row in rows.EnumerateArray())
            {
                if (++sourceCount > MaximumEvents) { truncated = true; break; }
                var values = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var column in row.EnumerateArray())
                    foreach (var value in column.EnumerateObject())
                        values.Add(value.Name, value.Value.ValueKind == JsonValueKind.Null ? null : value.Value.GetString());
                var normalized = values.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal);
                normalized["userIdentity"] = new { arn = values.GetValueOrDefault("principalArn") };
                using var entry = JsonDocument.Parse(JsonSerializer.Serialize(normalized));
                AddAwsAudit(events, entry.RootElement, q.AccountId, start, end);
            }
            provenance.Add(Digest($"cloudtrail-lake:{q.Destination}/query/{id}/page/{provenance.Count + 1}", page.RootElement.GetRawText(), rows.GetArrayLength()));
            next = Text(page.RootElement, "NextToken");
            if (next is not null && !seen.Add(next)) throw new InvalidOperationException("CloudTrail Lake repeated a continuation token.");
        } while (!truncated && (next is not null || sourceCount == 0 && provenance.Count == 0));
        return Snapshot(q, start, end, "CloudTrail Lake", "Partial: selected event data store, enabled selectors and retained history only. Queries incur client charges. Resource/grant attribution is unavailable for this scalar-column query.", truncated, events, provenance);
    }

    private async Task<CloudActivitySnapshot> CollectWorkspaceAsync(CloudActivityQuery q, DateTimeOffset start, DateTimeOffset end, CancellationToken token)
    {
        using var workspace = await RunAsync("az", ["monitor", "log-analytics", "workspace", "show", "--ids", q.Destination, "--subscription", q.AccountId, "--output", "json"], token);
        if (!RequiredText(workspace.RootElement, "id").Equals(q.Destination, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParse(Text(workspace.RootElement, "customerId"), out var workspaceId))
            throw new InvalidOperationException("Azure workspace identity does not match the selected destination.");
        var kql = "union isfuzzy=true StorageBlobLogs, StorageFileLogs, StorageQueueLogs, StorageTableLogs, AzureDiagnostics"
            + $" | where TimeGenerated between (datetime({Iso(start)}) .. datetime({Iso(end)}))"
            + $" | where tolower(_ResourceId) startswith '/subscriptions/{q.AccountId.ToLowerInvariant()}/'"
            + " | project TimeGenerated, ResourceId=tostring(_ResourceId), Operation=tostring(OperationName),"
            + " Principal=coalesce(tostring(column_ifexists('RequesterObjectId','')), tostring(column_ifexists('identity_claim_oid_g',''))),"
            + " Outcome=coalesce(tostring(column_ifexists('StatusCode','')), tostring(column_ifexists('ResultType','')))"
            + $" | order by TimeGenerated desc | take {MaximumEvents + 1}";
        using var result = await RunAsync("az", ["rest", "--method", "post", "--url", $"https://api.loganalytics.io/v1/workspaces/{workspaceId:D}/query",
            "--resource", "https://api.loganalytics.io", "--body", JsonSerializer.Serialize(new { query = kql, timespan = $"{Iso(start)}/{Iso(end)}" }), "--output", "json"], token);
        if (result.RootElement.TryGetProperty("error", out _)) throw new InvalidOperationException("Azure Log Analytics returned a query error or partial failure; no snapshot published.");
        var tables = RequiredArray(result.RootElement, "tables");
        if (tables.GetArrayLength() != 1) throw new InvalidOperationException("Unexpected Log Analytics result tables.");
        var table = tables[0];
        var names = RequiredArray(table, "columns").EnumerateArray().Select(column => RequiredText(column, "name")).ToArray();
        if (!names.SequenceEqual(new[] { "TimeGenerated", "ResourceId", "Operation", "Principal", "Outcome" }))
            throw new InvalidOperationException("Unexpected Log Analytics result columns.");
        var rows = RequiredArray(table, "rows");
        var events = new List<CloudActivityEvent>();
        foreach (var row in rows.EnumerateArray().Take(MaximumEvents))
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != names.Length) throw new InvalidOperationException("Invalid Log Analytics row.");
            var fields = names.Select((name, index) => (name, Value: row[index].GetString())).ToDictionary(pair => pair.name, pair => pair.Value);
            using var entry = JsonDocument.Parse(JsonSerializer.Serialize(fields));
            AddAzureAudit(events, entry.RootElement, q.AccountId, start, end, workspaceRow: true);
        }
        return Snapshot(q, start, end, "Azure Log Analytics data-plane", "Partial: selected workspace Storage*Logs and AzureDiagnostics only. Diagnostic settings, unsupported service schemas, identity gaps and retention require review. Operation names are not IAM permissions.", rows.GetArrayLength() > MaximumEvents,
            events, [Digest(q.Destination, result.RootElement.GetRawText(), rows.GetArrayLength())]);
    }

    private async Task<CloudActivitySnapshot> CollectArchiveAsync(CloudActivityQuery q, DateTimeOffset start, DateTimeOffset end, CancellationToken token)
    {
        var aws = q.SourceKind == CloudActivitySource.AwsS3Archive;
        var accountName = aws ? q.Destination : q.Destination.Split('/')[8];
        if (!aws)
        {
            using var account = await RunAsync("az", ["storage", "account", "show", "--ids", q.Destination, "--subscription", q.AccountId, "--output", "json"], token);
            if (!RequiredText(account.RootElement, "id").Equals(q.Destination, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Azure storage destination does not match the selected bound subscription.");
        }
        var files = new List<(string Name, long Size, string Version)>();
        string? next = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var truncated = false;
        var pageCount = 0;
        do
        {
            if (++pageCount > 64) { truncated = true; break; }
            IReadOnlyList<string> args;
            if (aws)
            {
                var list = new List<string> { "s3api", "list-objects-v2", "--bucket", q.Destination, "--prefix", q.Prefix,
                    "--expected-bucket-owner", q.AccountId, "--max-keys", "1000", "--no-paginate", "--output", "json" };
                if (next is not null) list.AddRange(["--continuation-token", next]);
                args = AwsArguments(q, list);
            }
            else
            {
                var list = new List<string> { "storage", "blob", "list", "--account-name", accountName, "--container-name", q.Container, "--prefix", q.Prefix,
                    "--auth-mode", "login", "--num-results", "1000", "--show-next-marker", "--output", "json" };
                if (next is not null) list.AddRange(["--marker", next]);
                args = list;
            }
            using var page = await RunAsync(aws ? "aws" : "az", args, token);
            var entries = aws ? page.RootElement.TryGetProperty("Contents", out var contents) ? contents : default : page.RootElement;
            if (entries.ValueKind is not (JsonValueKind.Array or JsonValueKind.Undefined)) throw new InvalidOperationException("Invalid archive listing.");
            string? azureMarker = null;
            var markerSeen = false;
            if (entries.ValueKind == JsonValueKind.Array)
                foreach (var item in entries.EnumerateArray())
                {
                    if (!aws && item.TryGetProperty("nextMarker", out var marker))
                    {
                        if (markerSeen || marker.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
                            throw new InvalidOperationException("Archive returned invalid continuation evidence.");
                        markerSeen = true;
                        azureMarker = marker.ValueKind == JsonValueKind.String ? marker.GetString() : null;
                        if (azureMarker?.Length == 0) azureMarker = null;
                        continue;
                    }
                    var name = RequiredText(item, aws ? "Key" : "name");
                    if (!name.StartsWith(q.Prefix, StringComparison.Ordinal) || name.Length > 2048 || name.Any(char.IsControl))
                        throw new InvalidOperationException("Archive returned an invalid or out-of-prefix object.");
                    if (aws && !name.EndsWith(".json.gz", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!aws && !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                    var properties = aws ? item : item.GetProperty("properties");
                    var size = properties.GetProperty(aws ? "Size" : "contentLength").GetInt64();
                    var version = RequiredText(properties, aws ? "ETag" : "etag");
                    if (size < 0 || size > MaximumArchiveBytes) throw new InvalidOperationException("Archive object exceeds the 8 MiB download limit. Narrow the source prefix.");
                    files.Add((name, size, version));
                    if (files.Count > MaximumArchiveFiles) { truncated = true; break; }
                }
            next = aws ? Text(page.RootElement, "NextContinuationToken") : azureMarker;
            if (aws && page.RootElement.TryGetProperty("IsTruncated", out var more) && more.ValueKind == JsonValueKind.True && next is null)
                throw new InvalidOperationException("Archive pagination evidence is incomplete.");
            if (next is not null && (next.Length > 4096 || next.Any(char.IsControl) || !seen.Add(next)))
                throw new InvalidOperationException("Archive returned an invalid or repeated continuation token.");
        } while (next is not null && !truncated);
        var events = new List<CloudActivityEvent>();
        var provenance = new List<CloudActivityProvenance>();
        var decodedTotal = 0;
        foreach (var file in files.Take(MaximumArchiveFiles))
        {
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(Path.GetTempPath(), $"yask-audit-{Guid.NewGuid():N}.json");
            try
            {
                IReadOnlyList<string> args = aws
                    ? AwsArguments(q, ["s3api", "get-object", "--bucket", q.Destination, "--key", file.Name, "--if-match", file.Version,
                        "--expected-bucket-owner", q.AccountId, path, "--output", "json"])
                    : ["storage", "blob", "download", "--account-name", accountName, "--container-name", q.Container, "--name", file.Name,
                        "--auth-mode", "login", "--if-match", file.Version, "--file", path, "--output", "json"];
                using var downloaded = await RunAsync(aws ? "aws" : "az", args, token);
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                if (input.Length != file.Size || input.Length > MaximumArchiveBytes) throw new InvalidOperationException("Downloaded archive size changed or exceeds the limit.");
                using var gzip = aws ? new GZipStream(input, CompressionMode.Decompress, leaveOpen: true) : null;
                var data = await ReadBoundedAsync(gzip is null ? input : gzip, token);
                decodedTotal += data.Length;
                if (decodedTotal > 32 * 1024 * 1024) throw new InvalidOperationException("Archive decoded total exceeds 32 MiB. Narrow the prefix.");
                using var document = JsonDocument.Parse(data);
                var entries = RequiredArray(document.RootElement, aws ? "Records" : "records");
                foreach (var entry in entries.EnumerateArray())
                {
                    if (aws) AddAwsAudit(events, entry, q.AccountId, start, end);
                    else AddAzureAudit(events, entry, q.AccountId, start, end, workspaceRow: false);
                }
                provenance.Add(new(aws ? $"s3://{q.Destination}/{file.Name}" : $"{q.Destination}/{q.Container}/{file.Name}",
                    Convert.ToHexString(SHA256.HashData(data)), entries.GetArrayLength()));
                if (events.Count >= MaximumEvents) { truncated = true; break; }
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
        return Snapshot(q, start, end, aws ? "CloudTrail S3 archive" : "Azure Blob diagnostic archive",
            "Partial: selected client archive prefix only; missing objects/categories, delivery lag, retention and unattributed permissions remain unknown. Maximum 128 files and 64 listing pages; use a date/resource-specific prefix. No source completeness is inferred.", truncated, events, provenance);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + count > MaximumDecodedBytes) throw new InvalidOperationException("Decompressed audit object exceeds 16 MiB.");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
        return output.ToArray();
    }

    private static void AddAwsAudit(List<CloudActivityEvent> events, JsonElement entry, string account, DateTimeOffset start, DateTimeOffset end)
    {
        if (RequiredText(entry, "recipientAccountId") != account) throw new InvalidOperationException("CloudTrail evidence belongs to another account.");
        var time = RequiredTime(entry, "eventTime");
        if (time < start || time > end) return;
        var principal = NestedText(entry, "userIdentity", "arn") ?? NestedText(entry, "userIdentity", "principalId") ?? "Unattributed";
        var action = $"{RequiredText(entry, "eventSource")}/{RequiredText(entry, "eventName")}";
        var resources = entry.TryGetProperty("resources", out var resourceArray) && resourceArray.ValueKind == JsonValueKind.Array
            ? resourceArray.EnumerateArray().Select(resource => Text(resource, "ARN")).OfType<string>().Distinct(StringComparer.Ordinal).ToArray() : [];
        var error = Text(entry, "errorCode");
        foreach (var resource in resources.Length == 0 ? new string?[] { null } : resources)
        {
            Add(events, RequiredText(entry, "eventID"), principal, action, null, resource, time, string.IsNullOrEmpty(error), start, end);
            events[^1] = events[^1] with { SourceIp = Text(entry, "sourceIPAddress") };
        }
    }

    private static void AddAzureAudit(List<CloudActivityEvent> events, JsonElement entry, string account, DateTimeOffset start, DateTimeOffset end, bool workspaceRow)
    {
        var resource = RequiredText(entry, workspaceRow ? "ResourceId" : "resourceId");
        if (!resource.StartsWith($"/subscriptions/{account}/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Azure data-plane evidence belongs to another subscription.");
        var time = RequiredTime(entry, workspaceRow ? "TimeGenerated" : "time");
        if (time < start || time > end) return;
        var operation = RequiredText(entry, workspaceRow ? "Operation" : "operationName");
        var principal = workspaceRow ? Text(entry, "Principal")
            : NestedText(entry, "properties", "requesterObjectId") ?? NestedText(entry, "identity", "principalId");
        if (!workspaceRow && string.IsNullOrWhiteSpace(principal)
            && entry.TryGetProperty("identity", out var identity) && identity.ValueKind == JsonValueKind.Object)
            principal = NestedText(identity, "claim", "http://schemas.microsoft.com/identity/claims/objectidentifier");
        var outcome = workspaceRow ? Text(entry, "Outcome") : Text(entry, "resultType") ?? NestedText(entry, "properties", "statusCode");
        if (!workspaceRow && string.IsNullOrWhiteSpace(outcome) && entry.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("statusCode", out var numericStatus)
            && numericStatus.ValueKind == JsonValueKind.Number)
            outcome = numericStatus.GetRawText();
        var known = int.TryParse(outcome, NumberStyles.None, CultureInfo.InvariantCulture, out var code) && code is >= 200 and <= 599
            || outcome is "Success" or "Succeeded" or "Failed" or "Failure";
        var success = known && (code is >= 200 and <= 299 || outcome is "Success" or "Succeeded");
        Add(events, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.GetRawText()))),
            string.IsNullOrWhiteSpace(principal) ? "Unattributed" : principal, operation, null, resource, time, success, start, end);
        events[^1] = events[^1] with { OutcomeKnown = known };
    }

    private static CloudActivityProvenance Digest(string id, string json, int records)
        => new(id, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))), records);

    private static CloudActivitySnapshot Snapshot(CloudActivityQuery q, DateTimeOffset start, DateTimeOffset end, string source,
        string details, bool truncated, List<CloudActivityEvent> events, IReadOnlyList<CloudActivityProvenance> provenance)
        => new(Guid.NewGuid(), q.Provider, q.AccountId, start, end, source, details, truncated,
            events.DistinctBy(item => (item.EventId, item.Principal, item.Action, item.PermissionAction, item.Resource))
                .OrderByDescending(item => item.Timestamp).ToArray())
        { Provenance = provenance, SourceScope = $"{q.Destination}/{q.Container}/{q.Prefix}" };
}
