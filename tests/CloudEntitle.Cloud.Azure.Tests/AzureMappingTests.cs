using System.Text.Json;
using CloudEntitle.Cloud.Azure.Mappers;
using CloudEntitle.Domain;
using Xunit;

namespace CloudEntitle.Cloud.Azure.Tests;

public sealed class AzureMappingTests
{
    [Fact]
    public void ScopeMapper_ShouldPreserveAzureScopeAndClassifyHierarchy()
    {
        var scope = AzureScopeMapper.Map("/subscriptions/sub-001/resourceGroups/rg-prod");

        Assert.Equal("/subscriptions/sub-001/resourceGroups/rg-prod", scope.Value);
        Assert.Equal("ResourceGroup", AzureScopeMapper.GetScopeType(scope.Value));
    }

    [Fact]
    public void PermissionMapper_ShouldPreserveAllowAndDenySemantics()
    {
        using var document = JsonDocument.Parse("{\"name\":\"role-001\",\"properties\":{\"permissions\":[{\"actions\":[\"Microsoft.Storage/*\"],\"notActions\":[\"Microsoft.Storage/delete\"],\"dataActions\":[\"Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read\"],\"notDataActions\":[\"Microsoft.Storage/private\"]}]}}");

        var permissions = AzurePermissionMapper.MapRolePermissions(Guid.NewGuid(), document.RootElement).ToArray();

        Assert.Equal(4, permissions.Length);
        Assert.Contains(permissions, permission => permission.AccessType == AccessType.Allow && permission.Source!.Contains("Actions"));
        Assert.Contains(permissions, permission => permission.AccessType == AccessType.Deny && permission.Source!.Contains("NotActions"));
        Assert.Contains(permissions, permission => permission.Action == "Microsoft.Storage/*");
    }
}
