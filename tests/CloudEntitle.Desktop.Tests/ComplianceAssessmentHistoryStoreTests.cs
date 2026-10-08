using System.IO;
using CloudEntitle.Compliance.Models;
using CloudEntitle.Desktop.Services;
using Xunit;

namespace CloudEntitle.Desktop.Tests;

public sealed class ComplianceAssessmentHistoryStoreTests
{
    [Fact]
    public async Task GetTrendAsync_ShouldIsolateOrganizationAndFrameworkAndOrderByAssessmentTime()
    {
        var directory = Path.Combine(Path.GetTempPath(), "YaskCiemComplianceHistoryTests", Guid.NewGuid().ToString("N"));
        var store = new LocalComplianceAssessmentHistoryStore(Path.Combine(directory, "history.json"));
        var organizationId = Guid.NewGuid();
        var otherOrganizationId = Guid.NewGuid();
        try
        {
            await store.SaveAsync(organizationId, CreateAssessment(ComplianceFramework.CiemBaseline, DateTime.UtcNow.AddHours(-2), passed: 2));
            await store.SaveAsync(organizationId, CreateAssessment(ComplianceFramework.CiemBaseline, DateTime.UtcNow.AddHours(-1), passed: 3));
            await store.SaveAsync(organizationId, CreateAssessment(ComplianceFramework.LeastPrivilege, DateTime.UtcNow, passed: 9));
            await store.SaveAsync(otherOrganizationId, CreateAssessment(ComplianceFramework.CiemBaseline, DateTime.UtcNow, passed: 8));

            var history = await store.GetTrendAsync(organizationId, ComplianceFramework.CiemBaseline);

            Assert.Equal(2, history.Count);
            Assert.True(history[0].AssessedAt < history[1].AssessedAt);
            Assert.Equal(new[] { 2, 3 }, history.Select(point => point.Passed));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static ComplianceAssessment CreateAssessment(ComplianceFramework framework, DateTime assessedAt, int passed)
        => new(Guid.NewGuid(), framework, assessedAt, Array.Empty<ComplianceControl>(), passed, 0, 0, 0);
}
