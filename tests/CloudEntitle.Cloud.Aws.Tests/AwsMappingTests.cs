using CloudEntitle.Cloud.Aws.Mappers;
using CloudEntitle.Domain;
using Xunit;

namespace CloudEntitle.Cloud.Aws.Tests;

public sealed class AwsMappingTests
{
    [Fact]
    public void PolicyMapper_ShouldPreserveAllowDenyWildcardsAndArnResources()
    {
        var policy = "{\"Statement\":[{\"Effect\":\"Allow\",\"Action\":[\"s3:GetObject\",\"s3:*\"],\"Resource\":\"arn:aws:s3:::demo-bucket/*\"},{\"Effect\":\"Deny\",\"NotAction\":\"iam:DeleteUser\",\"NotResource\":\"arn:aws:iam::123456789012:user/demo\"}]}";

        var permissions = AwsPolicyMapper.MapPolicy(Guid.NewGuid(), "DemoPolicy", "arn:aws:iam::123456789012:policy/DemoPolicy", policy).ToArray();

        Assert.Equal(3, permissions.Length);
        Assert.Contains(permissions, permission => permission.Action == "s3:*" && permission.AccessType == AccessType.Allow);
        Assert.Contains(permissions, permission => permission.Action == "iam:DeleteUser" && permission.AccessType == AccessType.Deny);
        Assert.Contains(permissions, permission => permission.Resource == "arn:aws:s3:::demo-bucket/*");
    }

    [Fact]
    public void TrustMapper_ShouldExtractAwsPrincipals()
    {
        var policy = "{\"Statement\":[{\"Effect\":\"Allow\",\"Principal\":{\"AWS\":[\"arn:aws:iam::123456789012:root\",\"arn:aws:iam::123456789012:role/Trusted\"]},\"Action\":\"sts:AssumeRole\"}]}";

        var principals = AwsPolicyMapper.MapTrustPrincipals(policy);

        Assert.Equal(2, principals.Count);
        Assert.Contains("arn:aws:iam::123456789012:role/Trusted", principals);
    }
}
