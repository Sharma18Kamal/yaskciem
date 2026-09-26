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
    public DbSet<ActivityRecord> ActivityRecords => Set<ActivityRecord>();
    public DbSet<RiskFindingModel> NormalizedRiskFindings => Set<RiskFindingModel>();
    public DbSet<RiskRuleModel> NormalizedRiskRules => Set<RiskRuleModel>();
    public DbSet<RecommendationModel> NormalizedRecommendations => Set<RecommendationModel>();
    public DbSet<Scan> Scans => Set<Scan>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

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
