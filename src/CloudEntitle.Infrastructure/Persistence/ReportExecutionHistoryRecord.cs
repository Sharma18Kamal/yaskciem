namespace CloudEntitle.Infrastructure.Persistence;

public sealed class ReportExecutionHistoryRecord
{
    public Guid Id { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public DateTime GeneratedAtUtc { get; set; }
    public string OutputFileName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}
