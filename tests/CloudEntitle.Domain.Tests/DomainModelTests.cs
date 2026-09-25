using CloudEntitle.Domain;
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
}
