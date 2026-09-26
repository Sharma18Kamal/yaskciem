using System.Text.Json;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CloudEntitle.Infrastructure.Persistence.Configurations;

internal static class PersistenceConversions
{
    public static ValueConverter<Dictionary<string, string>, string> TagsConverter { get; } =
        new(value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
            value => JsonSerializer.Deserialize<Dictionary<string, string>>(value, (JsonSerializerOptions?)null) ?? new());

    public static ValueComparer<Dictionary<string, string>> TagsComparer { get; } =
        new((left, right) => JsonSerializer.Serialize(left, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(right, (JsonSerializerOptions?)null),
            value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null).GetHashCode(),
            value => new Dictionary<string, string>(value, StringComparer.OrdinalIgnoreCase));

    public static ValueConverter<IReadOnlyDictionary<string, string>, string> MetadataConverter { get; } =
        new(value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
            value => JsonSerializer.Deserialize<Dictionary<string, string>>(value, (JsonSerializerOptions?)null) ?? new());
}

public sealed class CloudResourceConfiguration : IEntityTypeConfiguration<CloudResource>
{
    public void Configure(EntityTypeBuilder<CloudResource> builder)
    {
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Name).IsRequired().HasMaxLength(300);
        builder.Property(entity => entity.Type).HasMaxLength(200);
        builder.Property(entity => entity.ResourceId).HasMaxLength(1000);
        builder.Property(entity => entity.Tags).HasConversion(PersistenceConversions.TagsConverter).Metadata.SetValueComparer(PersistenceConversions.TagsComparer);
        builder.HasIndex(entity => new { entity.AccountId, entity.ResourceId });
        builder.HasIndex(entity => new { entity.AccountId, entity.Type });
    }
}

public sealed class CloudAccountConfiguration : IEntityTypeConfiguration<CloudAccount>
{
    public void Configure(EntityTypeBuilder<CloudAccount> builder)
    {
        builder.HasIndex(entity => new { entity.CloudProvider, entity.ExternalAccountId });
        builder.HasIndex(entity => new { entity.CloudProvider, entity.SubscriptionId });
        builder.HasIndex(entity => entity.ProjectId);
        builder.HasIndex(entity => entity.AccountType);
        builder.HasIndex(entity => entity.ParentAccountId);
    }
}

public sealed class CloudPrincipalConfiguration : IEntityTypeConfiguration<CloudPrincipal>
{
    public void Configure(EntityTypeBuilder<CloudPrincipal> builder)
    {
        builder.HasIndex(entity => new { entity.AccountId, entity.PrincipalId });
        builder.HasIndex(entity => new { entity.AccountId, entity.IdentityType, entity.IsPrivileged });
        builder.HasIndex(entity => new { entity.Email, entity.IsExternal });
    }
}

public sealed class CloudGroupConfiguration : IEntityTypeConfiguration<CloudGroup>
{
    public void Configure(EntityTypeBuilder<CloudGroup> builder)
    {
        builder.Property(entity => entity.MemberPrincipalIds).HasConversion(
            values => JsonSerializer.Serialize(values, (JsonSerializerOptions?)null),
            value => JsonSerializer.Deserialize<List<Guid>>(value, (JsonSerializerOptions?)null) ?? new());
        builder.HasIndex(entity => new { entity.AccountId, entity.GroupId });
    }
}

public sealed class CloudRoleConfiguration : IEntityTypeConfiguration<CloudRole>
{
    public void Configure(EntityTypeBuilder<CloudRole> builder)
    {
        builder.HasIndex(entity => new { entity.AccountId, entity.RoleId });
        builder.HasIndex(entity => new { entity.AccountId, entity.IsPrivileged });
    }
}

public sealed class CloudPolicyConfiguration : IEntityTypeConfiguration<CloudPolicy>
{
    public void Configure(EntityTypeBuilder<CloudPolicy> builder)
    {
        builder.Property(entity => entity.Document).HasColumnType("TEXT");
        builder.HasIndex(entity => new { entity.AccountId, entity.ExternalId });
        builder.HasIndex(entity => new { entity.AccountId, entity.PolicyType });
    }
}

public sealed class CloudPermissionConfiguration : IEntityTypeConfiguration<CloudPermission>
{
    public void Configure(EntityTypeBuilder<CloudPermission> builder)
    {
        builder.HasIndex(entity => new { entity.AccountId, entity.Action });
        builder.HasIndex(entity => new { entity.AccountId, entity.Resource });
    }
}

public sealed class EntitlementConfiguration : IEntityTypeConfiguration<Entitlement>
{
    public void Configure(EntityTypeBuilder<Entitlement> builder)
    {
        builder.HasKey(entity => entity.Id);
        builder.HasIndex(entity => new { entity.PrincipalId, entity.CloudAccountId });
        builder.HasIndex(entity => new { entity.PermissionId, entity.ResourceId, entity.Scope });
        builder.HasOne<CloudPrincipal>().WithMany().HasForeignKey(entity => entity.PrincipalId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CloudRole>().WithMany().HasForeignKey(entity => entity.RoleId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CloudPolicy>().WithMany().HasForeignKey(entity => entity.PolicyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CloudPermission>().WithMany().HasForeignKey(entity => entity.PermissionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CloudAccount>().WithMany().HasForeignKey(entity => entity.CloudAccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PolicyAssignmentConfiguration : IEntityTypeConfiguration<PolicyAssignment>
{
    public void Configure(EntityTypeBuilder<PolicyAssignment> builder)
    {
        builder.HasKey(entity => entity.Id);
        builder.HasIndex(entity => new { entity.PrincipalId, entity.PolicyId, entity.CloudAccountId });
        builder.HasOne<CloudPrincipal>().WithMany().HasForeignKey(entity => entity.PrincipalId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CloudPolicy>().WithMany().HasForeignKey(entity => entity.PolicyId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class GroupMembershipConfiguration : IEntityTypeConfiguration<GroupMembership>
{
    public void Configure(EntityTypeBuilder<GroupMembership> builder)
    {
        builder.HasKey(entity => entity.Id);
        builder.HasIndex(entity => new { entity.GroupId, entity.PrincipalId });
        builder.HasOne<CloudGroup>().WithMany().HasForeignKey(entity => entity.GroupId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CloudPrincipal>().WithMany().HasForeignKey(entity => entity.PrincipalId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class EffectiveAccessConfiguration : IEntityTypeConfiguration<EffectiveAccess>
{
    public void Configure(EntityTypeBuilder<EffectiveAccess> builder)
    {
        builder.HasKey(entity => entity.Id);
        builder.HasIndex(entity => new { entity.PrincipalId, entity.CloudAccountId, entity.ResourceId });
        builder.HasIndex(entity => new { entity.PermissionId, entity.IsPrivileged, entity.Decision });
        builder.HasIndex(entity => new { entity.CloudAccountId, entity.EvaluationStatus, entity.CalculatedAt });
        builder.Property(entity => entity.AccessSource).HasMaxLength(200);
        builder.Property(entity => entity.Evidence).HasColumnType("TEXT");
        builder.Property(entity => entity.EvaluationDetails).HasColumnType("TEXT");
        builder.HasOne<CloudPrincipal>().WithMany().HasForeignKey(entity => entity.PrincipalId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CloudPermission>().WithMany().HasForeignKey(entity => entity.PermissionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CloudResource>().WithMany().HasForeignKey(entity => entity.ResourceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CloudRole>().WithMany().HasForeignKey(entity => entity.RoleId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CloudPolicy>().WithMany().HasForeignKey(entity => entity.PolicyId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class AccessPathConfiguration : IEntityTypeConfiguration<AccessPath>
{
    public void Configure(EntityTypeBuilder<AccessPath> builder)
    {
        builder.HasKey(entity => entity.Id);
        builder.HasMany(entity => entity.Nodes).WithOne().HasForeignKey(entity => entity.AccessPathId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class AccessPathNodeConfiguration : IEntityTypeConfiguration<AccessPathNode>
{
    public void Configure(EntityTypeBuilder<AccessPathNode> builder)
    {
        builder.HasKey(entity => entity.Id);
        builder.HasIndex(entity => new { entity.AccessPathId, entity.Sequence }).IsUnique();
        builder.Property(entity => entity.NodeType).IsRequired().HasMaxLength(100);
        builder.Property(entity => entity.Name).IsRequired().HasMaxLength(300);
    }
}

public sealed class ActivityRecordConfiguration : IEntityTypeConfiguration<ActivityRecord>
{
    public void Configure(EntityTypeBuilder<ActivityRecord> builder)
    {
        builder.HasKey(entity => entity.Id);
        builder.HasIndex(entity => new { entity.PrincipalId, entity.CloudAccountId, entity.Timestamp });
        builder.HasIndex(entity => entity.Action);
        builder.HasOne<CloudPrincipal>().WithMany().HasForeignKey(entity => entity.PrincipalId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class NormalizedRiskFindingConfiguration : IEntityTypeConfiguration<RiskFindingModel>
{
    public void Configure(EntityTypeBuilder<RiskFindingModel> builder)
    {
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Evidence).HasConversion(
            value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
            value => JsonSerializer.Deserialize<CloudEntitle.Domain.ValueObjects.RiskEvidence>(value, (JsonSerializerOptions?)null)
                ?? new CloudEntitle.Domain.ValueObjects.RiskEvidence("Evidence unavailable.", new Dictionary<string, string>()));
        builder.HasIndex(entity => new { entity.RuleId, entity.CloudAccountId, entity.Severity, entity.Status });
    }
}

public sealed class NormalizedRiskRuleConfiguration : IEntityTypeConfiguration<RiskRuleModel>
{
    public void Configure(EntityTypeBuilder<RiskRuleModel> builder)
    {
        builder.HasKey(entity => entity.Id);
        builder.HasIndex(entity => entity.Code).IsUnique();
    }
}

public sealed class NormalizedRecommendationConfiguration : IEntityTypeConfiguration<RecommendationModel>
{
    public void Configure(EntityTypeBuilder<RecommendationModel> builder)
    {
        builder.HasKey(entity => entity.Id);
        builder.HasIndex(entity => new { entity.RiskFindingId, entity.Priority });
        builder.HasOne<RiskFindingModel>().WithMany().HasForeignKey(entity => entity.RiskFindingId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ScanConfiguration : IEntityTypeConfiguration<Scan>
{
    public void Configure(EntityTypeBuilder<Scan> builder)
    {
        builder.HasKey(entity => entity.Id);
        builder.HasIndex(entity => new { entity.CloudAccountId, entity.StartedAt, entity.Status });
        builder.HasOne<CloudAccount>().WithMany().HasForeignKey(entity => entity.CloudAccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Metadata).HasConversion(PersistenceConversions.MetadataConverter);
        builder.HasIndex(entity => new { entity.OrganizationId, entity.Timestamp });
        builder.HasIndex(entity => entity.EventType);
    }
}
