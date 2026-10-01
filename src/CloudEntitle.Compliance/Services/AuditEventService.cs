using CloudEntitle.Domain.Entities;
using CloudEntitle.Security.Services;

namespace CloudEntitle.Compliance.Services;

public sealed class AuditEventService
{
    public AuditEvent Create(string eventType, string description, Guid? userId = null, Guid? organizationId = null, string? ipAddress = null, IReadOnlyDictionary<string, string>? metadata = null)
    {
        var sanitized = SensitiveDataRedactor.RedactMetadata(metadata);
        return new AuditEvent { Id = Guid.NewGuid(), UserId = userId, OrganizationId = organizationId, EventType = eventType, Description = description, Timestamp = DateTime.UtcNow, IpAddress = ipAddress, Metadata = sanitized };
    }

}
