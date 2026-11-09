namespace CloudEntitle.Infrastructure.Persistence;

public sealed class RemediationPlanRecord
{
    public Guid Id { get; set; }
    public Guid? OrganizationId { get; set; }
    public int Provider { get; set; }
    public int Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string Payload { get; set; } = "";
}

public sealed class RemediationActionRecord
{
    public Guid PlanId { get; set; }
    public Guid Id { get; set; }
    public int Position { get; set; }
    public string Payload { get; set; } = "";
}

public sealed class RemediationApprovalRecord
{
    public Guid PlanId { get; set; }
    public string Payload { get; set; } = "";
}

public sealed class RemediationExecutionRecord
{
    public Guid PlanId { get; set; }
    public Guid Id { get; set; }
    public string Payload { get; set; } = "";
}

public sealed class RemediationRollbackRecord
{
    public Guid PlanId { get; set; }
    public Guid Id { get; set; }
    public int Position { get; set; }
    public string Payload { get; set; } = "";
}

public sealed class RemediationAuditRecord
{
    public Guid Id { get; set; }
    public Guid PlanId { get; set; }
    public DateTime Timestamp { get; set; }
    public string Payload { get; set; } = "";
}
