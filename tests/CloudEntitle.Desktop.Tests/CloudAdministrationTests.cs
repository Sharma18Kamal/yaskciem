using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Desktop.Services;
using CloudEntitle.Domain;
using CloudEntitle.OrganizationManagement.Models;
using CloudEntitle.OrganizationManagement.Services;
using Xunit;

namespace CloudEntitle.Desktop.Tests;

public sealed class CloudAdministrationTests
{
    [Theory]
    [InlineData(CloudAdministrationOperation.AzureAssignRole)]
    [InlineData(CloudAdministrationOperation.AzureRemoveRole)]
    [InlineData(CloudAdministrationOperation.AzureUpdateCustomRole)]
    [InlineData(CloudAdministrationOperation.AwsAttachManagedPolicy)]
    [InlineData(CloudAdministrationOperation.AwsDetachManagedPolicy)]
    [InlineData(CloudAdministrationOperation.AwsPutInlinePolicy)]
    [InlineData(CloudAdministrationOperation.AwsDeleteInlinePolicy)]
    [InlineData(CloudAdministrationOperation.GcpSetAllowPolicy)]
    [InlineData(CloudAdministrationOperation.GcpPutDenyPolicy)]
    [InlineData(CloudAdministrationOperation.GcpDeleteDenyPolicy)]
    public async Task NativeOperations_PreviewWithoutWritingExplicitlyApplyVerifyAndRollback(CloudAdministrationOperation operation)
    {
        using var fixture = new Fixture(operation);
        var plan = await fixture.Service.PreviewAsync(fixture.Request);
        Assert.Equal(0, fixture.Runner.Writes);
        Assert.Equal("Prepared", plan.Status);
        Assert.Equal("Prepared", Assert.Single(fixture.Journal.Actions));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApplyAsync(plan.Id, false));
        Assert.Equal(0, fixture.Runner.Writes);
        var applied = await fixture.Service.ApplyAsync(plan.Id, true);
        Assert.Equal("Applied", applied.Status);
        Assert.Equal(1, fixture.Runner.Writes);
        Assert.Equal(new[] { "Prepared", "Approved", "Applied" }, fixture.Journal.Actions);
        var rolledBack = await fixture.Service.RollbackAsync(plan.Id, true);
        Assert.Equal("RolledBack", rolledBack.Status);
        Assert.Equal(2, fixture.Runner.Writes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApplyAsync(plan.Id, true));
        Assert.All(fixture.Runner.InputFiles, file => Assert.False(File.Exists(file)));
    }

    [Fact]
    public async Task GcpDenyCreation_RollbackDeletesOnlyTheNewVersionWithEtag()
    {
        using var fixture = new Fixture(CloudAdministrationOperation.GcpPutDenyPolicy);
        fixture.Runner.State = new JsonObject { ["attachmentPoint"] = "cloudresourcemanager.googleapis.com/projects/123" };
        var plan = await fixture.Service.PreviewAsync(fixture.Request);
        Assert.Equal("Applied", (await fixture.Service.ApplyAsync(plan.Id, true)).Status);
        Assert.Contains(fixture.Runner.WriteCalls, args => args.Contains("create"));
        Assert.Equal("RolledBack", (await fixture.Service.RollbackAsync(plan.Id, true)).Status);
        Assert.Contains(fixture.Runner.WriteCalls, args => args.Contains("delete") && args.Contains("--etag"));
    }

    [Theory]
    [InlineData("user")]
    [InlineData("group")]
    public async Task AwsInlinePolicy_SupportsIdentityKindsAndUrlEncodedDocuments(string kind)
    {
        using var fixture = new Fixture(CloudAdministrationOperation.AwsPutInlinePolicy, awsKind: kind);
        fixture.Runner.EncodePolicy = true;
        var plan = await fixture.Service.PreviewAsync(fixture.Request);
        Assert.Equal("Applied", (await fixture.Service.ApplyAsync(plan.Id, true)).Status);
        Assert.Equal("RolledBack", (await fixture.Service.RollbackAsync(plan.Id, true)).Status);
        Assert.All(fixture.Runner.WriteCalls, args => Assert.Contains($"--{kind}-name", args));
    }

    [Fact]
    public async Task AzureConditionalAssignment_IsRestoredWithoutLosingConditions()
    {
        using var fixture = new Fixture(CloudAdministrationOperation.AzureRemoveRole);
        fixture.Runner.State["properties"]!["condition"] = "restricted-condition";
        fixture.Runner.State["properties"]!["conditionVersion"] = "2.0";
        fixture.Runner.State["properties"]!["description"] = "restricted storage access";
        var plan = await fixture.Service.PreviewAsync(fixture.Request);
        await fixture.Service.ApplyAsync(plan.Id, true);
        await fixture.Service.RollbackAsync(plan.Id, true);
        Assert.Equal("restricted-condition", fixture.Runner.State["properties"]!["condition"]!.GetValue<string>());
        Assert.Equal("2.0", fixture.Runner.State["properties"]!["conditionVersion"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviewExpiresOnlyAfterFifteenMinutes(bool expired)
    {
        var clock = new Clock();
        using var fixture = new Fixture(CloudAdministrationOperation.AwsAttachManagedPolicy, clock);
        var plan = await fixture.Service.PreviewAsync(fixture.Request);
        clock.Utc += TimeSpan.FromMinutes(15) + (expired ? TimeSpan.FromTicks(1) : TimeSpan.Zero);
        if (expired)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApplyAsync(plan.Id, true));
            Assert.Equal(0, fixture.Runner.Writes);
        }
        else Assert.Equal("Applied", (await fixture.Service.ApplyAsync(plan.Id, true)).Status);
    }

    [Fact]
    public async Task UnknownStatusSurvivesAuditFailureAfterMutationAndDoesNotExposeProviderErrors()
    {
        using var fixture = new Fixture(CloudAdministrationOperation.AwsPutInlinePolicy);
        var plan = await fixture.Service.PreviewAsync(fixture.Request);
        fixture.Runner.FailAfterWrite = true;
        fixture.Journal.FailOnAction = "ApplyUnknown";
        var error = await Assert.ThrowsAsync<CloudAdministrationException>(() => fixture.Service.ApplyAsync(plan.Id, true));
        Assert.Equal("ApplyUnknown", error.Plan.Status);
        Assert.Contains("audit append failed", error.Message);
        Assert.DoesNotContain("private secret", error.ToString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApplyAsync(plan.Id, true));
        fixture.Journal.FailOnAction = null;
        Assert.Equal("Applied", (await fixture.Service.ReconcileAsync(plan.Id)).Status);
        Assert.Equal(1, fixture.Runner.Writes);
    }

    [Fact]
    public async Task ChangedCloudStateOrIdentityPreventsWritingAndRollbackCannotOverwriteDrift()
    {
        using var fixture = new Fixture(CloudAdministrationOperation.AwsPutInlinePolicy);
        var plan = await fixture.Service.PreviewAsync(fixture.Request);
        var before = fixture.Runner.State.DeepClone();
        fixture.Runner.State["Statement"]![0]!["Action"] = "s3:ListBucket";
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApplyAsync(plan.Id, true));
        Assert.Equal(0, fixture.Runner.Writes);
        fixture.Runner.State = (JsonObject)before;
        fixture.Runner.CloudActor = "another";
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApplyAsync(plan.Id, true));
        Assert.Equal(0, fixture.Runner.Writes);
        fixture.Runner.CloudActor = "operator";
        await fixture.Service.ApplyAsync(plan.Id, true);
        fixture.Runner.State["Statement"]![0]!["Action"] = "iam:*";
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RollbackAsync(plan.Id, true));
        Assert.Equal(1, fixture.Runner.Writes);
    }

    [Fact]
    public async Task AmbiguousWriteIsNotRetriedAndCanBeReconciledReadOnly()
    {
        using var fixture = new Fixture(CloudAdministrationOperation.AwsPutInlinePolicy);
        var plan = await fixture.Service.PreviewAsync(fixture.Request);
        fixture.Runner.FailAfterWrite = true;
        await Assert.ThrowsAsync<CloudAdministrationException>(() => fixture.Service.ApplyAsync(plan.Id, true));
        Assert.Equal("ApplyUnknown", fixture.Journal.Plans.Last().Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApplyAsync(plan.Id, true));
        Assert.Equal(1, fixture.Runner.Writes);
        Assert.Equal("Applied", (await fixture.Service.ReconcileAsync(plan.Id)).Status);
        Assert.Equal(1, fixture.Runner.Writes);
        fixture.Runner.FailAfterWrite = false;
        Assert.Equal("RolledBack", (await fixture.Service.RollbackAsync(plan.Id, true)).Status);
    }

    [Fact]
    public async Task AuditFailureOrOrganizationSignOutStopsWrites()
    {
        using var fixture = new Fixture(CloudAdministrationOperation.AwsAttachManagedPolicy);
        var plan = await fixture.Service.PreviewAsync(fixture.Request);
        fixture.Journal.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.ApplyAsync(plan.Id, true));
        Assert.Equal(0, fixture.Runner.Writes);
        fixture.Journal.Fail = false;
        fixture.Context.Set(null, null);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ApplyAsync(plan.Id, true));
        Assert.Equal(0, fixture.Runner.Writes);
    }

    [Fact]
    public async Task InlineCreation_RollbackDeletesOnlyTheNewPolicy()
    {
        using var fixture = new Fixture(CloudAdministrationOperation.AwsPutInlinePolicy);
        fixture.Runner.State = new JsonObject();
        var plan = await fixture.Service.PreviewAsync(fixture.Request);
        await fixture.Service.ApplyAsync(plan.Id, true);
        await fixture.Service.RollbackAsync(plan.Id, true);
        Assert.Empty(fixture.Runner.State);
        Assert.Contains(fixture.Runner.WriteCalls, args => args.Contains("delete-role-policy"));
    }

    [Fact]
    public async Task GcpConditionalAllowPolicy_PreservesLoggingAndUsesCurrentEtagOnApplyAndRollback()
    {
        using var fixture = new Fixture(CloudAdministrationOperation.GcpSetAllowPolicy);
        fixture.Runner.State["auditConfigs"] = JsonNode.Parse("[{\"service\":\"allServices\",\"auditLogConfigs\":[{\"logType\":\"DATA_READ\"}]}]");
        var document = JsonNode.Parse(fixture.Request.PolicyJson)!.AsObject();
        document["auditConfigs"] = fixture.Runner.State["auditConfigs"]!.DeepClone();
        document["bindings"]![0]!["condition"] = JsonNode.Parse("{\"title\":\"temporary\",\"expression\":\"request.time < timestamp('2030-01-01T00:00:00Z')\"}");
        var plan = await fixture.Service.PreviewAsync(fixture.Request with { PolicyJson = document.ToJsonString() });
        Assert.Equal(3, JsonNode.Parse(plan.AfterJson)!["version"]!.GetValue<int>());
        await fixture.Service.ApplyAsync(plan.Id, true);
        await fixture.Service.RollbackAsync(plan.Id, true);
        Assert.True(JsonNode.DeepEquals(document["auditConfigs"], fixture.Runner.State["auditConfigs"]));
    }

    [Fact]
    public async Task BuiltInAzureRoleAndCrossSubscriptionCustomRoleEditingAreRejected()
    {
        using var fixture = new Fixture(CloudAdministrationOperation.AzureUpdateCustomRole);
        fixture.Runner.State["properties"]!["type"] = "BuiltInRole";
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PreviewAsync(fixture.Request));
        fixture.Runner.State["properties"]!["type"] = "CustomRole";
        var document = JsonNode.Parse(fixture.Request.PolicyJson)!;
        document["assignableScopes"] = new JsonArray("/subscriptions/" + Guid.NewGuid());
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.PreviewAsync(fixture.Request with { PolicyJson = document.ToJsonString() }));
        Assert.Equal(0, fixture.Runner.Writes);
    }

    [Fact]
    public async Task ViewerUnboundAccountAndAuditLoggingChangesAreRejected()
    {
        using var fixture = new Fixture(CloudAdministrationOperation.GcpSetAllowPolicy);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.PreviewAsync(fixture.Request with { AccountId = "other-project" }));
        fixture.Runner.State["auditConfigs"] = JsonNode.Parse("[{\"service\":\"allServices\",\"auditLogConfigs\":[{\"logType\":\"DATA_READ\"}]}]");
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.PreviewAsync(fixture.Request));
        var viewer = new OrganizationIdentity(Guid.NewGuid(), "viewer", fixture.Actor.TenantId, "viewer@example.invalid");
        fixture.Organizations.AddMember(fixture.Organization, fixture.Actor, viewer.Email, OrganizationMemberRole.Viewer);
        fixture.Organizations.AcceptInvitation(fixture.Organization, viewer);
        fixture.Context.Set(viewer, fixture.Organization);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.PreviewAsync(fixture.Request));
        Assert.Equal(0, fixture.Runner.Writes);
    }

    [Theory]
    [InlineData("arn:aws:iam::999999999999:policy/other")]
    [InlineData("arn:aws:iam::123456789012:policy/evil&command")]
    public void UnsafePolicyTargetsAreRejectedBeforeAnyCloudCall(string policy)
    {
        using var fixture = new Fixture(CloudAdministrationOperation.AwsAttachManagedPolicy);
        Assert.Throws<ArgumentException>(() => CloudAdministrationService.Validate(fixture.Request with { RoleOrPolicy = policy }));
        Assert.Throws<ArgumentException>(() => CloudAdministrationService.Validate(fixture.Request with { Profile = "bad&shell" }));
    }

    [Fact]
    public async Task JournalPersistsBeforeAfterAndActorForManualRecoveryWithoutReplayAuthority()
    {
        using var fixture = new Fixture(CloudAdministrationOperation.AwsAttachManagedPolicy);
        var plan = await fixture.Service.PreviewAsync(fixture.Request);
        var path = Path.Combine(fixture.Directory, "journal");
        await new CloudAdministrationJournal(path).AppendAsync(plan, "Prepared");
        var file = Assert.Single(Directory.GetFiles(path));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(file));
        Assert.Equal(fixture.Actor.UserId, json.RootElement.GetProperty("plan").GetProperty("ActorId").GetGuid());
        Assert.Equal(plan.BeforeJson, json.RootElement.GetProperty("plan").GetProperty("BeforeJson").GetString());
        var restarted = new CloudAdministrationService(fixture.Runner, fixture.Context, fixture.Organizations, fixture.Journal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.ApplyAsync(plan.Id, true));
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), $"yask-admin-tests-{Guid.NewGuid():N}");
        public OrganizationIdentity Actor { get; }
        public Guid Organization { get; }
        public OrganizationManagementService Organizations { get; }
        public AuthenticatedOrganizationContext Context { get; } = new();
        public NativeRunner Runner { get; }
        public Journal Journal { get; } = new();
        public CloudAdministrationRequest Request { get; }
        public CloudAdministrationService Service { get; }
        public Fixture(CloudAdministrationOperation operation, TimeProvider? clock = null, string awsKind = "role")
        {
            Actor = new(Guid.NewGuid(), "owner", Guid.NewGuid().ToString(), "owner@example.invalid");
            Organizations = new(new LocalOrganizationStore(Path.Combine(Directory, "organizations.json")));
            Organization = Organizations.CreateOrganization("Admin", "admin", Actor.TenantId, Actor).Id;
            var provider = operation.ToString().StartsWith("Azure", StringComparison.Ordinal) ? CloudProvider.Azure
                : operation.ToString().StartsWith("Aws", StringComparison.Ordinal) ? CloudProvider.Aws : CloudProvider.Gcp;
            var account = provider == CloudProvider.Azure ? Guid.NewGuid().ToString() : provider == CloudProvider.Aws ? "123456789012" : "client-project";
            var role = provider == CloudProvider.Azure ? Guid.NewGuid().ToString()
                : operation is CloudAdministrationOperation.AwsAttachManagedPolicy or CloudAdministrationOperation.AwsDetachManagedPolicy
                    ? $"arn:aws:iam::{account}:policy/Test" : "test-policy";
            var policy = provider == CloudProvider.Azure
                ? $"{{\"roleName\":\"Custom\",\"permissions\":[{{\"actions\":[\"Microsoft.Compute/*/read\"],\"notActions\":[],\"dataActions\":[],\"notDataActions\":[]}}],\"assignableScopes\":[\"/subscriptions/{account}\"]}}"
                : provider == CloudProvider.Aws ? "{\"Version\":\"2012-10-17\",\"Statement\":[{\"Effect\":\"Deny\",\"Action\":\"s3:GetObject\",\"Resource\":\"*\"}]}"
                : operation == CloudAdministrationOperation.GcpSetAllowPolicy ? "{\"bindings\":[{\"role\":\"roles/viewer\",\"members\":[\"serviceAccount:service@client-project.iam.gserviceaccount.com\"]}]}"
                : "{\"displayName\":\"Test deny\",\"rules\":[{\"denyRule\":{\"deniedPrincipals\":[\"principalSet://goog/public:all\"],\"deniedPermissions\":[\"storage.googleapis.com/objects.get\"]}}]}";
            Request = new(provider, account, "", operation, $"/subscriptions/{account}",
                provider == CloudProvider.Azure ? Guid.NewGuid().ToString() : "role", Guid.NewGuid().ToString(), role, policy, "Reviewed test change");
            if (provider == CloudProvider.Aws) Request = Request with { Subject = "test-role", SubjectKind = awsKind };
            Runner = new(Request);
            Organizations.BindCloudAccount(Organization, Actor, provider, Guid.NewGuid(), account, "Test");
            Context.Set(Actor, Organization);
            Service = new(Runner, Context, Organizations, Journal, timeProvider: clock);
        }
        public void Dispose() { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true); }
    }

    private sealed class Journal : ICloudAdministrationJournal
    {
        public List<string> Actions { get; } = new();
        public List<CloudAdministrationPlan> Plans { get; } = new();
        public bool Fail { get; set; }
        public string? FailOnAction { get; set; }
        public Task AppendAsync(CloudAdministrationPlan plan, string action, CancellationToken token = default)
        {
            if (Fail || action == FailOnAction) throw new IOException("Audit disk unavailable.");
            Actions.Add(action); Plans.Add(plan); return Task.CompletedTask;
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Utc { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Utc;
    }

    private sealed class NativeRunner : ICloudCommandRunner
    {
        private readonly CloudAdministrationRequest _request;
        public JsonObject State { get; set; }
        public int Writes { get; private set; }
        public string CloudActor { get; set; } = "operator";
        public bool FailAfterWrite { get; set; }
        public bool EncodePolicy { get; set; }
        public List<string> InputFiles { get; } = new();
        public List<IReadOnlyList<string>> WriteCalls { get; } = new();
        public NativeRunner(CloudAdministrationRequest request)
        {
            _request = request;
            State = request.Operation switch
            {
                CloudAdministrationOperation.AzureAssignRole => new JsonObject(),
                CloudAdministrationOperation.AzureRemoveRole => JsonNode.Parse($"{{\"properties\":{{\"principalId\":\"{request.Subject}\",\"roleDefinitionId\":\"/subscriptions/{request.AccountId}/providers/Microsoft.Authorization/roleDefinitions/{request.RoleOrPolicy}\"}}}}")!.AsObject(),
                CloudAdministrationOperation.AzureUpdateCustomRole => new JsonObject { ["properties"] = JsonNode.Parse(request.PolicyJson) },
                CloudAdministrationOperation.AwsAttachManagedPolicy => new JsonObject { ["attached"] = false },
                CloudAdministrationOperation.AwsDetachManagedPolicy => new JsonObject { ["attached"] = true },
                CloudAdministrationOperation.AwsPutInlinePolicy or CloudAdministrationOperation.AwsDeleteInlinePolicy =>
                    JsonNode.Parse("{\"Version\":\"2012-10-17\",\"Statement\":[{\"Effect\":\"Allow\",\"Action\":\"s3:GetObject\",\"Resource\":\"*\"}]}")!.AsObject(),
                CloudAdministrationOperation.GcpSetAllowPolicy => JsonNode.Parse("{\"version\":1,\"etag\":\"before\",\"bindings\":[{\"role\":\"roles/viewer\",\"members\":[\"user:old@example.invalid\"]}]}")!.AsObject(),
                _ => JsonNode.Parse("{\"attachmentPoint\":\"cloudresourcemanager.googleapis.com/projects/123\",\"policy\":{\"name\":\"policies/cloudresourcemanager.googleapis.com%2Fprojects%2F123/denypolicies/test-policy\",\"etag\":\"before\",\"rules\":[{\"denyRule\":{\"deniedPrincipals\":[\"principalSet://goog/public:all\"],\"deniedPermissions\":[\"storage.googleapis.com/objects.delete\"]}}]}}")!.AsObject()
            };
            if (request.Operation == CloudAdministrationOperation.AzureUpdateCustomRole)
            {
                State["properties"]!["type"] = "CustomRole";
                State["properties"]!["permissions"]![0]!["actions"] = JsonNode.Parse("[\"Microsoft.Storage/*/read\"]");
            }
        }
        public async Task<CloudCommandResult> RunAsync(string command, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? Value(string flag) { var i = args.ToList().IndexOf(flag); return i < 0 ? null : args[i + 1]; }
            CloudCommandResult Json(JsonNode? node) => new(0, node?.ToJsonString() ?? "{}", "");
            if (command == "aws" && args.Contains("get-caller-identity"))
                return Json(new JsonObject { ["Account"] = _request.AccountId, ["Arn"] = $"arn:aws:iam::{_request.AccountId}:user/{CloudActor}" });
            if (command == "az" && args.Contains("show")) return new(0, JsonSerializer.Serialize(new {
                id = _request.AccountId, tenantId = "tenant", state = "Enabled", user = new { name = CloudActor } }), "");
            if (command == "az" && Value("--url")?.EndsWith("?api-version=2022-12-01", StringComparison.Ordinal) == true)
                return Json(new JsonObject { ["subscriptionId"] = _request.AccountId, ["tenantId"] = "tenant" });
            if (command == "gcloud" && args.Contains("config")) return new(0, "{}", "");
            if (command == "gcloud" && args.Contains("auth")) return new(0, $"[{{\"account\":\"{CloudActor}@example.invalid\"}}]", "");
            if (command == "gcloud" && args.Contains("describe"))
                return Json(new JsonObject { ["projectId"] = _request.AccountId, ["projectNumber"] = "123" });
            var write = command == "az" ? Value("--method") is "put" or "delete"
                : command == "aws" ? args.Any(arg => arg.StartsWith("put-", StringComparison.Ordinal) || arg.StartsWith("delete-", StringComparison.Ordinal)
                    || arg.StartsWith("attach-", StringComparison.Ordinal) || arg.StartsWith("detach-", StringComparison.Ordinal))
                : args.Contains("set-iam-policy") || args.Contains("create") || args.Contains("update") || args.Contains("delete");
            if (write)
            {
                Writes++;
                WriteCalls.Add(args.ToArray());
                var file = Value("--body")?.TrimStart('@') ?? Value("--policy-document")?.Replace("file://", "", StringComparison.Ordinal)
                    ?? Value("--policy-file");
                if (command == "gcloud" && args.Contains("set-iam-policy")) file = args[3];
                JsonObject? body = null;
                if (file is not null) { InputFiles.Add(file); body = JsonNode.Parse(await File.ReadAllTextAsync(file, cancellationToken))!.AsObject(); }
                if (command == "az") State = body ?? new JsonObject();
                else if (command == "aws")
                {
                    if (args.Any(arg => arg.StartsWith("attach-", StringComparison.Ordinal) || arg.StartsWith("detach-", StringComparison.Ordinal)))
                        State = new JsonObject { ["attached"] = args.Any(arg => arg.StartsWith("attach-", StringComparison.Ordinal)) };
                    else State = body ?? new JsonObject();
                }
                else if (args.Contains("set-iam-policy"))
                {
                    Assert.Contains("--account", args);
                    Assert.Equal("operator@example.invalid", Value("--account"));
                    Assert.Equal(State["etag"]!.GetValue<string>(), body!["etag"]!.GetValue<string>());
                    State = body!; State["etag"] = "new";
                }
                else
                {
                    Assert.Equal("cloudresourcemanager.googleapis.com/projects/123", Value("--attachment-point"));
                    if (State["policy"] is not null)
                        Assert.Equal(State["policy"]!["etag"]!.GetValue<string>(), Value("--etag"));
                    if (args.Contains("delete")) State.Remove("policy");
                    else
                    {
                        State["policy"] = body;
                        State["policy"]!["name"] = "policies/cloudresourcemanager.googleapis.com%2Fprojects%2F123/denypolicies/test-policy";
                        State["policy"]!["etag"] = "new";
                    }
                }
                return new(FailAfterWrite ? 1 : 0, "{}", FailAfterWrite ? "private secret provider error" : "");
            }
            if (command == "az" && args.Contains("assignment"))
            {
                if (State.Count == 0) return Json(new JsonArray());
                var assignment = State["properties"]!.DeepClone().AsObject();
                assignment["name"] = _request.SubjectKind;
                assignment["scope"] = _request.Scope;
                return Json(new JsonArray(assignment));
            }
            if (command == "az") return Json(State);
            if (command == "aws")
            {
                if (args.Contains($"get-{_request.SubjectKind}"))
                    return Json(new JsonObject { [_request.SubjectKind == "role" ? "Role" : _request.SubjectKind == "user" ? "User" : "Group"] =
                        new JsonObject { ["Arn"] = $"arn:aws:iam::{_request.AccountId}:{_request.SubjectKind}/{_request.Subject}" } });
                if (args.Contains($"list-attached-{_request.SubjectKind}-policies"))
                    return Json(new JsonObject { ["AttachedPolicies"] = State["attached"]!.GetValue<bool>() ? new JsonArray(new JsonObject { ["PolicyArn"] = _request.RoleOrPolicy }) : new JsonArray() });
                if (args.Contains($"list-{_request.SubjectKind}-policies"))
                    return Json(new JsonObject { ["PolicyNames"] = State.Count == 0 ? new JsonArray() : new JsonArray(JsonValue.Create(_request.RoleOrPolicy)) });
                return Json(new JsonObject { ["PolicyDocument"] = EncodePolicy ? JsonValue.Create(Uri.EscapeDataString(State.ToJsonString())) : State.DeepClone() });
            }
            if (args.Contains("get-iam-policy")) return Json(State);
            if (args.Contains("list")) return Json(State["policy"] is null ? new JsonArray()
                : new JsonArray(new JsonObject { ["name"] = State["policy"]!["name"]!.DeepClone() }));
            if (args.Contains("get")) return Json(State["policy"]);
            throw new InvalidOperationException($"Unexpected fixture command: {command} {string.Join(' ', args)}");
        }
    }
}
