using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CloudEntitle.Integration.Contracts;

public sealed record SharedMonitoringEntity(string Key, string StateHash, SharedAlertSeverity Severity)
{
    public bool Resolved { get; init; }
}

public static class SharedMonitoringProjection
{
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static IReadOnlyList<SharedMonitoringEntity> Project(IEnumerable<SharedCiemRecordDto> records, string kind)
    {
        var entities = new Dictionary<string, SharedMonitoringEntity>(StringComparer.Ordinal);
        foreach (var record in records.Where(record => record.Kind == kind))
        {
            using var document = JsonDocument.Parse(record.PayloadJson);
            var root = document.RootElement;
            string Read(string name)
            {
                foreach (var property in root.EnumerateObject())
                    if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? "" : property.Value.ToString();
                return "";
            }
            if (root.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Monitoring records must contain JSON objects.");
            var access = kind == "effective-access";
            var fields = access
                ? new[] { Read("principal"), Read("role"), Read("scope"), Read("source") }
                : new[] { Read("ruleId"), Read("principal"), Read("resource"), Read("permission"), Read("title") };
            if (string.IsNullOrWhiteSpace(fields[0]) || string.IsNullOrWhiteSpace(fields[access ? 1 : 4]))
                throw new ArgumentException("Monitoring records require stable natural identifiers.");
            var key = Hash(JsonSerializer.Serialize(fields));
            if (kind == "resource-finding")
            {
                var fingerprint = Read("fingerprint");
                if (string.IsNullOrWhiteSpace(fingerprint))
                    throw new ArgumentException("Resource monitoring findings require a stable fingerprint.");
                key = Hash(fingerprint);
            }
            SharedAlertSeverity severity;
            if (access)
            {
                if (!bool.TryParse(Read("isPrivileged"), out var privileged))
                    throw new ArgumentException("Monitoring effective access requires an explicit privilege classification.");
                severity = privileged ? SharedAlertSeverity.High : SharedAlertSeverity.Informational;
            }
            else
            {
                var value = Read("severity");
                // Legacy scan payloads serialize the domain RiskSeverity enum numerically (Critical=0).
                severity = int.TryParse(value, out var numeric) && numeric is >= 0 and <= 4
                    ? (SharedAlertSeverity)(4 - numeric)
                    : Enum.TryParse<SharedAlertSeverity>(value, true, out var parsed) && Enum.IsDefined(parsed)
                        ? parsed : throw new ArgumentException("Monitoring findings require a valid severity.");
            }
            var state = access
                ? new[] { severity.ToString(), Read("assignmentType") }
                : new[] { severity.ToString(), Read("status"), Read("evaluationState"), Read("recommendation") };
            var entity = new SharedMonitoringEntity(key, Hash(JsonSerializer.Serialize(state)), severity)
            {
                Resolved = !access && (Read("status").Equals("Resolved", StringComparison.OrdinalIgnoreCase) || Read("status") == "1")
            };
            if (entities.TryGetValue(key, out var existing) && existing != entity)
                throw new ArgumentException("Monitoring records contain ambiguous natural identifiers.");
            entities[key] = entity;
        }
        return entities.Values.OrderBy(entity => entity.Key, StringComparer.Ordinal).ToArray();
    }
}
