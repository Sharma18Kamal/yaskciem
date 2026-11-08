using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;

namespace CloudEntitle.LicenseServer.Services;

public sealed record ContinuousChangeSource(string Alias, CloudProvider Provider, string AccountId, string SourceId,
    string ConfigurationSha256, Uri Endpoint, string BearerToken, int PollSeconds);

public static class ContinuousChangeSourceConfiguration
{
    public static ContinuousChangeSource? Resolve(IConfiguration configuration, Guid tenant, Guid organization, string alias)
    {
        if (!ContinuousChangeFeedValidation.SafeId(alias, 64)
            || alias != alias.ToLowerInvariant()
            || alias.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return null;
        var section = configuration.GetSection($"ContinuousChangeFeeds:Organizations:{tenant:D}:{organization:D}:Sources:{alias}");
        if (!section.GetValue<bool>("Enabled") || !section.GetValue<bool>("Connected")) return null;
        if (!Enum.TryParse<CloudProvider>(section["Provider"], out var provider)
            || provider is not (CloudProvider.Azure or CloudProvider.Aws or CloudProvider.Gcp)
            || !ContinuousChangeFeedValidation.SafeId(section["AccountId"], 128)
            || !ContinuousChangeFeedValidation.SafeId(section["SourceId"], 128)
            || !ContinuousChangeFeedValidation.SafeId(section["SourceVersion"], 64)
            || !Uri.TryCreate(section["Endpoint"], UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps || endpoint.Port != 443
            || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment) || endpoint.IsLoopback
            || IPAddress.TryParse(endpoint.Host, out _) || !IsAllowedHost(section, endpoint.Host))
            return null;
        var secret = section["BearerToken"];
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > 4096 || secret.Any(char.IsControl)) return null;
        var poll = section.GetValue<int?>("PollSeconds") ?? 60;
        if (poll is < 15 or > 3600) return null;
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{provider}|{section["AccountId"]}|{section["SourceId"]}|{section["SourceVersion"]}|{endpoint.AbsoluteUri}|{secret}")));
        return new(alias, provider, section["AccountId"]!, section["SourceId"]!, fingerprint, endpoint, secret, poll);
    }

    private static bool IsAllowedHost(IConfigurationSection section, string host)
        => section.GetSection("AllowedHosts").GetChildren().Any(item =>
            !string.IsNullOrWhiteSpace(item.Value) && string.Equals(item.Value, host, StringComparison.OrdinalIgnoreCase)
            && !item.Value.Contains('*'));
}

public interface IContinuousChangeBridge
{
    Task<ContinuousChangeBatch> PollAsync(ContinuousChangeSource source, ContinuousChangeFeedStatus state, CancellationToken token);
}

public sealed class HttpsContinuousChangeBridge(IHttpClientFactory clients) : IContinuousChangeBridge
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public async Task<ContinuousChangeBatch> PollAsync(ContinuousChangeSource source, ContinuousChangeFeedStatus state,
        CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var query = $"epoch={state.SourceEpoch:D}&sourceId={Uri.EscapeDataString(state.SourceId)}&limit={ContinuousChangeFeedValidation.MaximumChanges}";
        if (state.Cursor is not null) query += "&cursor=" + Uri.EscapeDataString(state.Cursor);
        using var request = new HttpRequestMessage(HttpMethod.Get, new UriBuilder(source.Endpoint) { Query = query }.Uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", source.BearerToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await clients.CreateClient("ContinuousChangeBridge")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new ChangeBridgeException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "bridge_auth",
                HttpStatusCode.TooManyRequests => "bridge_throttled",
                _ => "bridge_response_uncertain"
            });
        if (response.Content.Headers.ContentType?.MediaType != "application/json"
            || response.Content.Headers.ContentLength > ContinuousChangeFeedValidation.MaximumBatchBytes)
            throw new ChangeBridgeException("bridge_invalid_response");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, timeout.Token)) != 0)
        {
            if (buffer.Length + count > ContinuousChangeFeedValidation.MaximumBatchBytes)
                throw new ChangeBridgeException("bridge_response_too_large");
            buffer.Write(chunk, 0, count);
        }
        buffer.Position = 0;
        var batch = await JsonSerializer.DeserializeAsync<ContinuousChangeBatch>(buffer, Options, timeout.Token)
            ?? throw new ChangeBridgeException("bridge_invalid_response");
        ContinuousChangeFeedValidation.Validate(batch, state);
        return batch;
    }
}

public sealed class ChangeBridgeException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
