using CloudEntitle.Application.Interfaces;
using CloudEntitle.Desktop.ViewModels;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using Xunit;

namespace CloudEntitle.Desktop.Tests;

public sealed class EffectivePermissionSimulatorTests
{
    [Theory]
    [InlineData(AccessType.Allow, AssignmentType.Direct, AccessDecision.Allow, EvaluationStatus.FullyEvaluated, true)]
    [InlineData(AccessType.Deny, AssignmentType.Direct, AccessDecision.Deny, EvaluationStatus.FullyEvaluated, true)]
    [InlineData(AccessType.Allow, AssignmentType.Conditional, AccessDecision.Conditional, EvaluationStatus.ConditionRequired, true)]
    [InlineData(AccessType.Allow, AssignmentType.Group, AccessDecision.Unknown, EvaluationStatus.PartiallyEvaluated, true)]
    [InlineData(AccessType.Allow, AssignmentType.Direct, AccessDecision.NotApplicable, EvaluationStatus.FullyEvaluated, false)]
    public async Task Simulator_ShouldReturnEvaluatorDecisionAndStatus(
        AccessType accessType,
        AssignmentType assignmentType,
        AccessDecision expectedDecision,
        EvaluationStatus expectedStatus,
        bool matchesSelectedPermission)
    {
        var account = new CloudAccount { Id = Guid.NewGuid(), Name = "Demo GCP", CloudProvider = CloudProviderType.Gcp, AccountType = AccountType.Project };
        var principal = new CloudPrincipal { Id = Guid.NewGuid(), AccountId = account.Id, CloudProvider = CloudProviderType.Gcp, Name = "sim-user", PrincipalId = "sim-user", IdentityType = IdentityType.User };
        var resource = new CloudResource { Id = Guid.NewGuid(), AccountId = account.Id, Name = "demo-resource", ResourceId = "projects/demo/resources/item", Type = "Resource" };
        var permission = new CloudPermission { Id = Guid.NewGuid(), AccountId = account.Id, Name = "Read item", Action = "resource.read", Resource = "*", AccessType = accessType };
        var entitlement = new Entitlement
        {
            Id = Guid.NewGuid(), PrincipalId = principal.Id, CloudAccountId = account.Id, PermissionId = matchesSelectedPermission ? permission.Id : Guid.NewGuid(),
            ResourceId = resource.Id, Scope = resource.ResourceId, AssignmentType = assignmentType, Source = "simulator test"
        };
        var snapshot = new DemoSnapshot(
            new[] { account }, new[] { principal }, new[] { resource }, new[] { entitlement },
            Array.Empty<CloudEntitle.Domain.Entities.EffectiveAccess>(), Array.Empty<RiskFindingModel>(), Array.Empty<RecommendationModel>(), Array.Empty<Scan>())
        {
            Permissions = new[] { permission }
        };
        var viewModel = new EffectiveAccessViewModel(new SimulatorDemoDataService(snapshot));

        await viewModel.RefreshAsync();
        await viewModel.SimulatePermissionAsync();

        Assert.NotNull(viewModel.SimulationResult);
        Assert.Equal(expectedDecision.ToString(), viewModel.SimulationResult.Decision);
        Assert.Equal(expectedStatus.ToString(), viewModel.SimulationResult.EvaluationStatus);
    }

    [Fact]
    public async Task Simulator_ShouldRemainUnavailableForLiveDataWithoutNormalizedEntitlements()
    {
        var snapshot = new DemoSnapshot(
            Array.Empty<CloudAccount>(), Array.Empty<CloudPrincipal>(), Array.Empty<CloudResource>(), Array.Empty<Entitlement>(),
            Array.Empty<CloudEntitle.Domain.Entities.EffectiveAccess>(), Array.Empty<RiskFindingModel>(), Array.Empty<RecommendationModel>(), Array.Empty<Scan>());
        var viewModel = new EffectiveAccessViewModel(new SimulatorDemoDataService(snapshot, isDemoMode: false));

        await viewModel.RefreshAsync();

        Assert.False(viewModel.CanSimulatePermission);
        Assert.Contains("unavailable", viewModel.SimulatorCoverageMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No normalized entitlement dataset", viewModel.SimulationStatus, StringComparison.Ordinal);
    }

    private sealed class SimulatorDemoDataService(DemoSnapshot snapshot, bool isDemoMode = true) : IDemoDataService
    {
        public event EventHandler? DataChanged { add { } remove { } }
        public bool IsDemoMode => isDemoMode;
        public Task<DemoStatistics> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(snapshot.Statistics);
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DemoSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
    }
}
