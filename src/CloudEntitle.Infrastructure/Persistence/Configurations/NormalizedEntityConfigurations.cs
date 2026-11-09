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

    public static ValueConverter<List<string>, string> StringListConverter { get; } =
        new(value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
            value => JsonSerializer.Deserialize<List<string>>(value, (JsonSerializerOptions?)null) ?? new());

    public static ValueComparer<List<string>> StringListComparer { get; } =
        new((left, right) => JsonSerializer.Serialize(left, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(right, (JsonSerializerOptions?)null),
            value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null).GetHashCode(),
            value => value.ToList());

    public static ValueConverter<List<WorkloadFederationTrust>, string> FederationTrustsConverter { get; } =
        new(value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
            value => JsonSerializer.Deserialize<List<WorkloadFederationTrust>>(value, (JsonSerializerOptions?)null) ?? new());

    public static ValueComparer<List<WorkloadFederationTrust>> FederationTrustsComparer { get; } =
        new((left, right) => JsonSerializer.Serialize(left, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(right, (JsonSerializerOptions?)null),
            value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null).GetHashCode(),
            value => value.ToList());

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
        builder.Property(entity => entity.OwnerIds).HasConversion(PersistenceConversions.StringListConverter)
            .Metadata.SetValueComparer(PersistenceConversions.StringListComparer);
        builder.Property(entity => entity.FederationTrusts).HasConversion(PersistenceConversions.FederationTrustsConverter)
            .Metadata.SetValueComparer(PersistenceConversions.FederationTrustsComparer);
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
        builder.HasMany(entity => entity.Edges).WithOne().HasForeignKey(entity => entity.AccessPathId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(entity => new { entity.Provider, entity.PrincipalId, entity.ResourceId, entity.PermissionId, entity.PathStatus });
        builder.HasIndex(entity => entity.EffectiveAccessId);
        builder.Property(entity => entity.Evidence).HasColumnType("TEXT");
        builder.Property(entity => entity.EngineVersion).HasMaxLength(50);
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
        builder.Property(entity => entity.EvidenceReference).HasMaxLength(500);
        builder.Property(entity => entity.Metadata).HasColumnType("TEXT");
    }
}

public sealed class AccessPathEdgeConfiguration : IEntityTypeConfiguration<AccessPathEdge>
{
    public void Configure(EntityTypeBuilder<AccessPathEdge> builder)
    {
        builder.HasKey(entity => entity.Id);
        builder.HasIndex(entity => new { entity.AccessPathId, entity.FromNodeId, entity.ToNodeId }).IsUnique();
        builder.Property(entity => entity.RelationshipType).IsRequired().HasMaxLength(100);
        builder.Property(entity => entity.EvidenceReference).HasMaxLength(500);
        builder.Property(entity => entity.Metadata).HasColumnType("TEXT");
    }
}

public sealed class ActivityRecordConfiguration : IEntityTypeConfiguration<ActivityRecord>
{
    public void Configure(EntityTypeBuilder<ActivityRecord> builder)
    {
        builder.HasKey(entity => entity.Id);
        builder.HasIndex(entity => new { entity.PrincipalId, entity.CloudAccountId, entity.Timestamp });
        builder.HasIndex(entity => entity.Action);
        builder.Property(entity => entity.PermissionAction).HasMaxLength(500);
        builder.Property(entity => entity.PermissionMappingEvidence).HasMaxLength(500);
        builder.HasIndex(entity => new { entity.CloudAccountId, entity.PrincipalId, entity.PermissionAction, entity.Timestamp });
        builder.HasOne<CloudPrincipal>().WithMany().HasForeignKey(entity => entity.PrincipalId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class CloudActivityEventConfiguration : IEntityTypeConfiguration<CloudActivityEvent>
{
    public void Configure(EntityTypeBuilder<CloudActivityEvent> builder)
    {
        builder.HasKey(item => item.Id);
        builder.HasIndex(item => item.DeduplicationKey).IsUnique();
        builder.HasIndex(item => new { item.CloudAccountId, item.Provider, item.EventTimestamp });
        builder.HasIndex(item => new { item.IdentityId, item.EventTimestamp });
        builder.HasIndex(item => item.SourceEventId);
        builder.HasIndex(item => item.Action);
        builder.HasIndex(item => item.ResourceReference);
        builder.Property(item => item.TenantOrAccountId).IsRequired().HasMaxLength(200);
        builder.Property(item => item.ProviderIdentity).IsRequired().HasMaxLength(512);
        builder.Property(item => item.PrincipalType).IsRequired().HasMaxLength(100);
        builder.Property(item => item.Action).IsRequired().HasMaxLength(500);
        builder.Property(item => item.PermissionAction).HasMaxLength(500);
        builder.Property(item => item.PermissionMappingEvidence).HasMaxLength(500);
        builder.Property(item => item.ResourceReference).HasMaxLength(2048);
        builder.Property(item => item.Outcome).IsRequired().HasMaxLength(32);
        builder.Property(item => item.SourceEventId).IsRequired().HasMaxLength(512);
        builder.Property(item => item.Source).IsRequired().HasMaxLength(256);
        builder.Property(item => item.DeduplicationKey).IsRequired().HasMaxLength(64);
        builder.Property(item => item.RawEvidence).HasColumnType("TEXT");
    }
}

public sealed class PermissionUsageRecordConfiguration : IEntityTypeConfiguration<PermissionUsageRecord>
{
    public void Configure(EntityTypeBuilder<PermissionUsageRecord> builder)
    {
        builder.HasKey(item => item.Id);
        builder.HasIndex(item => new { item.CloudAccountId, item.Provider, item.IdentityId, item.PermissionId, item.ResourceId, item.CalculatedAtUtc });
        builder.HasIndex(item => new { item.PermissionId, item.State90Days });
        builder.Property(item => item.Action).IsRequired().HasMaxLength(500);
        builder.Property(item => item.CorrelationStatus).IsRequired().HasMaxLength(100);
        builder.Property(item => item.EffectiveAccessIds).IsRequired().HasColumnType("TEXT");
        builder.Property(item => item.Evidence).HasColumnType("TEXT");
    }
}

public sealed class PermissionUsageSummaryConfiguration : IEntityTypeConfiguration<PermissionUsageSummary>
{
    public void Configure(EntityTypeBuilder<PermissionUsageSummary> builder)
    {
        builder.HasKey(item => item.Id);
        builder.HasIndex(item => new { item.CloudAccountId, item.Provider, item.WindowDays, item.BucketType, item.BucketStartUtc, item.IdentityId }).IsUnique();
        builder.Property(item => item.BucketType).IsRequired().HasMaxLength(16);
    }
}

public sealed class PermissionObservationWindowConfiguration : IEntityTypeConfiguration<PermissionObservationWindow>
{
    public void Configure(EntityTypeBuilder<PermissionObservationWindow> builder)
    {
        builder.HasKey(item => item.Id);
        builder.HasIndex(item => new { item.CloudAccountId, item.Provider, item.ObservedFromUtc, item.ObservedThroughUtc });
        builder.HasIndex(item => item.SnapshotId).IsUnique();
        builder.Property(item => item.Source).IsRequired().HasMaxLength(256);
        builder.Property(item => item.Status).IsRequired().HasMaxLength(32);
        builder.Property(item => item.Details).IsRequired().HasMaxLength(4000);
        builder.Property(item => item.Provenance).IsRequired().HasColumnType("TEXT");
    }
}

public sealed class UsageAnalyticsFindingConfiguration : IEntityTypeConfiguration<UsageAnalyticsFinding>
{
    public void Configure(EntityTypeBuilder<UsageAnalyticsFinding> builder)
    {
        builder.HasKey(item => item.Id);
        builder.HasIndex(item => new { item.CloudAccountId, item.Provider, item.IdentityId, item.ObservedAtUtc });
        builder.HasIndex(item => new { item.PermissionId, item.Severity });
        builder.HasIndex(item => item.RiskFindingId).IsUnique();
        builder.Property(item => item.Evidence).IsRequired().HasColumnType("TEXT");
    }
}

public sealed class UsageRecommendationConfiguration : IEntityTypeConfiguration<UsageRecommendation>
{
    public void Configure(EntityTypeBuilder<UsageRecommendation> builder)
    {
        builder.HasKey(item => item.Id);
        builder.HasIndex(item => item.RecommendationId).IsUnique();
        builder.HasIndex(item => new { item.CloudAccountId, item.Provider, item.Status, item.CreatedAtUtc });
        builder.Property(item => item.RecommendationType).IsRequired().HasMaxLength(100);
        builder.Property(item => item.Status).IsRequired().HasMaxLength(32);
        builder.Property(item => item.Evidence).IsRequired().HasColumnType("TEXT");
    }
}

public sealed class ToxicCombinationRuleConfiguration : IEntityTypeConfiguration<ToxicCombinationRule>
{
    public void Configure(EntityTypeBuilder<ToxicCombinationRule> builder)
    {
        builder.HasKey(item => item.Id);
        builder.HasIndex(item => item.Key).IsUnique();
        builder.HasIndex(item => new { item.Provider, item.Enabled });
        builder.Property(item => item.Key).IsRequired().HasMaxLength(120);
        builder.Property(item => item.Name).IsRequired().HasMaxLength(240);
        builder.Property(item => item.Description).IsRequired().HasMaxLength(2000);
        builder.Property(item => item.FirstPermissionPattern).IsRequired().HasMaxLength(500);
        builder.Property(item => item.SecondPermissionPattern).IsRequired().HasMaxLength(500);
        builder.Property(item => item.Recommendation).IsRequired().HasMaxLength(2000);
        builder.Property(item => item.Version).IsRequired().HasMaxLength(32);
    }
}

public sealed class ToxicCombinationFindingConfiguration : IEntityTypeConfiguration<ToxicCombinationFinding>
{
    public void Configure(EntityTypeBuilder<ToxicCombinationFinding> builder)
    {
        builder.HasKey(item => item.Id);
        builder.HasIndex(item => item.Fingerprint).IsUnique();
        builder.HasIndex(item => new { item.CloudAccountId, item.Provider, item.PrincipalId, item.Severity });
        builder.HasIndex(item => new { item.RuleId, item.LastEvaluatedAtUtc });
        builder.Property(item => item.RuleKey).IsRequired().HasMaxLength(120);
        builder.Property(item => item.Fingerprint).IsRequired().HasMaxLength(512);
        builder.Property(item => item.Confidence).IsRequired().HasMaxLength(16);
        builder.Property(item => item.ResourceRelation).IsRequired().HasMaxLength(32);
        builder.Property(item => item.EvidenceSummary).IsRequired().HasColumnType("TEXT");
        builder.Property(item => item.Recommendation).IsRequired().HasMaxLength(2000);
        builder.HasOne<ToxicCombinationRule>().WithMany().HasForeignKey(item => item.RuleId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ToxicCombinationEvidenceConfiguration : IEntityTypeConfiguration<ToxicCombinationEvidence>
{
    public void Configure(EntityTypeBuilder<ToxicCombinationEvidence> builder)
    {
        builder.HasKey(item => item.Id);
        builder.HasIndex(item => new { item.FindingId, item.EffectiveAccessId }).IsUnique();
        builder.HasIndex(item => new { item.PermissionId, item.ResourceId });
        builder.Property(item => item.PermissionAction).IsRequired().HasMaxLength(500);
        builder.Property(item => item.Scope).HasMaxLength(1000);
        builder.Property(item => item.AssignmentType).IsRequired().HasMaxLength(32);
        builder.Property(item => item.EvaluationStatus).IsRequired().HasMaxLength(32);
        builder.Property(item => item.SourceEvidence).IsRequired().HasColumnType("TEXT");
        builder.HasOne<ToxicCombinationFinding>().WithMany().HasForeignKey(item => item.FindingId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class PermissionCombinationConfiguration : IEntityTypeConfiguration<PermissionCombination>
{
    public void Configure(EntityTypeBuilder<PermissionCombination> builder)
    {
        builder.HasKey(item => item.Id);
        builder.HasIndex(item => new { item.FindingId, item.FirstEffectiveAccessId, item.SecondEffectiveAccessId }).IsUnique();
        builder.Property(item => item.FirstAction).IsRequired().HasMaxLength(500);
        builder.Property(item => item.SecondAction).IsRequired().HasMaxLength(500);
        builder.Property(item => item.ProviderSemantics).IsRequired().HasMaxLength(1000);
        builder.Property(item => item.ScopeAssessment).IsRequired().HasMaxLength(32);
        builder.HasOne<ToxicCombinationFinding>().WithMany().HasForeignKey(item => item.FindingId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ToxicCombinationRecommendationConfiguration : IEntityTypeConfiguration<ToxicCombinationRecommendation>
{
    public void Configure(EntityTypeBuilder<ToxicCombinationRecommendation> builder)
    {
        builder.HasKey(item => item.Id);
        builder.HasIndex(item => item.FindingId).IsUnique();
        builder.Property(item => item.Title).IsRequired().HasMaxLength(240);
        builder.Property(item => item.Description).IsRequired().HasMaxLength(2000);
        builder.Property(item => item.Evidence).IsRequired().HasColumnType("TEXT");
        builder.Property(item => item.Status).IsRequired().HasMaxLength(32);
        builder.HasOne<ToxicCombinationFinding>().WithMany().HasForeignKey(item => item.FindingId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ToxicCombinationTrustPathEvidenceConfiguration : IEntityTypeConfiguration<ToxicCombinationTrustPathEvidence>
{
    public void Configure(EntityTypeBuilder<ToxicCombinationTrustPathEvidence> builder)
    {
        builder.HasKey(item => item.Id);
        builder.HasIndex(item => new { item.FindingId, item.Sequence }).IsUnique();
        builder.HasIndex(item => new { item.SourceCloudAccountId, item.SourcePrincipalId, item.TargetCloudAccountId, item.TargetPrincipalId });
        builder.Property(item => item.RelationshipType).IsRequired().HasMaxLength(120);
        builder.Property(item => item.Status).IsRequired().HasMaxLength(24);
        builder.Property(item => item.EvidenceSource).IsRequired().HasMaxLength(500);
        builder.Property(item => item.EvidenceReference).IsRequired().HasMaxLength(2000);
        builder.Property(item => item.ConditionEvidence).IsRequired().HasMaxLength(2000);
        builder.HasOne<ToxicCombinationFinding>().WithMany().HasForeignKey(item => item.FindingId).OnDelete(DeleteBehavior.Cascade);
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
        builder.HasIndex(entity => entity.Fingerprint);
        builder.HasIndex(entity => new { entity.PrincipalId, entity.ResourceId, entity.EffectiveAccessId });
        builder.Property(entity => entity.Title).IsRequired().HasMaxLength(250);
        builder.Property(entity => entity.RuleVersion).HasMaxLength(20);
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
        builder.HasIndex(entity => new { entity.RecommendationType, entity.Provider, entity.Status, entity.EvaluationState });
        builder.HasIndex(entity => new { entity.RiskFindingId, entity.EffectiveAccessId, entity.AccessPathId });
        builder.HasIndex(entity => entity.Fingerprint);
        builder.Property(entity => entity.Changes).HasColumnType("TEXT");
        builder.Property(entity => entity.Impact).HasColumnType("TEXT");
        builder.Property(entity => entity.Evidence).HasColumnType("TEXT");
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
