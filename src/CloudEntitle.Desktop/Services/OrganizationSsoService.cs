using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using CloudEntitle.OrganizationManagement.Models;
using Microsoft.Identity.Client;

namespace CloudEntitle.Desktop.Services;

public interface IOrganizationSsoService
{
    Task<OrganizationIdentity> SignInAsync(string tenantId, CancellationToken cancellationToken = default);
}

public interface IApiAccessTokenProvider
{
    Task<string> GetApiAccessTokenAsync(CancellationToken cancellationToken = default);
}

public sealed class EntraOrganizationSsoService : IOrganizationSsoService, IApiAccessTokenProvider
{
    private const string ClientIdEnvironmentVariable = "YASK_CIEM_ENTRA_CLIENT_ID";
    private const string ApiScopeEnvironmentVariable = "YASK_CIEM_API_SCOPE";
    private readonly HttpClient _httpClient;
    private readonly string? _clientId;
    private readonly string? _apiScope;
    private IPublicClientApplication? _application;
    private IAccount? _signedInAccount;

    public EntraOrganizationSsoService(HttpClient? httpClient = null, string? clientId = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _clientId = clientId ?? Environment.GetEnvironmentVariable(ClientIdEnvironmentVariable);
        _apiScope = Environment.GetEnvironmentVariable(ApiScopeEnvironmentVariable);
    }

    public async Task<OrganizationIdentity> SignInAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(tenantId, out var parsedTenantId) || parsedTenantId == Guid.Empty)
            throw new ArgumentException("Enter a valid Microsoft Entra tenant ID.", nameof(tenantId));
        if (!Guid.TryParse(_clientId, out var parsedClientId) || parsedClientId == Guid.Empty)
            throw new InvalidOperationException($"Configure the desktop public-client app registration in {ClientIdEnvironmentVariable} before signing in.");

        var application = PublicClientApplicationBuilder.Create(parsedClientId.ToString())
            .WithAuthority(AzureCloudInstance.AzurePublic, parsedTenantId.ToString())
            .WithDefaultRedirectUri()
            .Build();
        var result = await application.AcquireTokenInteractive(new[] { "User.Read" })
            .WithPrompt(Prompt.SelectAccount)
            .ExecuteAsync(cancellationToken);
        _application = application;
        _signedInAccount = result.Account;
        if (!string.IsNullOrWhiteSpace(_apiScope))
        {
            try
            {
                await application.AcquireTokenSilent(new[] { _apiScope }, result.Account).ExecuteAsync(cancellationToken);
            }
            catch (MsalUiRequiredException)
            {
                await application.AcquireTokenInteractive(new[] { _apiScope })
                    .WithAccount(result.Account)
                    .ExecuteAsync(cancellationToken);
            }
        }

        var claims = result.ClaimsPrincipal;
        var claimTenant = claims.FindFirst("tid")?.Value ?? result.TenantId;
        var objectId = claims.FindFirst("oid")?.Value;
        var subject = claims.FindFirst("sub")?.Value;
        if (!Guid.TryParse(claimTenant, out var authenticatedTenant)
            || authenticatedTenant != parsedTenantId
            || !Guid.TryParse(objectId, out var userId)
            || userId == Guid.Empty
            || string.IsNullOrWhiteSpace(subject))
            throw new UnauthorizedAccessException("Entra sign-in returned incomplete or mismatched tenant identity claims.");

        var email = claims.FindFirst(ClaimTypes.Email)?.Value
            ?? claims.FindFirst("email")?.Value
            ?? claims.FindFirst("preferred_username")?.Value
            ?? claims.FindFirst("upn")?.Value
            ?? result.Account.Username;
        if (string.IsNullOrWhiteSpace(email))
            throw new UnauthorizedAccessException("Entra sign-in did not return an email or user principal name for organization membership matching.");

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me?$select=mail,userPrincipalName");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", result.AccessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Microsoft Graph profile lookup failed ({(int)response.StatusCode}); grant the desktop app the delegated User.Read permission.");
        using var profile = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (profile.RootElement.TryGetProperty("mail", out var mail) && !string.IsNullOrWhiteSpace(mail.GetString()))
            email = mail.GetString()!;
        else if (profile.RootElement.TryGetProperty("userPrincipalName", out var userPrincipalName) && !string.IsNullOrWhiteSpace(userPrincipalName.GetString()))
            email = userPrincipalName.GetString()!;

        return new OrganizationIdentity(userId, subject, authenticatedTenant.ToString(), email.Trim());
    }

    public async Task<string> GetApiAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_apiScope))
            throw new InvalidOperationException($"Configure {ApiScopeEnvironmentVariable} with the API's delegated scope before synchronizing central audit events.");
        if (_application is null || _signedInAccount is null)
            throw new InvalidOperationException("Sign in with Entra ID before synchronizing central audit events.");

        var result = await _application.AcquireTokenSilent(new[] { _apiScope }, _signedInAccount)
            .ExecuteAsync(cancellationToken);
        return result.AccessToken;
    }
}
