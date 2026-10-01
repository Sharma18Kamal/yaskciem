using CloudEntitle.Domain.Entities;

namespace CloudEntitle.Compliance.Services;

public sealed class AuditEventService
{
    public AuditEvent Create(string eventType, string description, Guid? userId = null, Guid? organizationId = null, string? ipAddress = null, IReadOnlyDictionary<string, string>? metadata = null)
    {
        var sanitized = (metadata ?? new Dictionary<string, string>()).Where(item => !IsSensitive(item.Key)).ToDictionary(item => item.Key, item => item.Value);
        return new AuditEvent { Id = Guid.NewGuid(), UserId = userId, OrganizationId = organizationId, EventType = eventType, Description = description, Timestamp = DateTime.UtcNow, IpAddress = ipAddress, Metadata = sanitized };
    }

    private static bool IsSensitive(string key) => key.Contains("password", StringComparison.OrdinalIgnoreCase) || key.Contains("token", StringComparison.OrdinalIgnoreCase) || key.Contains("secret", StringComparison.OrdinalIgnoreCase) || key.Contains("private", StringComparison.OrdinalIgnoreCase) || key.Contains("key", StringComparison.OrdinalIgnoreCase);
}
