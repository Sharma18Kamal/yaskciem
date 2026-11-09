using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Integration.Contracts;
using Serilog;

namespace CloudEntitle.Desktop.Services;

public sealed class SubscriptionAccessService(HttpClient client, IApiAccessTokenProvider tokens)
{
    public event EventHandler? Changed;
    public string Status { get; private set; } = "30-day trial: sign in under Organizations, then start trial";

    public async Task<SubscriptionStatus> RefreshAsync(bool registerTrial = false, CancellationToken token = default)
    {
        try
        {
            if (client.BaseAddress is not { Scheme: "https" })
                throw new InvalidOperationException("Configure YASK_CIEM_API_BASE_URL with the License Server HTTPS URL, then restart.");
            var accessToken = await tokens.GetApiAccessTokenAsync(token);
            using var request = new HttpRequestMessage(registerTrial ? HttpMethod.Post : HttpMethod.Get,
                registerTrial ? "api/v1/subscription/trial" : "api/v1/subscription");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await client.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<SubscriptionStatus>(cancellationToken: token)
                ?? throw new InvalidOperationException("License Server returned an empty entitlement.");
            if (result.TenantId == Guid.Empty || result.Status is not ("Trial" or "Licensed" or "Expired" or "NotRegistered")
                || result.CanUseLicensedFeatures != (result.Status is "Trial" or "Licensed")
                || result.Status == "Trial" && (result.TrialExpiresAtUtc is null || result.TrialExpiresAtUtc <= result.EvaluatedAtUtc)
                || result.Status == "Licensed" && (result.LicensedUntilUtc is null || result.LicensedUntilUtc <= result.EvaluatedAtUtc))
                throw new InvalidOperationException("License Server returned an invalid entitlement.");
            Status = result.Status switch
            {
                "Trial" => $"30-day trial ends {result.TrialExpiresAtUtc:u}",
                "Licensed" => $"Licensed until {result.LicensedUntilUtc:u}",
                "Expired" => "Trial expired: existing data is read-only",
                _ => "30-day trial not started"
            };
            Changed?.Invoke(this, EventArgs.Empty);
            return result;
        }
        catch (OrganizationSignInRequiredException)
        {
            Status = "License Server sign-in required: open Organizations and select Sign in";
            Changed?.Invoke(this, EventArgs.Empty);
            Log.Information("Subscription validation requires organization sign-in; licensed operations are blocked.");
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
        {
            Status = "License validation unavailable: existing data is read-only";
            Changed?.Invoke(this, EventArgs.Empty);
            Log.Warning("Subscription validation failed ({ErrorType}); licensed operations are blocked.", exception.GetType().Name);
            throw;
        }
    }

    public async Task RequireAsync(CancellationToken token = default)
    {
        if (!(await RefreshAsync(token: token)).CanUseLicensedFeatures)
            throw new InvalidOperationException("An active trial or license is required. Start the 30-day trial after organization sign-in, or contact the License Server administrator.");
    }
}

public sealed class LicensedCloudCommandRunner(ICloudCommandRunner inner, SubscriptionAccessService subscription) : ICloudCommandRunner
{
    public async Task<CloudCommandResult> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        if (!IsSessionOperation(command, arguments))
            await subscription.RequireAsync(cancellationToken);
        return await inner.RunAsync(command, arguments, cancellationToken);
    }

    internal static bool IsSessionOperation(string command, IReadOnlyList<string> arguments)
    {
        var offset = command == "aws" && arguments.Count >= 2 && arguments[0] == "--profile" ? 2 : 0;
        var verb = arguments.Count > offset ? arguments[offset] : null;
        var action = arguments.Count > offset + 1 ? arguments[offset + 1] : null;
        return command switch
        {
            "az" => verb == "logout" && arguments.Count == 1
                || verb == "account" && action is "show" or "list",
            "aws" => verb == "sts" && action == "get-caller-identity"
                || verb == "configure" && action == "list-profiles"
                || verb == "sso" && action == "logout",
            "gcloud" => verb == "auth" && action is "list" or "revoke",
            _ => false
        };
    }
}
