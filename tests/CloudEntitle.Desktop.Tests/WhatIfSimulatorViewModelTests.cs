using System.Text.Json;
using System.IO;
using System.Text.Json.Serialization;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Desktop.ViewModels;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.EffectiveAccess.Models;
using CloudEntitle.EffectiveAccess.Services;
using CloudEntitle.Normalization.Models;
using CloudEntitle.OrganizationManagement.Models;
using CloudEntitle.OrganizationManagement.Services;
using CloudEntitle.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CloudEntitle.Desktop.Tests;

public sealed class WhatIfSimulatorViewModelTests
{
    [Fact]
    public async Task WhatIf_SavedSqliteInventoryIsAccountScopedPartialAndReadOnly()
    {
        var example = await WhatIfPermissionSimulator.ImportAsync(Bundle());
        var normalized = example.Evaluation.Normalized;
        var accountId = normalized.Principals[0].AccountId;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CloudEntitleDbContext>().UseSqlite(connection).Options;
        var factory = new ContextFactory(options);
        var grant = normalized.Entitlements[0];
        var entitlement = new Entitlement { Id = grant.Id, CloudAccountId = accountId, PrincipalId = grant.PrincipalId,
            RoleId = grant.RoleId, PermissionId = grant.PermissionId, Scope = grant.Scope.OriginalScope,
            AssignmentType = AssignmentType.Role, GrantedAt = DateTime.UtcNow.AddDays(-1), Source = "saved-assignment" };
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            db.Add(new CloudAccount { Id = accountId, CloudProvider = CloudProviderType.Azure, Name = "Saved real account",
                LastScannedAt = DateTime.UtcNow.AddMinutes(-3) });
            db.Add(new CloudAccount { Id = Guid.NewGuid(), CloudProvider = CloudProviderType.Aws, Name = "Other account" });
            db.AddRange(normalized.Principals);
            db.AddRange(normalized.Roles);
            db.AddRange(normalized.Permissions);
            db.AddRange(example.Evaluation.Resources);
            db.Add(entitlement);
            await db.SaveChangesAsync();
        }
        var saved = new WhatIfSavedData(factory);
        Assert.Equal(2, (await saved.GetAccountsAsync(default)).Count);
        var input = await saved.LoadAsync(accountId, default);
        Assert.NotNull(input.SourceSnapshotAtUtc);
        Assert.Equal("Analyst", Assert.Single(input.Evaluation.Normalized.Principals).Name);
        Assert.Equal(NormalizationStatus.Unknown, Assert.Single(input.Evaluation.Normalized.Entitlements).Status);
        Assert.NotEmpty(input.Limitations);
        var report = await new WhatIfPermissionSimulator(new EffectiveAccessEngine()).SimulateAsync(input,
            new(CloudProvider.Azure, accountId, WhatIfTargetKind.Role, normalized.Roles[0].Id,
                "Microsoft.Storage/storageAccounts/listKeys/action"));
        Assert.Null(report.ModeledExposureReductionPercent);
        Assert.NotEqual(WhatIfImpact.ModeledAccessLost, Assert.Single(report.AccessChanges).Impact);
        await using var after = factory.CreateDbContext();
        Assert.Equal(entitlement.PermissionId, (await after.Entitlements.SingleAsync()).PermissionId);
        Assert.Equal(AccessType.Allow, (await after.CloudPermissions.SingleAsync()).AccessType);
    }

    [Fact]
    public async Task WhatIf_ExportIsExplicitlyNonExecutableAndNotProviderVerified()
    {
        var path = Path.Combine(Path.GetTempPath(), $"yask-whatif-{Guid.NewGuid():N}.json");
        try
        {
            var vm = Create(new SavedData(), new AuthenticatedOrganizationContext(), dialogs: new Dialogs { ExportPath = path });
            await vm.LoadJsonAsync(Bundle());
            vm.SelectedTarget = Assert.Single(vm.Targets);
            await vm.SimulateAsync();
            await vm.ExportAsync();
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.False(json.RootElement.GetProperty("MutatesCloud").GetBoolean());
            Assert.False(json.RootElement.GetProperty("IsProviderVerified").GetBoolean());
            Assert.Equal(64, json.RootElement.GetProperty("InputSha256").GetString()!.Length);
            Assert.Contains("non-executable", vm.Status);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task WhatIf_StaleOrganizationSurfacesErrorWithoutLoading()
    {
        var saved = new SavedData();
        var context = new AuthenticatedOrganizationContext();
        context.Set(new OrganizationIdentity(Guid.NewGuid(), "owner", Guid.NewGuid().ToString("D"), "owner@example.invalid"), Guid.NewGuid());
        var vm = Create(saved, context);
        await vm.RefreshAsync();
        Assert.Contains("failed", vm.Status);
        Assert.Equal(0, saved.Reads);
        Assert.Empty(vm.Accounts);
    }
    [Fact]
    public async Task WhatIf_OfflineImportSimulatesAndEditsInvalidateReport()
    {
        var saved = new SavedData();
        var context = new AuthenticatedOrganizationContext();
        var vm = Create(saved, context);
        Assert.Equal(0, saved.Reads);
        await vm.LoadJsonAsync(Bundle());
        vm.SelectedTarget = Assert.Single(vm.Targets);
        await vm.SimulateAsync();
        Assert.NotNull(vm.Report);
        Assert.Equal(WhatIfImpact.ModeledAccessLost, Assert.Single(vm.AccessChanges).Impact);
        Assert.Contains("1 modeled losses", vm.Summary);
        vm.Action = "Microsoft.Storage/storageAccounts/read";
        Assert.Null(vm.Report);
        Assert.Empty(vm.AccessChanges);
        Assert.Equal(0, saved.Reads);
    }

    [Fact]
    public async Task WhatIf_RecalculatesToxicFindingsFromTheIsolatedAfterSnapshot()
    {
        var input = await WhatIfPermissionSimulator.ImportAsync(Bundle());
        var normalized = input.Evaluation.Normalized;
        var accountId = normalized.Principals[0].AccountId;
        var resource = input.Evaluation.Resources[0];
        var sensitiveResource = new CloudResource
        {
            Id = resource.Id,
            AccountId = resource.AccountId,
            Name = resource.Name,
            Type = resource.Type,
            ResourceId = resource.ResourceId,
            Tags = new(StringComparer.OrdinalIgnoreCase) { ["data-classification"] = "sensitive" }
        };
        var readPermission = new CloudPermission
        {
            Id = Guid.NewGuid(), AccountId = accountId,
            Action = "Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read"
        };
        var readGrant = normalized.Entitlements[0] with
        {
            Id = Guid.NewGuid(),
            PermissionId = readPermission.Id,
            ResourcePattern = "*",
            Evidence = normalized.Entitlements[0].Evidence with { SourceId = "sensitive-storage-read" }
        };
        input = input with
        {
            Evaluation = input.Evaluation with
            {
                Normalized = normalized with
                {
                    Permissions = normalized.Permissions.Append(readPermission).ToArray(),
                    Entitlements = normalized.Entitlements.Append(readGrant).ToArray()
                },
                Resources = new[] { sensitiveResource }
            }
        };

        var engine = new EffectiveAccessEngine();
        var analyzer = new WhatIfScenarioAnalyzer(new CloudEntitle.RiskEngine.Services.RiskEngine());
        var simulator = new WhatIfPermissionSimulator(engine, analyzer);
        var report = await simulator.SimulateAsync(input, new(CloudProvider.Azure, accountId,
            WhatIfTargetKind.Role, normalized.Roles[0].Id, "Microsoft.Storage/storageAccounts/write")
        {
            Operation = WhatIfOperation.Add
        });

        Assert.NotNull(report.SecurityImpact);
        Assert.True(report.SecurityImpact!.ToxicFindingsAfter > report.SecurityImpact.ToxicFindingsBefore);
        Assert.Contains(report.SecurityImpact.AddedFindings,
            finding => finding.Title.Contains("Sensitive storage", StringComparison.OrdinalIgnoreCase));
        Assert.False(report.MutatesCloud);
        Assert.False(report.IsProviderVerified);
    }

    [Fact]
    public async Task WhatIf_InvalidImportNeverLeavesOldEvidenceAvailable()
    {
        var vm = Create(new SavedData(), new AuthenticatedOrganizationContext());
        await vm.LoadJsonAsync(Bundle());
        vm.SelectedTarget = Assert.Single(vm.Targets);
        await vm.SimulateAsync();
        await vm.LoadJsonAsync("""{"SchemaVersion":"unknown"}""");
        Assert.Null(vm.Report);
        Assert.Empty(vm.Targets);
        await vm.SimulateAsync();
        Assert.Contains("Load evidence", vm.Status);
    }

    [Fact]
    public async Task WhatIf_ContextChangeClearsImportedDataAndConsentlessStartupDoesNotLoad()
    {
        var saved = new SavedData();
        var context = new AuthenticatedOrganizationContext();
        var vm = Create(saved, context);
        await vm.LoadJsonAsync(Bundle());
        Assert.NotEmpty(vm.Targets);
        context.Set(new OrganizationIdentity(Guid.NewGuid(), "subject", Guid.NewGuid().ToString("D"), "user@example.invalid"), Guid.NewGuid());
        Assert.Empty(vm.Targets);
        Assert.Null(vm.Report);
        Assert.Equal(0, saved.Reads);
    }

    [Fact]
    public async Task WhatIf_LateSavedLoadCannotRepopulateAfterAccountChange()
    {
        var saved = new SavedData { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var vm = Create(saved, new AuthenticatedOrganizationContext());
        var account = new CloudAccount { Id = Guid.NewGuid(), CloudProvider = CloudProviderType.Azure };
        vm.SelectedAccount = account;
        var load = vm.LoadSavedAsync();
        vm.SelectedAccount = new CloudAccount { Id = Guid.NewGuid(), CloudProvider = CloudProviderType.Azure };
        saved.Pending.SetResult(await WhatIfPermissionSimulator.ImportAsync(Bundle()));
        await load;
        Assert.Empty(vm.Targets);
        Assert.Null(vm.Report);
    }

    [Fact]
    public async Task WhatIf_SignedInOrganizationRejectsUnboundImportedData()
    {
        var context = new AuthenticatedOrganizationContext();
        var organizations = new OrganizationManagementService();
        var identity = new OrganizationIdentity(Guid.NewGuid(), "owner", Guid.NewGuid().ToString("D"), "owner@example.invalid");
        var organization = organizations.CreateOrganization("Simulation tests", Guid.NewGuid().ToString("N"), identity.TenantId, identity);
        context.Set(identity, organization.Id);
        var vm = Create(new SavedData(), context, organizations);
        await vm.LoadJsonAsync(Bundle());
        Assert.Empty(vm.Targets);
        Assert.Contains("must match", vm.Status);
    }

    private static WhatIfSimulatorViewModel Create(SavedData saved, AuthenticatedOrganizationContext context,
        OrganizationManagementService? organizations = null, Dialogs? dialogs = null)
        => new(new EmptyData(), saved, new(new EffectiveAccessEngine()), dialogs ?? new Dialogs(), context,
            organizations ?? new OrganizationManagementService());

    private static string Bundle()
    {
        var account = Guid.NewGuid();
        var principal = new CloudPrincipal { Id = Guid.NewGuid(), AccountId = account, CloudProvider = CloudProviderType.Azure, Name = "Analyst" };
        var role = new CloudRole { Id = Guid.NewGuid(), AccountId = account, Name = "Storage" };
        var permission = new CloudPermission { Id = Guid.NewGuid(), AccountId = account, Action = "Microsoft.Storage/storageAccounts/listKeys/action" };
        var resource = new CloudResource { Id = Guid.NewGuid(), AccountId = account, Type = "Microsoft.Storage/storageAccounts",
            ResourceId = "/subscriptions/demo/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/data" };
        var discovery = new ProviderDiscoveryResult(CloudProvider.Azure,
            new[] { new CloudAccount { Id = account, CloudProvider = CloudProviderType.Azure } }, new[] { principal }, new[] { role },
            Array.Empty<CloudPolicy>(), new[] { permission }, new[] { new ProviderAuthorizationRecord("export-role", "Role", account,
                principal.Id, role.Id, null, permission.Id, "/subscriptions/demo", "*", AssignmentType.Role, AccessType.Allow,
                "AzureRoleDefinition", "role JSON", "action list", null, null, true, "/subscriptions/demo", null) }, DateTime.UtcNow);
        return JsonSerializer.Serialize(new OfflineAuthorizationBundle("1.0", "Test bundle", discovery, new[] { resource },
            Array.Empty<GroupMembership>(), Array.Empty<string>()), new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });
    }
    private sealed class SavedData : IWhatIfSavedData
    {
        public int Reads { get; private set; }
        public TaskCompletionSource<WhatIfInput>? Pending { get; init; }
        public Task<IReadOnlyList<CloudAccount>> GetAccountsAsync(CancellationToken token)
        { Reads++; return Task.FromResult<IReadOnlyList<CloudAccount>>(Array.Empty<CloudAccount>()); }
        public Task<WhatIfInput> LoadAsync(Guid accountId, CancellationToken token)
        { Reads++; return Pending?.Task ?? WhatIfPermissionSimulator.ImportAsync(Bundle(), token); }
    }
    private sealed class EmptyData : IDemoDataService
    {
        public event EventHandler? DataChanged { add { } remove { } }
        public bool IsDemoMode => false;
        public Task<DemoStatistics> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DemoStatistics(0, 0, 0, 0, 0, 0, 0, null));
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DemoSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DemoSnapshot(
            Array.Empty<CloudAccount>(), Array.Empty<CloudPrincipal>(), Array.Empty<CloudResource>(), Array.Empty<Entitlement>(),
            Array.Empty<CloudEntitle.Domain.Entities.EffectiveAccess>(), Array.Empty<RiskFindingModel>(), Array.Empty<RecommendationModel>(), Array.Empty<Scan>()));
    }
    private sealed class Dialogs : IDialogService
    {
        public string? ExportPath { get; init; }
        public string? ChooseWhatIfReportPath() => ExportPath;
        public void ShowInformation(string message, string title = "YASK-CIEM") { }
        public void ShowError(string message, string title = "YASK-CIEM") { }
        public bool Confirm(string message, string title = "YASK-CIEM") => false;
        public string? ChooseReportPath() => null;
        public string? ChoosePolicyDraftPath() => null;
        public string? ChooseComplianceBundlePath() => null;
        public string? ChooseLocalBackupSavePath() => null;
        public string? ChooseLocalBackupOpenPath() => null;
    }
    private sealed class ContextFactory(DbContextOptions<CloudEntitleDbContext> options) : IDbContextFactory<CloudEntitleDbContext>
    {
        public CloudEntitleDbContext CreateDbContext() => new(options);
    }
}
