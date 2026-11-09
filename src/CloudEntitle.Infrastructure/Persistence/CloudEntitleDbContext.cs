namespace CloudEntitle.Infrastructure.Persistence;

using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using Microsoft.EntityFrameworkCore;

public class CloudEntitleDbContext : DbContext
{
    public CloudEntitleDbContext(DbContextOptions<CloudEntitleDbContext> options)
        : base(options)
    {
    }

    public DbSet<CloudAccount> CloudAccounts => Set<CloudAccount>();
    public DbSet<CloudSubscription> CloudSubscriptions => Set<CloudSubscription>();
    public DbSet<CloudProject> CloudProjects => Set<CloudProject>();
    public DbSet<CloudResource> CloudResources => Set<CloudResource>();
    public DbSet<CloudPrincipal> CloudPrincipals => Set<CloudPrincipal>();
    public DbSet<CloudGroup> CloudGroups => Set<CloudGroup>();
    public DbSet<CloudServiceAccount> CloudServiceAccounts => Set<CloudServiceAccount>();
    public DbSet<CloudRole> CloudRoles => Set<CloudRole>();
    public DbSet<CloudPermission> CloudPermissions => Set<CloudPermission>();
    public DbSet<CloudPolicy> CloudPolicies => Set<CloudPolicy>();
    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();
    public DbSet<PermissionAssignment> PermissionAssignments => Set<PermissionAssignment>();
    public DbSet<RiskFinding> RiskFindings => Set<RiskFinding>();
    public DbSet<RiskRule> RiskRules => Set<RiskRule>();
    public DbSet<Recommendation> Recommendations => Set<Recommendation>();
    public DbSet<Entitlement> Entitlements => Set<Entitlement>();
    public DbSet<PolicyAssignment> PolicyAssignments => Set<PolicyAssignment>();
    public DbSet<GroupMembership> GroupMemberships => Set<GroupMembership>();
    public DbSet<EffectiveAccess> EffectiveAccesses => Set<EffectiveAccess>();
    public DbSet<AccessPath> AccessPaths => Set<AccessPath>();
    public DbSet<AccessPathNode> AccessPathNodes => Set<AccessPathNode>();
    public DbSet<AccessPathEdge> AccessPathEdges => Set<AccessPathEdge>();
    public DbSet<ActivityRecord> ActivityRecords => Set<ActivityRecord>();
    public DbSet<CloudActivityEvent> CloudActivityEvents => Set<CloudActivityEvent>();
    public DbSet<PermissionUsageRecord> PermissionUsageRecords => Set<PermissionUsageRecord>();
    public DbSet<PermissionUsageSummary> PermissionUsageSummaries => Set<PermissionUsageSummary>();
    public DbSet<PermissionObservationWindow> PermissionObservationWindows => Set<PermissionObservationWindow>();
    public DbSet<UsageAnalyticsFinding> UsageAnalyticsFindings => Set<UsageAnalyticsFinding>();
    public DbSet<UsageRecommendation> UsageRecommendations => Set<UsageRecommendation>();
    public DbSet<ToxicCombinationRule> ToxicCombinationRules => Set<ToxicCombinationRule>();
    public DbSet<ToxicCombinationFinding> ToxicCombinationFindings => Set<ToxicCombinationFinding>();
    public DbSet<ToxicCombinationEvidence> ToxicCombinationEvidence => Set<ToxicCombinationEvidence>();
    public DbSet<PermissionCombination> PermissionCombinations => Set<PermissionCombination>();
    public DbSet<ToxicCombinationRecommendation> ToxicCombinationRecommendations => Set<ToxicCombinationRecommendation>();
    public DbSet<ToxicCombinationTrustPathEvidence> ToxicCombinationTrustPathEvidence => Set<ToxicCombinationTrustPathEvidence>();
    public DbSet<RiskFindingModel> NormalizedRiskFindings => Set<RiskFindingModel>();
    public DbSet<RiskRuleModel> NormalizedRiskRules => Set<RiskRuleModel>();
    public DbSet<RecommendationModel> NormalizedRecommendations => Set<RecommendationModel>();
    public DbSet<Scan> Scans => Set<Scan>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<RemediationPlanRecord> RemediationPlans => Set<RemediationPlanRecord>();
    public DbSet<RemediationActionRecord> RemediationActions => Set<RemediationActionRecord>();
    public DbSet<RemediationApprovalRecord> RemediationApprovals => Set<RemediationApprovalRecord>();
    public DbSet<RemediationExecutionRecord> RemediationExecutions => Set<RemediationExecutionRecord>();
    public DbSet<RemediationRollbackRecord> RemediationRollbacks => Set<RemediationRollbackRecord>();
    public DbSet<RemediationAuditRecord> RemediationAuditRecords => Set<RemediationAuditRecord>();
    public DbSet<ReportExecutionHistoryRecord> ReportExecutionHistory => Set<ReportExecutionHistoryRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<RemediationPlanRecord>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.OrganizationId, x.Status });
            entity.HasIndex(x => x.CreatedAt);
        });
        modelBuilder.Entity<RemediationActionRecord>(entity =>
        {
            entity.HasKey(x => new { x.PlanId, x.Id });
            entity.HasIndex(x => new { x.PlanId, x.Position });
            entity.HasOne<RemediationPlanRecord>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<RemediationApprovalRecord>(entity =>
        {
            entity.HasKey(x => x.PlanId);
            entity.HasOne<RemediationPlanRecord>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<RemediationExecutionRecord>(entity =>
        {
            entity.HasKey(x => new { x.PlanId, x.Id });
            entity.HasOne<RemediationPlanRecord>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<RemediationRollbackRecord>(entity =>
        {
            entity.HasKey(x => new { x.PlanId, x.Id });
            entity.HasIndex(x => new { x.PlanId, x.Position });
            entity.HasOne<RemediationPlanRecord>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<RemediationAuditRecord>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.PlanId, x.Timestamp });
            entity.HasOne<RemediationPlanRecord>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<ReportExecutionHistoryRecord>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Category).IsRequired().HasMaxLength(80);
            entity.Property(x => x.Format).IsRequired().HasMaxLength(20);
            entity.Property(x => x.OutputFileName).IsRequired().HasMaxLength(260);
            entity.Property(x => x.Status).IsRequired().HasMaxLength(20);
            entity.HasIndex(x => x.GeneratedAtUtc);
            entity.HasIndex(x => new { x.Category, x.GeneratedAtUtc });
        });

        modelBuilder.Entity<CloudAccount>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired().HasMaxLength(200);
            entity.Property(x => x.SubscriptionId).HasMaxLength(200);
            entity.Property(x => x.ExternalAccountId).HasMaxLength(200);
            entity.Property(x => x.TenantId).HasMaxLength(200);
            entity.Property(x => x.ProjectId).HasMaxLength(200);
        });

        modelBuilder.Entity<CloudPrincipal>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired().HasMaxLength(200);
            entity.Property(x => x.Email).HasMaxLength(300);
            entity.Property(x => x.PrincipalId).HasMaxLength(200);
            entity.Property(x => x.LastActivity).HasMaxLength(200);
        });

        modelBuilder.Entity<CloudRole>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired().HasMaxLength(200);
            entity.Property(x => x.RoleId).IsRequired().HasMaxLength(200);
            entity.Property(x => x.Description).HasMaxLength(500);
            entity.Property(x => x.Scope).HasMaxLength(500);
        });

        modelBuilder.Entity<CloudPermission>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired().HasMaxLength(200);
            entity.Property(x => x.Action).HasMaxLength(300);
            entity.Property(x => x.Resource).HasMaxLength(500);
            entity.Property(x => x.Scope).HasMaxLength(500);
            entity.Property(x => x.Source).HasMaxLength(200);
        });

        modelBuilder.Entity<RiskFinding>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Title).IsRequired().HasMaxLength(250);
            entity.Property(x => x.Principal).HasMaxLength(250);
            entity.Property(x => x.Resource).HasMaxLength(500);
            entity.Property(x => x.Permission).HasMaxLength(250);
            entity.Property(x => x.Recommendation).HasMaxLength(1000);
            entity.Property(x => x.Evidence).HasMaxLength(2000);
        });

        modelBuilder.Entity<RiskRule>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired().HasMaxLength(250);
            entity.Property(x => x.Description).HasMaxLength(1000);
            entity.Property(x => x.RuleExpression).IsRequired().HasMaxLength(500);
        });

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CloudEntitleDbContext).Assembly);
    }
}
