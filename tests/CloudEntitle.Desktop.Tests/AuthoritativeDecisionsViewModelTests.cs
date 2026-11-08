using System.Net.Http;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Desktop.ViewModels;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
using Xunit;

namespace CloudEntitle.Desktop.Tests;

public sealed class AuthoritativeDecisionsViewModelTests
{
    [Fact]
    public async Task AuthoritativeDecisionUi_NeverReadsProviderAtContextSetupOrSavedReadsAndRequiresNewConsent()
    {
        var api = new Api();
        var vm = new AuthoritativeDecisionsViewModel(api);
        vm.SetContext(Guid.NewGuid(), "tenant|user|gcp-project", true, true);
        Assert.Equal(0, api.Captures);
        Assert.Equal(0, api.SourceReads);
        await vm.LoadSourcesAsync();
        vm.SelectedSource = Assert.Single(vm.Sources);
        await vm.CaptureAsync();
        Assert.Equal(0, api.Captures);
        Assert.Contains("Explicitly authorize", vm.Status);
        vm.ReadConsent = true;
        await vm.CaptureAsync();
        Assert.Equal(1, api.Captures);
        Assert.True(api.Request!.ReadConsent);
        Assert.False(vm.ReadConsent);
        Assert.Equal(vm.SelectedSource.Principals[0], api.Request.Principal);
        Assert.Equal(vm.SelectedSource.PrivilegedPermissions[0], api.Request.Permission);
        Assert.Single(vm.History);
        await vm.CaptureAsync();
        Assert.Equal(1, api.Captures);
        await vm.LoadHistoryAsync();
        vm.Before = vm.History[0];
        vm.After = vm.History[1];
        await vm.AnalyzeAsync();
        Assert.Equal(1, api.Captures);
        Assert.NotNull(vm.Report);
        Assert.False(vm.ReadConsent);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("principal")]
    [InlineData("organization")]
    [InlineData("identity")]
    [InlineData("permissions")]
    [InlineData("binding")]
    [InlineData("signout")]
    [InlineData("cancel")]
    public async Task AuthoritativeDecisionUi_CancelsCaptureAndDiscardsLateResultsWhenContextChanges(string change)
    {
        var api = new Api { PendingCapture = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var vm = new AuthoritativeDecisionsViewModel(api);
        var organization = Guid.NewGuid();
        vm.SetContext(organization, "tenant|user|gcp-project", true, true);
        await vm.LoadSourcesAsync();
        vm.SelectedSource = vm.Sources[0];
        vm.ReadConsent = true;
        var capture = vm.CaptureAsync();
        Assert.True(vm.Busy);
        switch (change)
        {
            case "source": vm.SelectedSource = api.Source with { SourceId = "other-source" }; break;
            case "principal": vm.Principal = "other@example.invalid"; break;
            case "organization": vm.SetContext(Guid.NewGuid(), "tenant|user|gcp-project", true, true); break;
            case "identity": vm.SetContext(organization, "tenant|another-user|gcp-project", true, true); break;
            case "permissions": vm.SetContext(organization, "tenant|user|gcp-project", true, false); break;
            case "binding": vm.SetContext(organization, "tenant|user|other-project", true, true); break;
            case "signout": vm.SetContext(null, "", false, false); break;
            case "cancel": vm.Cancel(); break;
        }
        Assert.True(api.Token.IsCancellationRequested);
        Assert.False(vm.Busy);
        Assert.False(vm.ReadConsent);
        api.PendingCapture.SetResult(api.Snapshot());
        await capture;
        Assert.Empty(vm.History);
        Assert.Null(vm.Report);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthoritativeDecisionUi_DiscardsLateSavedHistoryOrAnalysisOnCancelAndReselect(bool analysis)
    {
        var api = new Api();
        var vm = new AuthoritativeDecisionsViewModel(api);
        vm.SetContext(Guid.NewGuid(), "tenant|user", true, true);
        await vm.LoadSourcesAsync();
        vm.SelectedSource = api.Source;
        await vm.LoadHistoryAsync();
        vm.Before = vm.History[0];
        vm.After = vm.History[1];
        Task pending;
        if (analysis)
        {
            api.PendingAnalysis = new(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = vm.AnalyzeAsync();
        }
        else
        {
            api.PendingHistory = new(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = vm.LoadHistoryAsync();
        }
        vm.SelectedSource = null;
        vm.SelectedSource = api.Source;
        Assert.True(api.Token.IsCancellationRequested);
        if (analysis) api.PendingAnalysis!.SetResult(api.Analysis());
        else api.PendingHistory!.SetResult([api.Snapshot(), api.Snapshot()]);
        await pending;
        Assert.Empty(vm.History);
        Assert.Null(vm.Report);
        Assert.Null(vm.Before);
        Assert.Null(vm.After);
        Assert.False(vm.ReadConsent);
    }

    [Fact]
    public async Task AuthoritativeDecisionUi_DiscardsLateConnectionListAndClearsResultsAfterPermissionLoss()
    {
        var api = new Api { PendingSources = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var vm = new AuthoritativeDecisionsViewModel(api);
        var organization = Guid.NewGuid();
        vm.SetContext(organization, "tenant|user", true, true);
        var pending = vm.LoadSourcesAsync();
        vm.SetContext(organization, "tenant|user", false, false);
        api.PendingSources.SetResult([api.Source]);
        await pending;
        Assert.Empty(vm.Sources);
        Assert.Null(vm.SelectedSource);
        await vm.CaptureAsync();
        Assert.Equal(0, api.Captures);
    }

    [Fact]
    public async Task AuthoritativeDecisionUi_NativeReadFailureConsumesConsentAndCannotBecomeConfirmation()
    {
        var api = new Api { ThrowCapture = true };
        var vm = new AuthoritativeDecisionsViewModel(api);
        vm.SetContext(Guid.NewGuid(), "tenant|user", true, true);
        await vm.LoadSourcesAsync();
        vm.SelectedSource = api.Source;
        vm.ReadConsent = true;
        await vm.CaptureAsync();
        Assert.False(vm.ReadConsent);
        Assert.False(vm.Busy);
        Assert.Null(vm.Report);
        Assert.Empty(vm.History);
        Assert.Contains("No confirmation", vm.Status);
        await vm.CaptureAsync();
        Assert.Equal(1, api.Captures);
    }

    private sealed class Api : ISharedEffectiveDecisionApi
    {
        public EffectiveDecisionSource Source { get; } = new("native-gcp", CloudProvider.Gcp, "sample-project-123",
            "collector@example.invalid", new string('A', 64), true, "Exact unconditional project IAM decision only.",
            ["alice@example.invalid"], ["resourcemanager.projects.setIamPolicy"],
            "//cloudresourcemanager.googleapis.com/projects/sample-project-123");
        public int Captures { get; private set; }
        public int SourceReads { get; private set; }
        public bool ThrowCapture { get; init; }
        public CaptureEffectiveDecisionRequest? Request { get; private set; }
        public CancellationToken Token { get; private set; }
        public TaskCompletionSource<EffectiveDecisionSnapshot>? PendingCapture { get; init; }
        public TaskCompletionSource<IReadOnlyList<EffectiveDecisionSource>>? PendingSources { get; init; }
        public TaskCompletionSource<IReadOnlyList<EffectiveDecisionSnapshot>>? PendingHistory { get; set; }
        public TaskCompletionSource<DecisionEscalationReport>? PendingAnalysis { get; set; }
        public EffectiveDecisionSnapshot Snapshot()
        {
            var now = DateTimeOffset.UtcNow;
            return new(Guid.NewGuid(), Source.Provider, Source.AccountId, Source.SourceId, Source.ConfigurationSha256,
                Source.CollectorIdentity, Source.Principals[0], Source.PrivilegedPermissions[0], Source.FullResourceName,
                new string('B', 64), now, now.AddSeconds(-1), now, "NOT_GRANTED", "Partial", "test",
                new string('C', 64), ["test-etag"], ["partial"]);
        }
        public DecisionEscalationReport Analysis() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Inconclusive",
            Source.Principals[0], Source.PrivilegedPermissions[0], Source.FullResourceName, new string('B', 64),
            DateTimeOffset.UtcNow, ["partial"], "No confirmation is inferred from partial evidence.");
        public Task<IReadOnlyList<EffectiveDecisionSource>> GetEffectiveDecisionSourcesAsync(Guid organization, CancellationToken token = default)
        {
            Token = token; SourceReads++;
            return PendingSources?.Task ?? Task.FromResult<IReadOnlyList<EffectiveDecisionSource>>([Source]);
        }
        public Task<EffectiveDecisionSnapshot> CaptureEffectiveDecisionAsync(Guid organization, CaptureEffectiveDecisionRequest request,
            CancellationToken token = default)
        {
            Token = token; Captures++; Request = request;
            if (ThrowCapture) throw new HttpRequestException("Test native source failure.");
            return PendingCapture?.Task ?? Task.FromResult(Snapshot());
        }
        public Task<IReadOnlyList<EffectiveDecisionSnapshot>> GetEffectiveDecisionHistoryAsync(Guid organization, string source, CancellationToken token = default)
        {
            Token = token;
            return PendingHistory?.Task ?? Task.FromResult<IReadOnlyList<EffectiveDecisionSnapshot>>([Snapshot(), Snapshot()]);
        }
        public Task<DecisionEscalationReport> AnalyzeDecisionEscalationAsync(Guid organization, AnalyzeDecisionEscalationRequest request,
            CancellationToken token = default)
        {
            Token = token;
            return PendingAnalysis?.Task ?? Task.FromResult(Analysis());
        }
    }
}
