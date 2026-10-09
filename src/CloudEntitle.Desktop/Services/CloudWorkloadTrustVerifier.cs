using System.Text.Json;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;

namespace CloudEntitle.Desktop.Services;

public sealed class CloudWorkloadTrustVerifier(ICloudCommandRunner commandRunner)
{
    public Task<WorkloadIdentityVerification> VerifyAsync(
        CloudProviderType provider,
        string contextName,
        string? apiServer,
        string serviceAccountNamespace,
        string serviceAccountName,
        string cloudIdentity,
        CancellationToken cancellationToken = default)
        => provider switch
        {
            CloudProviderType.Aws => VerifyAwsAsync(contextName, serviceAccountNamespace, serviceAccountName, cloudIdentity, cancellationToken),
            CloudProviderType.Gcp => VerifyGcpAsync(contextName, serviceAccountNamespace, serviceAccountName, cloudIdentity, cancellationToken),
            CloudProviderType.Azure => VerifyAzureAsync(apiServer, serviceAccountNamespace, serviceAccountName, cloudIdentity, cancellationToken),
            _ => Task.FromResult(new WorkloadIdentityVerification(WorkloadTrustStatus.Unsupported, "The Kubernetes context is not identified as AKS, EKS, or GKE."))
        };

    private async Task<WorkloadIdentityVerification> VerifyAwsAsync(string contextName, string ns, string serviceAccount, string roleArn, CancellationToken cancellationToken)
    {
        var arn = roleArn.Split(':');
        if (arn.Length < 6 || !arn[2].Equals("iam", StringComparison.OrdinalIgnoreCase) || !arn[5].StartsWith("role/", StringComparison.OrdinalIgnoreCase))
            return new(WorkloadTrustStatus.Mismatch, "The annotated AWS identity is not an IAM role ARN.");
        var roleName = arn[5]["role/".Length..].Split('/').Last();
        var clusterArn = contextName.Split('/');
        var clusterName = clusterArn.Length > 1 ? clusterArn[^1] : string.Empty;
        var region = clusterArn.Length > 1 ? clusterArn[0].Split(':').ElementAtOrDefault(3) : null;
        if (string.IsNullOrWhiteSpace(clusterName) || string.IsNullOrWhiteSpace(region))
            return new(WorkloadTrustStatus.Unavailable, "The EKS context does not identify a cluster name and region needed to verify its OIDC issuer.");

        var caller = await RunAsync("aws", new[] { "sts", "get-caller-identity", "--output", "json" }, cancellationToken);
        if (caller.Verification is not null) return caller.Verification;
        using var callerDocument = JsonDocument.Parse(caller.Output);
        if (ReadString(callerDocument.RootElement, "Account") is not { } currentAccount || currentAccount != arn[4])
            return new(WorkloadTrustStatus.Mismatch, "The active AWS CLI account does not match the annotated IAM role account.");

        var cluster = await RunAsync("aws", new[] { "eks", "describe-cluster", "--name", clusterName, "--region", region, "--output", "json" }, cancellationToken);
        if (cluster.Verification is not null) return cluster.Verification;
        using var clusterDocument = JsonDocument.Parse(cluster.Output);
        var issuer = ReadNestedString(clusterDocument.RootElement, "cluster", "identity", "oidc", "issuer");
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri))
            return new(WorkloadTrustStatus.Unavailable, "The EKS cluster did not return an OIDC issuer URL.");

        var role = await RunAsync("aws", new[] { "iam", "get-role", "--role-name", roleName, "--output", "json" }, cancellationToken);
        if (role.Verification is not null) return role.Verification;
        using var roleDocument = JsonDocument.Parse(role.Output);
        var trust = ReadNestedElement(roleDocument.RootElement, "Role", "AssumeRolePolicyDocument");
        var expectedProvider = $"arn:aws:iam::{arn[4]}:oidc-provider/{issuerUri.Host}{issuerUri.AbsolutePath}";
        var expectedSubject = $"system:serviceaccount:{ns}:{serviceAccount}";
        return HasAwsWebIdentityTrust(trust, expectedProvider, expectedSubject)
            ? new WorkloadIdentityVerification(WorkloadTrustStatus.Verified, $"IAM role trust matches EKS OIDC issuer and subject {expectedSubject}.")
            {
                Issuer = issuerUri.AbsoluteUri,
                Subject = expectedSubject,
                Audience = "sts.amazonaws.com",
                Source = "AWS EKS OIDC role trust"
            }
            : new(WorkloadTrustStatus.Mismatch, $"IAM role trust does not contain the expected EKS issuer, audience, and subject {expectedSubject}.");
    }

    private async Task<WorkloadIdentityVerification> VerifyGcpAsync(string contextName, string ns, string serviceAccount, string cloudServiceAccount, CancellationToken cancellationToken)
    {
        var at = cloudServiceAccount.LastIndexOf('@');
        var dot = cloudServiceAccount.IndexOf('.', at + 1);
        if (at < 1 || dot <= at + 1 || !cloudServiceAccount.EndsWith(".iam.gserviceaccount.com", StringComparison.OrdinalIgnoreCase))
            return new(WorkloadTrustStatus.Mismatch, "The annotated GCP identity is not a service-account email address.");
        var project = cloudServiceAccount[(at + 1)..dot];
        var contextParts = contextName.Split('_', StringSplitOptions.RemoveEmptyEntries);
        var clusterName = contextParts.Length >= 4 && contextParts[0].Equals("gke", StringComparison.OrdinalIgnoreCase)
            ? contextParts[^1]
            : contextParts.LastOrDefault() ?? string.Empty;
        var clusters = await RunAsync("gcloud", new[] { "container", "clusters", "list", $"--project={project}", "--format=json" }, cancellationToken);
        if (clusters.Verification is not null) return clusters.Verification;
        using var clustersDocument = JsonDocument.Parse(clusters.Output);
        var cluster = clustersDocument.RootElement.ValueKind == JsonValueKind.Array
            ? clustersDocument.RootElement.EnumerateArray().FirstOrDefault(item => ReadString(item, "name")?.Equals(clusterName, StringComparison.OrdinalIgnoreCase) == true)
            : default;
        var workloadPool = ReadNestedString(cluster, "workloadIdentityConfig", "workloadPool");
        if (string.IsNullOrWhiteSpace(workloadPool))
            return new(WorkloadTrustStatus.Unavailable, "The active gcloud project did not return matching GKE Workload Identity pool metadata.");

        var projectInfo = await RunAsync("gcloud", new[] { "projects", "describe", project, "--format=json" }, cancellationToken);
        if (projectInfo.Verification is not null) return projectInfo.Verification;
        using var projectDocument = JsonDocument.Parse(projectInfo.Output);
        var projectNumber = ReadString(projectDocument.RootElement, "projectNumber");
        if (string.IsNullOrWhiteSpace(projectNumber))
            return new(WorkloadTrustStatus.Unavailable, "The GCP project number needed to verify the workload pool was not returned.");

        var policy = await RunAsync("gcloud", new[] { "iam", "service-accounts", "get-iam-policy", cloudServiceAccount, $"--project={project}", "--format=json" }, cancellationToken);
        if (policy.Verification is not null) return policy.Verification;
        using var policyDocument = JsonDocument.Parse(policy.Output);
        var expectedLegacyMember = $"serviceAccount:{workloadPool}[{ns}/{serviceAccount}]";
        var expectedPrincipal = $"principal://iam.googleapis.com/projects/{projectNumber}/locations/global/workloadIdentityPools/{workloadPool}/subject/ns/{ns}/sa/{serviceAccount}";
        var matchingBinding = policyDocument.RootElement.TryGetProperty("bindings", out var bindings)
            && bindings.ValueKind == JsonValueKind.Array
            ? bindings.EnumerateArray().FirstOrDefault(binding =>
                ReadString(binding, "role") == "roles/iam.workloadIdentityUser"
                && binding.TryGetProperty("members", out var members)
                && members.ValueKind == JsonValueKind.Array
                && members.EnumerateArray().Any(member => member.ValueKind == JsonValueKind.String
                    && (member.GetString() == expectedLegacyMember || member.GetString() == expectedPrincipal)))
            : default;
        if (matchingBinding.ValueKind != JsonValueKind.Object)
            return new(WorkloadTrustStatus.Mismatch, $"The service account IAM policy lacks roles/iam.workloadIdentityUser for {ns}/{serviceAccount}.");
        if (matchingBinding.TryGetProperty("condition", out var condition) && condition.ValueKind == JsonValueKind.Object)
            return new(WorkloadTrustStatus.Unavailable, "A condition restricts the matching GKE workload binding; its runtime context was not evaluated.");
        return new WorkloadIdentityVerification(WorkloadTrustStatus.Verified, $"GKE Workload Identity binding matches {ns}/{serviceAccount} on {cloudServiceAccount}.")
        {
            Issuer = workloadPool,
            Subject = expectedPrincipal,
            Audience = cloudServiceAccount,
            Source = "GKE Workload Identity IAM binding"
        };
    }

    private async Task<WorkloadIdentityVerification> VerifyAzureAsync(string? apiServer, string ns, string serviceAccount, string clientId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(clientId, out _))
            return new(WorkloadTrustStatus.Mismatch, "The Azure workload identity annotation is not a client ID.");
        if (!Uri.TryCreate(apiServer, UriKind.Absolute, out var serverUri))
            return new(WorkloadTrustStatus.Unavailable, "The AKS API server URL is unavailable for matching its OIDC issuer.");

        var clusters = await RunAsync("az", new[] { "aks", "list", "--output", "json" }, cancellationToken);
        if (clusters.Verification is not null) return clusters.Verification;
        using var clusterDocument = JsonDocument.Parse(clusters.Output);
        var match = clusterDocument.RootElement.ValueKind == JsonValueKind.Array
            ? clusterDocument.RootElement.EnumerateArray().FirstOrDefault(item =>
            {
                var fqdn = ReadString(item, "fqdn") ?? ReadString(item, "privateFqdn");
                return string.Equals(fqdn, serverUri.Host, StringComparison.OrdinalIgnoreCase);
            })
            : default;
        var issuer = ReadNestedString(match, "oidcIssuerProfile", "issuerUrl");
        if (string.IsNullOrWhiteSpace(issuer))
            return new(WorkloadTrustStatus.Unavailable, "No matching AKS cluster with an enabled OIDC issuer was found in the active Azure subscription.");

        var identities = await RunAsync("az", new[] { "identity", "list", "--output", "json" }, cancellationToken);
        if (identities.Verification is not null) return identities.Verification;
        using var identitiesDocument = JsonDocument.Parse(identities.Output);
        var identity = identitiesDocument.RootElement.ValueKind == JsonValueKind.Array
            ? identitiesDocument.RootElement.EnumerateArray().FirstOrDefault(item => string.Equals(ReadString(item, "clientId"), clientId, StringComparison.OrdinalIgnoreCase))
            : default;
        CloudCommandResult credentials;
        if (identity.ValueKind == JsonValueKind.Object)
        {
            var identityName = ReadString(identity, "name");
            var resourceGroup = ReadString(identity, "resourceGroup");
            if (string.IsNullOrWhiteSpace(identityName) || string.IsNullOrWhiteSpace(resourceGroup))
                return new(WorkloadTrustStatus.Unavailable, "Azure returned an incomplete user-assigned identity record.");
            credentials = await commandRunner.RunAsync("az", new[] { "identity", "federated-credential", "list", "--identity-name", identityName, "--resource-group", resourceGroup, "--output", "json" }, cancellationToken);
        }
        else
        {
            credentials = await commandRunner.RunAsync("az", new[] { "ad", "app", "federated-credential", "list", "--id", clientId, "--output", "json" }, cancellationToken);
        }

        if (!credentials.IsSuccess) return FromCommandFailure("Azure federated credential lookup", credentials);
        using var credentialDocument = JsonDocument.Parse(credentials.StandardOutput);
        var expectedSubject = $"system:serviceaccount:{ns}:{serviceAccount}";
        var verified = credentialDocument.RootElement.ValueKind == JsonValueKind.Array
            && credentialDocument.RootElement.EnumerateArray().Any(credential =>
                string.Equals(ReadString(credential, "issuer"), issuer, StringComparison.OrdinalIgnoreCase)
                && ReadString(credential, "subject") == expectedSubject
                && credential.TryGetProperty("audiences", out var audiences)
                && audiences.ValueKind == JsonValueKind.Array
                && audiences.EnumerateArray().Any(audience => audience.GetString() == "api://AzureADTokenExchange"));
        return verified
            ? new WorkloadIdentityVerification(WorkloadTrustStatus.Verified, $"AKS federated credential matches OIDC issuer, audience, and subject {expectedSubject}.")
            {
                Issuer = issuer,
                Subject = expectedSubject,
                Audience = "api://AzureADTokenExchange",
                Source = "AKS federated identity credential"
            }
            : new(WorkloadTrustStatus.Mismatch, $"No Azure federated credential matches {expectedSubject} and the AKS OIDC issuer.");
    }

    private async Task<(string Output, WorkloadIdentityVerification? Verification)> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            var result = await commandRunner.RunAsync(command, arguments, cancellationToken);
            return result.IsSuccess
                ? (result.StandardOutput, null)
                : (string.Empty, FromCommandFailure($"{command} {arguments[0]}", result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return (string.Empty, new(WorkloadTrustStatus.Unavailable, $"{command} verification could not run: {exception.Message}"));
        }
    }

    private static WorkloadIdentityVerification FromCommandFailure(string source, CloudCommandResult result)
    {
        var details = result.StandardError.Trim();
        var denied = details.Contains("denied", StringComparison.OrdinalIgnoreCase)
            || details.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
            || details.Contains("not authorized", StringComparison.OrdinalIgnoreCase)
            || details.Contains("insufficient privileges", StringComparison.OrdinalIgnoreCase)
            || details.Contains("permission", StringComparison.OrdinalIgnoreCase);
        return new(denied ? WorkloadTrustStatus.Denied : WorkloadTrustStatus.Unavailable,
            $"{source} failed: {(string.IsNullOrWhiteSpace(details) ? "no diagnostic returned" : details)}");
    }

    private static bool HasAwsWebIdentityTrust(JsonElement policy, string expectedProvider, string expectedSubject)
    {
        if (!policy.TryGetProperty("Statement", out var statements)) return false;
        if (statements.ValueKind != JsonValueKind.Array) statements = JsonSerializer.SerializeToElement(new[] { statements });
        foreach (var statement in statements.EnumerateArray())
        {
            if (!string.Equals(ReadString(statement, "Effect"), "Allow", StringComparison.OrdinalIgnoreCase)
                || !ContainsValue(statement, "Action", "sts:AssumeRoleWithWebIdentity")
                || !statement.TryGetProperty("Principal", out var principal)
                || !ContainsValue(principal, "Federated", expectedProvider)
                || !statement.TryGetProperty("Condition", out var condition)) continue;
            var hasSubject = ConditionMatches(condition, ":sub", expectedSubject);
            var hasAudience = ConditionMatches(condition, ":aud", "sts.amazonaws.com");
            if (hasSubject && hasAudience) return true;
        }
        return false;
    }

    private static bool ConditionMatches(JsonElement condition, string suffix, string expected)
    {
        foreach (var operation in condition.EnumerateObject())
        {
            if (operation.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (var item in operation.Value.EnumerateObject())
            {
                if (item.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && ContainsJsonValue(item.Value, expected, operation.Name.Equals("StringLike", StringComparison.OrdinalIgnoreCase))) return true;
            }
        }
        return false;
    }

    private static bool ContainsValue(JsonElement parent, string property, string expected)
        => parent.TryGetProperty(property, out var value) && ContainsJsonValue(value, expected, false);

    private static bool ContainsJsonValue(JsonElement value, string expected, bool allowWildcard)
    {
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && Matches(item.GetString(), expected, allowWildcard));
        return value.ValueKind == JsonValueKind.String && Matches(value.GetString(), expected, allowWildcard);
    }

    private static bool Matches(string? actual, string expected, bool allowWildcard)
    {
        if (string.Equals(actual, expected, StringComparison.Ordinal)) return true;
        if (!allowWildcard || string.IsNullOrEmpty(actual)) return false;
        var segments = actual.Split('*');
        var offset = 0;
        foreach (var segment in segments.Where(segment => segment.Length > 0))
        {
            var index = actual.StartsWith('*') || offset > 0 ? expected.IndexOf(segment, offset, StringComparison.Ordinal) : expected.StartsWith(segment, StringComparison.Ordinal) ? 0 : -1;
            if (index < 0) return false;
            offset = index + segment.Length;
        }
        return actual.EndsWith('*') || expected.EndsWith(segments[^1], StringComparison.Ordinal);
    }

    private static JsonElement ReadNestedElement(JsonElement element, params string[] path)
    {
        foreach (var property in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out element)) return default;
        }
        return element;
    }

    private static string? ReadNestedString(JsonElement element, params string[] path)
    {
        var value = ReadNestedElement(element, path);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static string? ReadString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
