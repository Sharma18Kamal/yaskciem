using CloudEntitle.Domain;
using CloudEntitle.Normalization.Models;
using CloudEntitle.Normalization.Services;
using Xunit;

namespace CloudEntitle.Normalization.Tests;

public sealed class NormalizationTests
{
    [Fact]
    public async Task AzureNotActions_ShouldRemainExplicitDenyAndTraceable()
    {
        var source = CreateSource(CloudProvider.Azure, AccessType.Deny, AssignmentType.Role, "Microsoft.Compute/virtualMachines/delete", "/subscriptions/azure-demo/resourceGroups/prod", "AzureRoleDefinition", "role-azure", "NotActions");

        var result = await new EntitlementNormalizationService().NormalizeAsync(source);
        var entitlement = Assert.Single(result.Entitlements);

        Assert.Equal(NormalizedAccessType.Deny, entitlement.AccessType);
        Assert.Equal("role-azure", entitlement.Evidence.SourceId);
        Assert.Equal("NotActions", entitlement.Evidence.OriginalAssignment);
        Assert.Equal(NormalizedScopeType.ResourceGroup, entitlement.Scope.Type);
    }

    [Fact]
    public async Task AwsCondition_ShouldRemainConditionalAndPartiallyNormalized()
    {
        var source = CreateSource(CloudProvider.Aws, AccessType.Allow, AssignmentType.Policy, "s3:GetObject", "arn:aws:s3:::demo/*", "AwsPolicyStatement", "statement-4", "Condition", "aws:RequestedRegion == us-east-1");

        var result = await new EntitlementNormalizationService().NormalizeAsync(source);
        var entitlement = Assert.Single(result.Entitlements);

        Assert.Equal(NormalizedAccessType.Conditional, entitlement.AccessType);
        Assert.True(entitlement.IsConditional);
        Assert.Equal("Unknown", entitlement.Condition!.EvaluationStatus);
        Assert.Equal(NormalizationStatus.PartiallyNormalized, entitlement.Status);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public async Task GcpInheritedBinding_ShouldPreserveInheritanceAndConditionEvidence()
    {
        var source = CreateSource(CloudProvider.Gcp, AccessType.Allow, AssignmentType.Inherited, "storage.objects.get", "projects/gcp-demo", "GcpIamBinding", "binding-1", "GCPCondition", "resource.name.startsWith('projects/gcp-demo')", isInherited: true, sourceScope: "organizations/123", targetScope: "projects/gcp-demo");

        var result = await new EntitlementNormalizationService().NormalizeAsync(source);
        var entitlement = Assert.Single(result.Entitlements);

        Assert.True(entitlement.IsInherited);
        Assert.False(entitlement.IsDirect);
        Assert.Equal("organizations/123", entitlement.Scope.SourceScope);
        Assert.Equal("projects/gcp-demo", entitlement.Scope.TargetScope);
        Assert.Equal(NormalizedScopeType.Project, entitlement.Scope.Type);
    }

    [Fact]
    public async Task Normalization_ShouldDeduplicateIdenticalRecordsAndValidateMissingReferences()
    {
        var source = CreateSource(CloudProvider.Azure, AccessType.Allow, AssignmentType.Direct, "read", "/subscriptions/demo", "RoleAssignment", "assignment-1", "Direct");
        var duplicate = source.AuthorizationRecords[0] with { SourceId = "assignment-2" };
        var result = await new EntitlementNormalizationService().NormalizeAsync(source with { AuthorizationRecords = new[] { source.AuthorizationRecords[0], duplicate } });

        Assert.Single(result.Entitlements);
        Assert.Equal(2, result.Statistics.ItemsProcessed);

        var invalid = source with { AuthorizationRecords = new[] { source.AuthorizationRecords[0] with { PrincipalId = Guid.NewGuid() } } };
        var invalidResult = await new EntitlementNormalizationService().NormalizeAsync(invalid);
        Assert.Empty(invalidResult.Entitlements);
        Assert.NotEmpty(invalidResult.Errors);
    }

    [Fact]
    public async Task CrossCloudPermissions_ShouldRemainProviderSpecific()
    {
        var service = new EntitlementNormalizationService();
        var azure = await service.NormalizeAsync(CreateSource(CloudProvider.Azure, AccessType.Allow, AssignmentType.Role, "Microsoft.Storage/storageAccounts/read", "/subscriptions/azure", "AzureRole", "az-role", "Actions"));
        var aws = await service.NormalizeAsync(CreateSource(CloudProvider.Aws, AccessType.Allow, AssignmentType.Policy, "s3:GetObject", "arn:aws:s3:::bucket/*", "AwsPolicy", "aws-policy", "Action"));
        var gcp = await service.NormalizeAsync(CreateSource(CloudProvider.Gcp, AccessType.Allow, AssignmentType.Direct, "storage.objects.get", "projects/gcp", "GcpRole", "gcp-role", "Permission"));

        Assert.Equal(CloudProvider.Azure, azure.Entitlements[0].Provider);
        Assert.Equal(CloudProvider.Aws, aws.Entitlements[0].Provider);
        Assert.Equal(CloudProvider.Gcp, gcp.Entitlements[0].Provider);
        Assert.NotEqual(azure.Permissions[0].Action, aws.Permissions[0].Action);
        Assert.NotEqual(aws.Permissions[0].Action, gcp.Permissions[0].Action);
    }

    private static ProviderDiscoveryResult CreateSource(CloudProvider provider, AccessType accessType, AssignmentType assignmentType, string action, string scope, string sourceType, string sourceId, string originalAssignment, string? condition = null, bool isInherited = false, string? sourceScope = null, string? targetScope = null)
    {
        var account = new CloudAccount { Id = Guid.NewGuid(), Name = $"{provider} account", ExternalAccountId = $"{provider}-demo", CloudProvider = provider switch { CloudProvider.Azure => CloudProviderType.Azure, CloudProvider.Aws => CloudProviderType.Aws, _ => CloudProviderType.Gcp } };
        var principal = new CloudPrincipal { Id = Guid.NewGuid(), AccountId = account.Id, Name = "demo-principal", PrincipalId = "demo-principal", CloudProvider = account.CloudProvider, IdentityType = IdentityType.User };
        var role = new CloudRole { Id = Guid.NewGuid(), AccountId = account.Id, Name = "DemoRole", RoleId = $"{provider}-role" };
        var policy = new CloudPolicy { Id = Guid.NewGuid(), AccountId = account.Id, Name = "DemoPolicy", ExternalId = $"{provider}-policy" };
        var permission = new CloudPermission { Id = Guid.NewGuid(), AccountId = account.Id, Name = action, Action = action, Resource = scope, AccessType = accessType };
        var record = new ProviderAuthorizationRecord(sourceId, "Demo source", account.Id, principal.Id, role.Id, policy.Id, permission.Id, scope, scope, assignmentType, accessType, sourceType, "original document", "original statement", condition, condition == null ? null : "DemoCondition", isInherited, sourceScope, targetScope, new Dictionary<string, string> { ["assignment"] = originalAssignment });
        return new ProviderDiscoveryResult(provider, new[] { account }, new[] { principal }, new[] { role }, new[] { policy }, new[] { permission }, new[] { record }, DateTime.UtcNow);
    }
}
