using CloudEntitle.Compliance.Models;
using CloudEntitle.Compliance.Services;
using CloudEntitle.Desktop.ViewModels;
using CloudEntitle.Domain;
using CloudEntitle.IdentityGovernance.Services;
using CloudEntitle.Infrastructure.Persistence;
using CloudEntitle.OrganizationManagement.Models;
using CloudEntitle.OrganizationManagement.Services;
using Microsoft.EntityFrameworkCore;
using DomainEffectiveAccess = CloudEntitle.Domain.Entities.EffectiveAccess;
using DomainAccessPath = CloudEntitle.Domain.Entities.AccessPath;

namespace CloudEntitle.Desktop.Services;

public interface IComplianceExportWorkflow
{
    bool CanExport { get; }
    Task<byte[]> CreateBundleAsync(ComplianceFramework framework, IReadOnlyCollection<ComplianceStandard>? selectedStandards = null,
        CancellationToken cancellationToken = default, IReadOnlyCollection<ComplianceException>? exceptions = null);
    Task<IReadOnlyList<ComplianceAssessmentTrendPoint>> GetHistoryAsync(ComplianceFramework framework, CancellationToken cancellationToken = default);
}

public sealed class ComplianceExportWorkflow(
    IDbContextFactory<CloudEntitleDbContext> contextFactory,
    OrganizationManagementService organizationService,
    AuthenticatedOrganizationContext authenticatedContext,
    ComplianceAssessmentService assessmentService,
    IdentityGovernanceService governanceService,
    ComplianceExportService exportService,
    IComplianceBundleSigner signer,
    IComplianceAssessmentHistoryStore? historyStore = null) : IComplianceExportWorkflow
{
    private readonly IComplianceAssessmentHistoryStore _historyStore = historyStore ?? new LocalComplianceAssessmentHistoryStore();

    public bool CanExport => TryGetAuthorizedOrganization(out _, out _);

    public async Task<byte[]> CreateBundleAsync(ComplianceFramework framework, IReadOnlyCollection<ComplianceStandard>? selectedStandards = null,
        CancellationToken cancellationToken = default, IReadOnlyCollection<ComplianceException>? exceptions = null)
    {
        if (!TryGetAuthorizedOrganization(out var identity, out var organization))
            throw new UnauthorizedAccessException("Sign in as an organization member with report-export permission before exporting compliance evidence.");

        var bindings = organizationService.GetTenantBindings(organization.Id, identity);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var accounts = await context.CloudAccounts.AsNoTracking().ToListAsync(cancellationToken);
        var boundAccounts = accounts.Where(account => bindings.Any(binding =>
            account.ExternalAccountId is not null
            &&
            binding.Provider.ToString().Equals(account.CloudProvider.ToString(), StringComparison.OrdinalIgnoreCase)
            && binding.ExternalTenantId.Equals(account.ExternalAccountId, StringComparison.OrdinalIgnoreCase))).ToArray();
        var accountIds = boundAccounts.Select(account => account.Id).ToArray();

        var principals = await context.CloudPrincipals.AsNoTracking().Where(item => accountIds.Contains(item.AccountId)).ToListAsync(cancellationToken);
        var effectiveAccess = await context.EffectiveAccesses.AsNoTracking().Where(item => accountIds.Contains(item.CloudAccountId)).ToListAsync(cancellationToken);
        var accessPaths = await context.AccessPaths.AsNoTracking().Where(item => accountIds.Contains(item.CloudAccountId)).ToListAsync(cancellationToken);
        var findings = await context.NormalizedRiskFindings.AsNoTracking().Where(item => accountIds.Contains(item.CloudAccountId)).ToListAsync(cancellationToken);
        var activity = await context.ActivityRecords.AsNoTracking().Where(item => accountIds.Contains(item.CloudAccountId)).ToListAsync(cancellationToken);
        var scans = await context.Scans.AsNoTracking().Where(item => accountIds.Contains(item.CloudAccountId)).ToListAsync(cancellationToken);
        var auditEvents = await context.AuditEvents.AsNoTracking().Where(item => item.OrganizationId == organization.Id).ToListAsync(cancellationToken);
        var governance = governanceService.Assess(principals, effectiveAccess, activity);
        var complianceContext = new ComplianceContext(boundAccounts, principals, effectiveAccess, accessPaths, findings, governance, auditEvents)
        {
            Scans = scans,
            Exceptions = exceptions?.ToArray() ?? Array.Empty<ComplianceException>()
        };
        var assessment = assessmentService.Assess(complianceContext, framework, selectedStandards: selectedStandards);
        await _historyStore.SaveAsync(organization.Id, assessment, cancellationToken);
        var latestScan = scans.Where(scan => scan.CompletedAt.HasValue).MaxBy(scan => scan.CompletedAt)?.CompletedAt;
        return exportService.CreateBundle(assessment,
            new ComplianceExportMetadata(organization.Id, organization.Name, organization.EntraTenantId, latestScan), signer);
    }

    public async Task<IReadOnlyList<ComplianceAssessmentTrendPoint>> GetHistoryAsync(ComplianceFramework framework, CancellationToken cancellationToken = default)
    {
        if (!TryGetAuthorizedOrganization(out _, out var organization))
            throw new UnauthorizedAccessException("Sign in as an organization member with report-export permission to view compliance history.");
        return await _historyStore.GetTrendAsync(organization.Id, framework, cancellationToken: cancellationToken);
    }

    private bool TryGetAuthorizedOrganization(out OrganizationIdentity identity, out OrganizationProfile organization)
    {
        identity = authenticatedContext.Identity!;
        organization = null!;
        if (identity is null || authenticatedContext.OrganizationId is not { } organizationId) return false;

        try
        {
            if (!organizationService.Authorize(organizationId, identity, OrganizationAction.ExportReports).Allowed) return false;
            organization = organizationService.GetOrganizations().FirstOrDefault(item => item.Id == organizationId)!;
            return organization is not null;
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
    }
}
