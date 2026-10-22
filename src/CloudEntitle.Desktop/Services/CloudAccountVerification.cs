using System.Text.Json;
using CloudEntitle.Application.Interfaces;

namespace CloudEntitle.Desktop.Services;

public sealed record CloudAccountVerification(string Provider, string AccountId, string Identity, string TenantId, DateTime CheckedAtUtc);

public static class CloudAccountVerifier
{
    public static void RequireSameIdentity(CloudAccountVerification expected, CloudAccountVerification actual)
    {
        if (!string.Equals(expected.Provider, actual.Provider, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected.AccountId, actual.AccountId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected.TenantId, actual.TenantId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected.Identity, actual.Identity, StringComparison.Ordinal))
            throw new InvalidOperationException("Cloud account, tenant or identity changed since verification. Verify the approved scope again before scanning.");
    }

    public static async Task<CloudAccountVerification> VerifyAsync(
        ICloudCommandRunner runner, LiveScanAccountRecord account, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(account.AccountId) || account.AccountId.StartsWith('-'))
            throw new ArgumentException("Select an account with a valid provider account ID.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        async Task<JsonDocument> ReadAsync(string command, string[] arguments)
        {
            var result = await runner.RunAsync(command, arguments, timeout.Token);
            if (!result.IsSuccess)
                throw new InvalidOperationException($"{account.Provider} verification failed. Ask the administrator to check CLI authentication and read permissions; no credentials or provider response bodies are displayed.");
            try { return JsonDocument.Parse(result.StandardOutput); }
            catch (JsonException) { throw new InvalidOperationException($"{account.Provider} returned an invalid verification response."); }
        }
        static string Required(JsonElement root, string property)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                throw new InvalidOperationException($"Verification response is missing {property}; account identity was not verified.");
            return value.GetString()!;
        }

        string actual;
        string identity;
        var tenant = string.Empty;
        switch (account.Provider.ToUpperInvariant())
        {
            case "AZURE":
                using (var document = await ReadAsync("az", ["account", "show", "--subscription", account.AccountId, "--output", "json"]))
                {
                    if (!string.Equals(Required(document.RootElement, "id"), account.AccountId, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Azure CLI subscription does not match the selected account.");
                    tenant = Required(document.RootElement, "tenantId");
                    if (!document.RootElement.TryGetProperty("user", out var user))
                        throw new InvalidOperationException("Azure did not return the authenticated CLI identity.");
                    identity = Required(user, "name");
                    if (!string.Equals(Required(document.RootElement, "state"), "Enabled", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The selected Azure subscription is not enabled.");
                }
                using (var document = await ReadAsync("az", ["rest", "--method", "get", "--url",
                    $"https://management.azure.com/subscriptions/{Uri.EscapeDataString(account.AccountId)}?api-version=2022-12-01", "--subscription", account.AccountId, "--output", "json"]))
                {
                    actual = Required(document.RootElement, "subscriptionId");
                    if (!string.Equals(Required(document.RootElement, "tenantId"), tenant, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Azure returned a different tenant from the selected CLI subscription.");
                }
                break;
            case "AWS":
                var arguments = new List<string>();
                if (!string.IsNullOrWhiteSpace(account.Profile))
                    arguments.AddRange(["--profile", account.Profile]);
                arguments.AddRange(["sts", "get-caller-identity", "--output", "json", "--no-cli-pager"]);
                using (var document = await ReadAsync("aws", arguments.ToArray()))
                {
                    actual = Required(document.RootElement, "Account");
                    identity = Required(document.RootElement, "Arn");
                }
                break;
            case "GCP":
                using (var config = await ReadAsync("gcloud", ["config", "list", "--format=json"]))
                {
                    if (config.RootElement.TryGetProperty("auth", out var authConfig)
                        && (authConfig.TryGetProperty("impersonate_service_account", out _)
                            || authConfig.TryGetProperty("access_token_file", out _)
                            || authConfig.TryGetProperty("credential_file_override", out _)))
                        throw new NotSupportedException("GCP CLI impersonation or credential/token-file overrides require separate effective-identity verification and are not supported by this check.");
                }
                using (var auth = await ReadAsync("gcloud", ["auth", "list", "--filter=status:ACTIVE", "--format=json"]))
                {
                    if (auth.RootElement.ValueKind != JsonValueKind.Array || auth.RootElement.GetArrayLength() != 1)
                        throw new InvalidOperationException("Select exactly one active Google Cloud CLI identity.");
                    identity = Required(auth.RootElement[0], "account");
                }
                using (var document = await ReadAsync("gcloud", ["projects", "describe", account.AccountId, "--account", identity, "--format=json"]))
                    actual = Required(document.RootElement, "projectId");
                break;
            default:
                throw new NotSupportedException("Account verification supports Azure subscriptions, AWS accounts and GCP projects, not Kubernetes contexts or organization roster rows.");
        }
        if (!string.Equals(actual, account.AccountId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Authenticated scope does not match the selected account. Refresh discovery and ask the administrator to correct the CLI profile; do not bind or scan this account.");
        return new(account.Provider, actual, identity, tenant, DateTime.UtcNow);
    }
}
