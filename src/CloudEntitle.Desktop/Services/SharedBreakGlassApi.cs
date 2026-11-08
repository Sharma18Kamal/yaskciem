using CloudEntitle.Integration.Contracts;

namespace CloudEntitle.Desktop.Services;

public interface ISharedBreakGlassApi
{
    Task<IReadOnlyList<BreakGlassReport>> GetBreakGlassReportsAsync(Guid organizationId, CancellationToken token = default);
    Task<BreakGlassReport> ReportBreakGlassAsync(Guid organizationId, CreateBreakGlassReportRequest request, CancellationToken token = default);
    Task<BreakGlassReport> ReviewBreakGlassAsync(Guid organizationId, Guid reportId, ReviewBreakGlassReportRequest request, CancellationToken token = default);
}
