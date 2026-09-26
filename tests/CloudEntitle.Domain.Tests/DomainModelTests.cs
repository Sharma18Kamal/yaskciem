using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Domain.ValueObjects;
using Xunit;

namespace CloudEntitle.Domain.Tests;

public class DomainModelTests
{
    [Fact]
    public void CloudAccount_ShouldCaptureNormalizedMetadata()
    {
        var account = new CloudAccount
        {
            Id = Guid.NewGuid(),
            Name = "Production Azure",
            CloudProvider = CloudProviderType.Azure,
            ConnectionStatus = ConnectionStatus.Connected,
            SubscriptionId = "sub-123",
            ExternalAccountId = "azure-prod-001",
            IsReadOnly = true
        };

        Assert.Equal("Production Azure", account.Name);
        Assert.Equal(CloudProviderType.Azure, account.CloudProvider);
        Assert.Equal(ConnectionStatus.Connected, account.ConnectionStatus);
        Assert.True(account.IsReadOnly);
    }

    [Fact]
    public void RiskFinding_ShouldStoreSeverityAndRecommendation()
    {
        var finding = new RiskFinding
        {
            Id = Guid.NewGuid(),
            CloudProvider = CloudProviderType.Aws,
            Severity = RiskSeverity.Critical,
            Title = "AdministratorAccess",
            Description = "Excessive privileges detected",
            Principal = "admin-user",
            Resource = "arn:aws:iam::123456789012:role/Admin",
            Recommendation = "Replace with least privilege access",
            Evidence = "Inline policy attached"
        };

        Assert.Equal(RiskSeverity.Critical, finding.Severity);
        Assert.Equal("AdministratorAccess", finding.Title);
        Assert.Equal("admin-user", finding.Principal);
    }

    [Fact]
    public void CloudPrincipal_ShouldSupportIdentityNormalization()
    {
        var principal = new CloudPrincipal
        {
            Id = Guid.NewGuid(),
            CloudProvider = CloudProviderType.Gcp,
            IdentityType = IdentityType.ServiceAccount,
            Name = "svc-security-scan",
            Email = "svc-security-scan@test-project.iam.gserviceaccount.com",
            IsPrivileged = true,
            IsActive = true
        };

        Assert.Equal(IdentityType.ServiceAccount, principal.IdentityType);
        Assert.Equal("svc-security-scan", principal.Name);
        Assert.True(principal.IsPrivileged);
        Assert.True(principal.IsActive);
    }

    [Fact]
    public void TypedIdentifiers_ShouldPreserveGuidIdentity()
    {
        var value = Guid.NewGuid();
        var accountId = new CloudAccountId(value);

        Assert.Equal(value, accountId.Value);
        Assert.Equal(value.ToString(), accountId.ToString());
    }

    [Fact]
    public void ValueObjects_ShouldRejectInvalidValues()
    {
        Assert.Throws<ArgumentException>(() => new CloudScope(" "));
        Assert.Throws<ArgumentException>(() => new PermissionIdentifier(""));
        Assert.Throws<ArgumentException>(() => new EmailAddress("not-an-email"));
    }

    [Fact]
    public void NormalizedEntities_ShouldValidateRequiredFields()
    {
        var account = new CloudAccount();
        var principal = new CloudPrincipal();
        var permission = new CloudPermission();
        var finding = new RiskFinding();

        Assert.Throws<ArgumentException>(() => account.Validate());
        Assert.Throws<ArgumentException>(() => principal.Validate());
        Assert.Throws<ArgumentException>(() => permission.Validate());
        Assert.Throws<ArgumentException>(() => finding.Validate());
    }

    [Fact]
    public void EffectiveAccess_ShouldRepresentDirectAndInheritedAccess()
    {
        var access = new EffectiveAccess
        {
            Id = Guid.NewGuid(),
            PrincipalId = Guid.NewGuid(),
            CloudAccountId = Guid.NewGuid(),
            PermissionId = Guid.NewGuid(),
            AssignmentType = AssignmentType.Group,
            AccessType = AccessType.Allow,
            IsDirect = false,
            IsInherited = true
        };

        Assert.Equal(AssignmentType.Group, access.AssignmentType);
        Assert.True(access.IsInherited);
        Assert.False(access.IsDirect);
    }

    [Fact]
    public void AccessPath_ShouldRetainOrderedProviderIndependentNodes()
    {
        var path = new AccessPath
        {
            Id = Guid.NewGuid(),
            PrincipalId = Guid.NewGuid(),
            CloudAccountId = Guid.NewGuid(),
            PathType = AccessPathType.GroupBased,
            Nodes =
            [
                new AccessPathNode { NodeType = "Principal", Name = "John", Sequence = 0 },
                new AccessPathNode { NodeType = "Group", Name = "SecurityGroup", Sequence = 1 },
                new AccessPathNode { NodeType = "Role", Name = "Contributor", Sequence = 2 },
                new AccessPathNode { NodeType = "Resource", Name = "Production", Sequence = 3 }
            ]
        };

        Assert.Equal(AccessPathType.GroupBased, path.PathType);
        Assert.Equal(new[] { "Principal", "Group", "Role", "Resource" }, path.Nodes.OrderBy(node => node.Sequence).Select(node => node.NodeType));
    }
}
