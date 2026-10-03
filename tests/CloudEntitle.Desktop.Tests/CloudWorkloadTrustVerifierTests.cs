using CloudEntitle.Application.Interfaces;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Domain;
using Xunit;

namespace CloudEntitle.Desktop.Tests;

public sealed class CloudWorkloadTrustVerifierTests
{
    [Fact]
    public async Task VerifyAsync_ShouldConfirmGkeServiceAccountBinding()
    {
        var runner = new FakeCommandRunner((command, arguments) =>
        {
            if (command != "gcloud") return new CloudCommandResult(1, string.Empty, "unexpected command");
            if (arguments.Contains("clusters")) return Ok("[{\"name\":\"demo-cluster\",\"workloadIdentityConfig\":{\"workloadPool\":\"123456789012.svc.id.goog\"}}]");
            if (arguments.Contains("describe")) return Ok("{\"projectNumber\":\"123456789012\"}");
            if (arguments.Contains("get-iam-policy")) return Ok("{\"bindings\":[{\"role\":\"roles/iam.workloadIdentityUser\",\"members\":[\"serviceAccount:123456789012.svc.id.goog[apps/app]\"]}]} ");
            return new CloudCommandResult(1, string.Empty, "unexpected gcloud command");
        });

        var result = await new CloudWorkloadTrustVerifier(runner).VerifyAsync(
            CloudProviderType.Gcp, "gke_demo-project_us-central1_demo-cluster", null,
            "apps", "app", "app@demo-project.iam.gserviceaccount.com");

        Assert.Equal(WorkloadTrustStatus.Verified, result.Status);
        Assert.Contains("apps/app", result.Details);
    }

    [Fact]
    public async Task VerifyAsync_ShouldConfirmAksFederatedCredential()
    {
        const string clientId = "00000000-0000-0000-0000-000000000001";
        const string issuer = "https://eastus.oic.prod-aks.azure.com/issuer-id/";
        var runner = new FakeCommandRunner((command, arguments) =>
        {
            if (command != "az") return new CloudCommandResult(1, string.Empty, "unexpected command");
            if (arguments.Contains("aks")) return Ok($"[{{\"fqdn\":\"aks.example.invalid\",\"oidcIssuerProfile\":{{\"issuerUrl\":\"{issuer}\"}}}}]");
            if (arguments.Contains("federated-credential")) return Ok($"[{{\"issuer\":\"{issuer}\",\"subject\":\"system:serviceaccount:apps:backend\",\"audiences\":[\"api://AzureADTokenExchange\"]}}]");
            if (arguments.Contains("identity") && arguments.Contains("list")) return Ok($"[{{\"name\":\"workload-mi\",\"resourceGroup\":\"identity-rg\",\"clientId\":\"{clientId}\"}}]");
            return new CloudCommandResult(1, string.Empty, "unexpected az command");
        });

        var result = await new CloudWorkloadTrustVerifier(runner).VerifyAsync(
            CloudProviderType.Azure, "aks-prod", "https://aks.example.invalid", "apps", "backend", clientId);

        Assert.Equal(WorkloadTrustStatus.Verified, result.Status);
        Assert.Contains("apps:backend", result.Details);
    }

    [Fact]
    public async Task VerifyAsync_ShouldReportDeniedWhenAwsTrustCannotBeRead()
    {
        var runner = new FakeCommandRunner((command, arguments) =>
        {
            if (arguments.Contains("get-caller-identity")) return Ok("{\"Account\":\"123456789012\"}");
            if (arguments.Contains("describe-cluster")) return Ok("{\"cluster\":{\"identity\":{\"oidc\":{\"issuer\":\"https://oidc.eks.us-east-1.amazonaws.com/id/EXAMPLE\"}}}}");
            if (arguments.Contains("get-role")) return new CloudCommandResult(1, string.Empty, "AccessDenied: not authorized to perform iam:GetRole");
            return new CloudCommandResult(1, string.Empty, "unexpected aws command");
        });

        var result = await new CloudWorkloadTrustVerifier(runner).VerifyAsync(
            CloudProviderType.Aws, "arn:aws:eks:us-east-1:123456789012:cluster/demo", null,
            "apps", "app", "arn:aws:iam::123456789012:role/app");

        Assert.Equal(WorkloadTrustStatus.Denied, result.Status);
        Assert.Contains("AccessDenied", result.Details);
    }

    [Fact]
    public async Task VerifyAsync_ShouldNotTreatAwsDenyStatementAsTrust()
    {
        var runner = new FakeCommandRunner((command, arguments) =>
        {
            if (arguments.Contains("get-caller-identity")) return Ok("{\"Account\":\"123456789012\"}");
            if (arguments.Contains("describe-cluster")) return Ok("{\"cluster\":{\"identity\":{\"oidc\":{\"issuer\":\"https://oidc.eks.us-east-1.amazonaws.com/id/EXAMPLE\"}}}}");
            if (arguments.Contains("get-role")) return Ok("{\"Role\":{\"AssumeRolePolicyDocument\":{\"Statement\":[{\"Effect\":\"Deny\",\"Principal\":{\"Federated\":\"arn:aws:iam::123456789012:oidc-provider/oidc.eks.us-east-1.amazonaws.com/id/EXAMPLE\"},\"Action\":\"sts:AssumeRoleWithWebIdentity\",\"Condition\":{\"StringEquals\":{\"oidc.eks.us-east-1.amazonaws.com/id/EXAMPLE:aud\":\"sts.amazonaws.com\",\"oidc.eks.us-east-1.amazonaws.com/id/EXAMPLE:sub\":\"system:serviceaccount:apps:app\"}}}]}}}");
            return new CloudCommandResult(1, string.Empty, "unexpected aws command");
        });

        var result = await new CloudWorkloadTrustVerifier(runner).VerifyAsync(
            CloudProviderType.Aws, "arn:aws:eks:us-east-1:123456789012:cluster/demo", null,
            "apps", "app", "arn:aws:iam::123456789012:role/app");

        Assert.Equal(WorkloadTrustStatus.Mismatch, result.Status);
    }

    [Fact]
    public async Task VerifyAsync_ShouldKeepConditionalGkeBindingUnavailable()
    {
        var runner = new FakeCommandRunner((command, arguments) =>
        {
            if (arguments.Contains("clusters")) return Ok("[{\"name\":\"demo-cluster\",\"workloadIdentityConfig\":{\"workloadPool\":\"123456789012.svc.id.goog\"}}]");
            if (arguments.Contains("describe")) return Ok("{\"projectNumber\":\"123456789012\"}");
            if (arguments.Contains("get-iam-policy")) return Ok("{\"bindings\":[{\"role\":\"roles/iam.workloadIdentityUser\",\"members\":[\"serviceAccount:123456789012.svc.id.goog[apps/app]\"],\"condition\":{\"expression\":\"request.time < timestamp('2030-01-01T00:00:00Z')\"}}]}");
            return new CloudCommandResult(1, string.Empty, "unexpected gcloud command");
        });

        var result = await new CloudWorkloadTrustVerifier(runner).VerifyAsync(
            CloudProviderType.Gcp, "gke_demo-project_us-central1_demo-cluster", null,
            "apps", "app", "app@demo-project.iam.gserviceaccount.com");

        Assert.Equal(WorkloadTrustStatus.Unavailable, result.Status);
        Assert.Contains("condition", result.Details, StringComparison.OrdinalIgnoreCase);
    }

    private static CloudCommandResult Ok(string output) => new(0, output, string.Empty);

    private sealed class FakeCommandRunner(Func<string, IReadOnlyList<string>, CloudCommandResult> run) : ICloudCommandRunner
    {
        public Task<CloudCommandResult> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
            => Task.FromResult(run(command, arguments));
    }
}
