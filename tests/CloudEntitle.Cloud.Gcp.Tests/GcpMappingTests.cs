using System.Text.Json;
using CloudEntitle.Cloud.Gcp.Mappers;
using CloudEntitle.Domain;
using Xunit;

namespace CloudEntitle.Cloud.Gcp.Tests;

public sealed class GcpMappingTests
{
    [Fact]
    public void IamMapper_ShouldPreserveBindingsAndConditions()
    {
        using var document = JsonDocument.Parse("{\"bindings\":[{\"role\":\"roles/storage.objectViewer\",\"members\":[\"user:demo@example.invalid\",\"group:demo-group@example.invalid\"],\"condition\":{\"title\":\"DemoWindow\",\"expression\":\"request.time < timestamp('2030-01-01T00:00:00Z')\"}}]}");

        var bindings = GcpIamPolicyMapper.MapBindings(document.RootElement);
        var permission = GcpIamPolicyMapper.ToPermission(Guid.NewGuid(), bindings[0], "demo-production");
        var policy = GcpIamPolicyMapper.ToPolicy(Guid.NewGuid(), bindings[0], "demo-production");

        Assert.Equal(2, bindings.Count);
        Assert.Contains("request.time", policy.Document);
        Assert.Equal(AccessType.Allow, permission.AccessType);
        Assert.Equal("roles/storage.objectViewer", permission.Action);
        Assert.Contains("condition", permission.Source!, StringComparison.OrdinalIgnoreCase);
    }
}
