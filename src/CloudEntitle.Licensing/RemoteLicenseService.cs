namespace CloudEntitle.Licensing;

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudEntitle.Application.Interfaces;

public sealed class RemoteLicenseService : ILicenseService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;

    public RemoteLicenseService(HttpClient httpClient, Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("License validation endpoints must use HTTPS.", nameof(endpoint));
        }

        _httpClient = httpClient;
        _endpoint = endpoint;
    }

    public async Task<LicenseValidationResult> ValidateAsync(string licenseKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(licenseKey);
        using var response = await _httpClient.PostAsJsonAsync(_endpoint, new { licenseKey }, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LicenseValidationResult>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("The license service returned an empty response.");
    }

    public async Task<bool> ActivateAsync(string licenseKey, CancellationToken cancellationToken = default)
        => (await SendActionAsync("activate", licenseKey, cancellationToken)).IsValid;

    public async Task<bool> DeactivateAsync(string licenseKey, CancellationToken cancellationToken = default)
        => (await SendActionAsync("deactivate", licenseKey, cancellationToken)).IsValid;

    private async Task<LicenseValidationResult> SendActionAsync(string action, string licenseKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(licenseKey);
        var endpoint = new Uri(_endpoint, action);
        using var response = await _httpClient.PostAsJsonAsync(endpoint, new { licenseKey }, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LicenseValidationResult>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("The license service returned an empty response.");
    }
}
