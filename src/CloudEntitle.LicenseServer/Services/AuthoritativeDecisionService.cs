using System.Collections.Concurrent;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.Integration.Services;

namespace CloudEntitle.LicenseServer.Services;

public interface IEffectiveDecisionService
{
    IReadOnlyList<EffectiveDecisionSource> Sources(Guid tenant, Guid organization);
    EffectiveDecisionSource Source(Guid tenant, Guid organization, string sourceId);
    Task<EffectiveDecisionSnapshot> CaptureAsync(Guid tenant, Guid organization, Guid actor,
        CaptureEffectiveDecisionRequest request, CancellationToken token);
    Task<IReadOnlyList<EffectiveDecisionSnapshot>> HistoryAsync(Guid tenant, Guid organization, string source, CancellationToken token);
    Task<EffectiveDecisionSnapshot?> SnapshotAsync(Guid tenant, Guid organization, string source, Guid snapshot, CancellationToken token);
    Task<DecisionEscalationReport> AnalyzeAsync(Guid tenant, Guid organization,
        AnalyzeDecisionEscalationRequest request, CancellationToken token);
    Task<DecisionEscalationReport?> AnalysisAsync(Guid tenant, Guid organization, string source, Guid analysis, CancellationToken token);
}

public sealed class AuthoritativeDecisionService(
    SharedWorkspaceStore store, IGcpAuthoritativeDecisionCollector collector,
    IReadOnlyList<EffectiveDecisionConnection> connections) : IEffectiveDecisionService
{
    private readonly ConcurrentDictionary<(Guid, Guid, string), SemaphoreSlim> _captures = new();
    public IReadOnlyList<EffectiveDecisionSource> Sources(Guid tenant, Guid organization)
        => connections.Where(c => c.TenantId == tenant && c.OrganizationId == organization).Select(c => c.Describe()).ToArray();
    public EffectiveDecisionSource Source(Guid tenant, Guid organization, string sourceId)
        => Connection(tenant, organization, sourceId).Describe();
    private EffectiveDecisionConnection Connection(Guid tenant, Guid organization, string sourceId)
        => connections.SingleOrDefault(c => c.TenantId == tenant && c.OrganizationId == organization && c.SourceId == sourceId)
            ?? throw new KeyNotFoundException("No source exists in this organization.");

    public async Task<EffectiveDecisionSnapshot> CaptureAsync(Guid tenant, Guid organization, Guid actor,
        CaptureEffectiveDecisionRequest request, CancellationToken token)
    {
        var source = Connection(tenant, organization, request.SourceId);
        source.Validate();
        ValidateSample(source, request);
        var gate = _captures.GetOrAdd((tenant, organization, source.SourceId), _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, token)) throw new InvalidOperationException("A capture for this source is already in progress.");
        try
        {
            var capture = await collector.CaptureAsync(source, request, token);
            token.ThrowIfCancellationRequested();
            await store.SaveNativeEffectiveDecisionAsync(tenant, organization, actor, capture, token);
            return capture.Snapshot;
        }
        finally { gate.Release(); }
    }

    public static void ValidateSample(EffectiveDecisionConnection source, CaptureEffectiveDecisionRequest request)
    {
        if (request is null || !request.ReadConsent || !source.Enabled
            || request.SourceId != source.SourceId || request.FullResourceName != source.FullResourceName
            || !source.Principals.Contains(request.Principal, StringComparer.Ordinal)
            || !source.PrivilegedPermissions.Contains(request.Permission, StringComparer.Ordinal))
            throw new ArgumentException("Explicit consent and the exact configured privileged sample are required.");
    }

    public Task<IReadOnlyList<EffectiveDecisionSnapshot>> HistoryAsync(Guid tenant, Guid organization, string source, CancellationToken token)
    {
        _ = Connection(tenant, organization, source);
        return store.GetEffectiveDecisionHistoryAsync(tenant, organization, source, token);
    }
    public async Task<EffectiveDecisionSnapshot?> SnapshotAsync(Guid tenant, Guid organization, string source, Guid snapshot, CancellationToken token)
    {
        _ = Connection(tenant, organization, source);
        return (await store.GetNativeEffectiveDecisionAsync(tenant, organization, source, snapshot, token))?.Snapshot;
    }
    public async Task<DecisionEscalationReport> AnalyzeAsync(Guid tenant, Guid organization,
        AnalyzeDecisionEscalationRequest request, CancellationToken token)
    {
        if (request is null || request.BeforeSnapshotId == Guid.Empty || request.AfterSnapshotId == Guid.Empty
            || request.BeforeSnapshotId == request.AfterSnapshotId) throw new ArgumentException("Two distinct saved observations are required.");
        var source = Connection(tenant, organization, request.SourceId);
        var before = await store.GetNativeEffectiveDecisionAsync(tenant, organization, request.SourceId, request.BeforeSnapshotId, token)
            ?? throw new KeyNotFoundException();
        var after = await store.GetNativeEffectiveDecisionAsync(tenant, organization, request.SourceId, request.AfterSnapshotId, token)
            ?? throw new KeyNotFoundException();
        var verified = SharedWorkspaceStore.VerifyNativeEffectiveDecision(before) && SharedWorkspaceStore.VerifyNativeEffectiveDecision(after);
        var report = new DecisionEscalationAnalysisService().Analyze(source.Describe(), before.Snapshot, after.Snapshot,
            verified, DateTimeOffset.UtcNow);
        return await store.SaveDecisionEscalationAsync(tenant, organization, request.SourceId, after.Snapshot, report, token);
    }
    public Task<DecisionEscalationReport?> AnalysisAsync(Guid tenant, Guid organization, string source, Guid analysis, CancellationToken token)
    {
        _ = Connection(tenant, organization, source);
        return store.GetDecisionEscalationAsync(tenant, organization, source, analysis, token);
    }
}

public static class AuthoritativeDecisionRegistration
{
    public static IServiceCollection AddAuthoritativeDecisionServices(this IServiceCollection services, IConfiguration configuration)
    {
        var sources = configuration.GetSection("AuthoritativeDecisions:Sources").Get<EffectiveDecisionConnection[]>() ?? [];
        if (sources.Length > 1000) throw new ArgumentException("Too many authoritative decision sources.");
        foreach (var source in sources) source.Validate();
        if (sources.GroupBy(s => (s.TenantId, s.OrganizationId, s.SourceId)).Any(g => g.Count() > 1))
            throw new ArgumentException("Authoritative decision source identifiers must be unique within an organization.");
        services.AddSingleton<IReadOnlyList<EffectiveDecisionConnection>>(sources);
        // No provider request or token acquisition occurs during registration or sign-in.
        services.AddHttpClient("AuthoritativeGcpDecision", client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            .RemoveAllLoggers();
        services.AddSingleton<IGcpAuthoritativeDecisionCollector>(provider => new GcpAuthoritativeDecisionCollector(
            provider.GetRequiredService<IHttpClientFactory>(), Environment.GetEnvironmentVariable));
        services.AddSingleton<IEffectiveDecisionService, AuthoritativeDecisionService>();
        return services;
    }
}
