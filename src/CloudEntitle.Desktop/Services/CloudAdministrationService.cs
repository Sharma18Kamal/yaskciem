using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;
using CloudEntitle.OrganizationManagement.Models;
using CloudEntitle.OrganizationManagement.Services;

namespace CloudEntitle.Desktop.Services;

public sealed class CloudAdministrationService(
    ICloudCommandRunner runner,
    AuthenticatedOrganizationContext context,
    OrganizationManagementService organizations,
    ICloudAdministrationJournal journal,
    ISharedOrganizationApi? sharedApi = null,
    TimeProvider? timeProvider = null) : ICloudAdministrationService
{
    private readonly Dictionary<Guid, CloudAdministrationPlan> _plans = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<CloudAdministrationPlan> PreviewAsync(CloudAdministrationRequest request, CancellationToken token = default)
    {
        Validate(request);
        await _gate.WaitAsync(token);
        try
        {
            var (actor, organization) = await AuthorizeAsync(request, token);
            var identity = await VerifyAsync(request, token);
            var before = await ReadAsync(request, identity, token);
            var after = Proposed(request, before);
            if (Equivalent(before, after)) throw new InvalidOperationException("No change: the requested state already exists.");
            await AuthorizeSameAsync(request, actor.UserId, organization, token);
            var plan = new CloudAdministrationPlan(Guid.NewGuid(), organization, actor.UserId, request, identity,
                before.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                after.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), _clock.GetUtcNow(), "Prepared",
                "Manual administration only. Review workloads, break-glass access, conditions and inherited grants. No complete least-privilege or dependency guarantee is available.");
            await journal.AppendAsync(plan, "Prepared", token);
            _plans.Add(plan.Id, plan);
            return plan;
        }
        finally { _gate.Release(); }
    }

    public Task<CloudAdministrationPlan> ApplyAsync(Guid planId, bool dependenciesReviewed, CancellationToken token = default)
        => ExecuteAsync(planId, dependenciesReviewed, false, token);
    public Task<CloudAdministrationPlan> RollbackAsync(Guid planId, bool dependenciesReviewed, CancellationToken token = default)
        => ExecuteAsync(planId, dependenciesReviewed, true, token);

    public async Task<CloudAdministrationPlan> VerifyAsync(Guid planId, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var plan = GetPlan(planId);
            if (plan.Status is not ("Applied" or "Verified" or "VerificationFailed"))
                throw new InvalidOperationException("Only a previously applied plan can be verified.");
            await AuthorizeSameAsync(plan.Request, plan.ActorId, plan.OrganizationId, token);
            var identity = await VerifyAsync(plan.Request, token);
            CloudAccountVerifier.RequireSameIdentity(plan.CloudIdentity, identity);
            var observed = await ReadAsync(plan.Request, identity, token);
            var verified = Equivalent(observed, Parse(plan.AfterJson));
            plan = plan with
            {
                Status = verified ? "Verified" : "VerificationFailed",
                Notice = verified
                    ? "A fresh provider read confirms the proposed state."
                    : "A fresh provider read does not match the proposed state. Reconcile and investigate; do not retry automatically."
            };
            await journal.AppendAsync(plan, plan.Status, token);
            _plans[planId] = plan;
            return plan;
        }
        finally { _gate.Release(); }
    }

    private async Task<CloudAdministrationPlan> ExecuteAsync(Guid id, bool reviewed, bool rollback, CancellationToken token)
    {
        if (!reviewed) throw new InvalidOperationException("Explicit approval and operational dependency/break-glass review are required.");
        await _gate.WaitAsync(token);
        try
        {
            var plan = GetPlan(id);
            if (rollback ? plan.Status is not ("Applied" or "Verified") : plan.Status != "Prepared")
                throw new InvalidOperationException("The plan is not eligible. Unknown outcomes require read-only reconciliation; do not replay.");
            if (!rollback && _clock.GetUtcNow() - plan.PreparedAtUtc > TimeSpan.FromMinutes(15))
                throw new InvalidOperationException("Preview expired. Prepare a fresh plan before applying.");
            await AuthorizeSameAsync(plan.Request, plan.ActorId, plan.OrganizationId, token);
            var identity = await VerifyAsync(plan.Request, token);
            CloudAccountVerifier.RequireSameIdentity(plan.CloudIdentity, identity);
            var current = await ReadAsync(plan.Request, identity, token);
            var expected = Parse(rollback ? plan.AfterJson : plan.BeforeJson);
            if (!Equivalent(current, expected)) throw new InvalidOperationException("Cloud state changed after preview/apply. Refresh and review; no write was attempted.");
            var desired = Parse(rollback ? plan.BeforeJson : plan.AfterJson);
            await AuthorizeSameAsync(plan.Request, plan.ActorId, plan.OrganizationId, token);
            CloudAccountVerifier.RequireSameIdentity(identity, await VerifyAsync(plan.Request, token));
            if (!rollback && _clock.GetUtcNow() - plan.PreparedAtUtc > TimeSpan.FromMinutes(15))
                throw new InvalidOperationException("Preview expired during verification. Prepare a fresh plan; no write was attempted.");
            plan = plan with { Status = rollback ? "RollbackStarted" : "ApplyStarted" };
            await journal.AppendAsync(plan, rollback ? "RollbackApproved" : "Approved", token);
            _plans[id] = plan;
            try
            {
                await WriteAsync(plan.Request, identity, current, desired, token);
                var observed = await ReadAsync(plan.Request, identity, token);
                if (!Equivalent(observed, desired))
                    throw new InvalidOperationException("The provider did not yet confirm the desired state. Reconcile after propagation; do not retry the write.");
                plan = plan with { Status = rollback ? "RolledBack" : "Applied", Notice = "Provider state verified. Effective access may still include inherited grants or other policies; run a fresh scan." };
                await journal.AppendAsync(plan, plan.Status, CancellationToken.None);
                _plans[id] = plan;
                return plan;
            }
            catch (Exception exception)
            {
                plan = plan with { Status = rollback ? "RollbackUnknown" : "ApplyUnknown", Notice = "A write may have occurred. Read-only reconciliation is required; automatic retries are disabled." };
                _plans[id] = plan;
                try { await journal.AppendAsync(plan, plan.Status, CancellationToken.None); }
                catch (Exception auditException)
                {
                    throw new CloudAdministrationException(plan, new AggregateException(exception, auditException), auditFailed: true);
                }
                throw new CloudAdministrationException(plan, exception);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<CloudAdministrationPlan> ReconcileAsync(Guid id, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var plan = GetPlan(id);
            if (plan.Status is not ("ApplyUnknown" or "RollbackUnknown"))
                throw new InvalidOperationException("Only unknown write outcomes need reconciliation.");
            await AuthorizeSameAsync(plan.Request, plan.ActorId, plan.OrganizationId, token);
            var identity = await VerifyAsync(plan.Request, token);
            CloudAccountVerifier.RequireSameIdentity(plan.CloudIdentity, identity);
            var observed = await ReadAsync(plan.Request, identity, token);
            var status = Equivalent(observed, Parse(plan.BeforeJson)) ? "RolledBack"
                : Equivalent(observed, Parse(plan.AfterJson)) ? "Applied" : plan.Status;
            plan = plan with { Status = status, Notice = status.EndsWith("Unknown", StringComparison.Ordinal)
                ? "Current cloud state matches neither snapshot. Manual incident review is required."
                : "Read-only reconciliation confirmed the stored snapshot. No write was retried." };
            await journal.AppendAsync(plan, "Reconciled", token);
            _plans[id] = plan;
            return plan;
        }
        finally { _gate.Release(); }
    }

    private CloudAdministrationPlan GetPlan(Guid id)
        => _plans.TryGetValue(id, out var plan) ? plan : throw new InvalidOperationException("Prepare a plan in this session. Local audit files cannot authorize replay.");

    private async Task<(OrganizationIdentity Actor, Guid Organization)> AuthorizeAsync(CloudAdministrationRequest request, CancellationToken token)
    {
        var actor = context.Identity ?? throw new UnauthorizedAccessException("Organization sign-in is required.");
        var organization = context.OrganizationId ?? throw new UnauthorizedAccessException("Select an organization.");
        IReadOnlyList<TenantBinding> bindings;
        if (sharedApi is not null)
        {
            var workspace = await sharedApi.GetWorkspaceAsync(organization, token);
            foreach (var action in new[] { OrganizationAction.ManageRemediations, OrganizationAction.ManagePolicies, OrganizationAction.ViewInventory })
                if (!workspace.AllowedActions.Contains(action)) throw new UnauthorizedAccessException("Current organization policy does not allow cloud administration.");
            bindings = workspace.Bindings;
        }
        else
        {
            foreach (var action in new[] { OrganizationAction.ManageRemediations, OrganizationAction.ManagePolicies, OrganizationAction.ViewInventory })
                if (!organizations.Authorize(organization, actor, action).Allowed) throw new UnauthorizedAccessException("Current organization policy does not allow cloud administration.");
            bindings = organizations.GetTenantBindings(organization, actor);
        }
        if (!bindings.Any(binding => binding.Provider == request.Provider && binding.ExternalTenantId == request.AccountId))
            throw new UnauthorizedAccessException("The target cloud account is not bound to this organization.");
        if (context.Identity != actor || context.OrganizationId != organization) throw new UnauthorizedAccessException("Organization context changed.");
        return (actor, organization);
    }

    private async Task AuthorizeSameAsync(CloudAdministrationRequest request, Guid actor, Guid organization, CancellationToken token)
    {
        var authorized = await AuthorizeAsync(request, token);
        if (authorized.Actor.UserId != actor || authorized.Organization != organization)
            throw new UnauthorizedAccessException("The signed-in administrator or organization changed.");
    }

    private Task<CloudAccountVerification> VerifyAsync(CloudAdministrationRequest request, CancellationToken token)
        => CloudAccountVerifier.VerifyAsync(runner, new(request.Provider.ToString(), request.AccountId, request.AccountId, request.Profile, ""), token);

    public static void Validate(CloudAdministrationRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.AccountId is null || r.Profile is null || r.Scope is null || r.Subject is null
            || r.SubjectKind is null || r.RoleOrPolicy is null || r.PolicyJson is null)
            throw new ArgumentException("Administration fields cannot be null.");
        if (!Enum.IsDefined(r.Operation) || string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Length > 2048
            || r.Profile.Length > 128 || r.Profile.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            || r.Profile.StartsWith('-') || r.PolicyJson.Length > 64 * 1024)
            throw new ArgumentException("Choose a supported operation, a safe CLI profile, and a review reason; policy size is limited to 64 KiB.");
        var name = r.Operation.ToString();
        if (name.StartsWith("Azure", StringComparison.Ordinal))
        {
            var root = $"/subscriptions/{r.AccountId}";
            if (r.Provider != CloudProvider.Azure || !Guid.TryParse(r.AccountId, out _)
                || !(r.Scope.Equals(root, StringComparison.OrdinalIgnoreCase) || r.Scope.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                || r.Scope.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.' or '(' or ')'))
                || r.Scope.Contains("//", StringComparison.Ordinal) || r.Scope.Contains("/..", StringComparison.Ordinal)
                || !Guid.TryParse(r.RoleOrPolicy, out _))
                throw new ArgumentException("Azure needs a bound subscription scope and a role definition GUID.");
            if (r.Operation != CloudAdministrationOperation.AzureUpdateCustomRole
                && (!Guid.TryParse(r.Subject, out _) || !Guid.TryParse(r.SubjectKind, out _)))
                throw new ArgumentException("Azure subject must be an Entra object GUID; target identifier must be a role-assignment GUID.");
        }
        else if (name.StartsWith("Aws", StringComparison.Ordinal))
        {
            if (r.Provider != CloudProvider.Aws || r.AccountId.Length != 12 || !r.AccountId.All(char.IsAsciiDigit)
                || r.SubjectKind is not ("user" or "role" or "group") || !IamName(r.Subject, 128))
                throw new ArgumentException("AWS requires a bound account and IAM user, role or group name. Devices/services use their IAM role.");
            if (r.Operation is CloudAdministrationOperation.AwsAttachManagedPolicy or CloudAdministrationOperation.AwsDetachManagedPolicy)
            {
                if (!r.RoleOrPolicy.StartsWith($"arn:aws:iam::{r.AccountId}:policy/", StringComparison.Ordinal)
                    && !r.RoleOrPolicy.StartsWith("arn:aws:iam::aws:policy/", StringComparison.Ordinal))
                    throw new ArgumentException("Choose an AWS managed policy ARN in this account or the aws namespace.");
                if (r.RoleOrPolicy.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is ':' or '/' or '-' or '_' or '+' or '=' or ',' or '.' or '@')))
                    throw new ArgumentException("Invalid managed policy ARN.");
            }
            else if (!IamName(r.RoleOrPolicy, 128)) throw new ArgumentException("Enter a valid inline policy name.");
        }
        else
        {
            if (r.Provider != CloudProvider.Gcp || r.AccountId.Length is < 6 or > 63
                || !char.IsAsciiLetterLower(r.AccountId[0]) || r.AccountId.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')))
                throw new ArgumentException("GCP requires a bound project ID.");
            if (r.Operation != CloudAdministrationOperation.GcpSetAllowPolicy
                && (r.RoleOrPolicy.Length is < 3 or > 63 || r.RoleOrPolicy.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))))
                throw new ArgumentException("Enter a GCP deny policy ID.");
        }
        if (r.Operation is CloudAdministrationOperation.AzureUpdateCustomRole or CloudAdministrationOperation.AwsPutInlinePolicy
            or CloudAdministrationOperation.GcpSetAllowPolicy or CloudAdministrationOperation.GcpPutDenyPolicy)
            _ = Parse(r.PolicyJson);
    }

    private static bool IamName(string value, int max)
        => value.Length is > 0 && value.Length <= max && !value.StartsWith('-')
            && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '=' or ',' or '.' or '@' or '_' or '-');
    private static JsonObject Parse(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject ?? throw new ArgumentException("Policy/state must be a JSON object."); }
        catch (JsonException) { throw new ArgumentException("Policy/state must be valid JSON."); }
    }

    private async Task<JsonObject> ReadAsync(CloudAdministrationRequest r, CloudAccountVerification identity, CancellationToken token)
    {
        if (r.Provider == CloudProvider.Azure)
        {
            if (r.Operation == CloudAdministrationOperation.AzureUpdateCustomRole)
            {
                var node = await JsonAsync("az", ["rest", "--method", "get", "--url", AzureUrl(r, true), "--subscription", r.AccountId, "--output", "json"], token);
                var properties = node["properties"] as JsonObject ?? throw new InvalidOperationException("Azure role properties are missing.");
                if (properties["type"]?.GetValue<string>() != "CustomRole") throw new InvalidOperationException("Built-in Azure roles cannot be edited.");
                return AzureRole(properties, r);
            }
            var list = await JsonAsync("az", ["role", "assignment", "list", "--scope", r.Scope, "--all", "--subscription", r.AccountId, "--output", "json"], token) as JsonArray
                ?? throw new InvalidOperationException("Invalid Azure assignment inventory.");
            var item = list.OfType<JsonObject>().SingleOrDefault(item => string.Equals(item["name"]?.GetValue<string>(), r.SubjectKind, StringComparison.OrdinalIgnoreCase));
            if (item is null) return new JsonObject();
            if (!string.Equals(item["scope"]?.GetValue<string>(), r.Scope, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(item["principalId"]?.GetValue<string>(), r.Subject, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(item["roleDefinitionId"]?.GetValue<string>(), AzureRoleId(r), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Role-assignment ID belongs to a different principal, role or scope.");
            return new JsonObject { ["properties"] = new JsonObject {
                ["principalId"] = r.Subject, ["roleDefinitionId"] = AzureRoleId(r),
                ["condition"] = item["condition"]?.DeepClone(), ["conditionVersion"] = item["conditionVersion"]?.DeepClone(),
                ["description"] = item["description"]?.DeepClone(), ["principalType"] = item["principalType"]?.DeepClone() } };
        }
        if (r.Provider == CloudProvider.Aws)
        {
            var subject = await JsonAsync("aws", AwsArgs(r, ["iam", $"get-{r.SubjectKind}", $"--{r.SubjectKind}-name", r.Subject, "--output", "json"]), token);
            var arn = subject[r.SubjectKind == "user" ? "User" : r.SubjectKind == "role" ? "Role" : "Group"]?["Arn"]?.GetValue<string>();
            if (arn is null || !arn.StartsWith($"arn:aws:iam::{r.AccountId}:", StringComparison.Ordinal)) throw new InvalidOperationException("IAM target is outside this account.");
            if (r.Operation is CloudAdministrationOperation.AwsAttachManagedPolicy or CloudAdministrationOperation.AwsDetachManagedPolicy)
            {
                var policies = await JsonAsync("aws", AwsArgs(r, ["iam", $"list-attached-{r.SubjectKind}-policies", $"--{r.SubjectKind}-name", r.Subject, "--output", "json"]), token);
                var items = policies["AttachedPolicies"] as JsonArray ?? throw new InvalidOperationException("Invalid attached-policy inventory.");
                return new JsonObject { ["attached"] = items.Any(item => item?["PolicyArn"]?.GetValue<string>() == r.RoleOrPolicy) };
            }
            var names = await JsonAsync("aws", AwsArgs(r, ["iam", $"list-{r.SubjectKind}-policies", $"--{r.SubjectKind}-name", r.Subject, "--output", "json"]), token);
            if (names["PolicyNames"] is not JsonArray policyNames) throw new InvalidOperationException("Invalid inline-policy inventory.");
            if (!policyNames.Any(item => item?.GetValue<string>() == r.RoleOrPolicy)) return new JsonObject();
            var policy = await JsonAsync("aws", AwsArgs(r, ["iam", $"get-{r.SubjectKind}-policy", $"--{r.SubjectKind}-name", r.Subject,
                "--policy-name", r.RoleOrPolicy, "--output", "json"]), token);
            var document = policy["PolicyDocument"] ?? throw new InvalidOperationException("Inline policy evidence is missing.");
            return document is JsonObject obj ? (JsonObject)obj.DeepClone() : Parse(Uri.UnescapeDataString(document.GetValue<string>()));
        }
        if (r.Operation == CloudAdministrationOperation.GcpSetAllowPolicy)
            return (JsonObject)await JsonAsync("gcloud", ["projects", "get-iam-policy", r.AccountId, "--account", identity.Identity, "--format=json"], token);
        var project = await JsonAsync("gcloud", ["projects", "describe", r.AccountId, "--account", identity.Identity, "--format=json"], token);
        var number = project["projectNumber"]?.ToString();
        if (string.IsNullOrEmpty(number) || !number.All(char.IsAsciiDigit)) throw new InvalidOperationException("GCP project number is missing.");
        var attachment = $"cloudresourcemanager.googleapis.com/projects/{number}";
        var listDeny = await JsonAsync("gcloud", ["iam", "policies", "list", "--attachment-point", attachment, "--kind", "denypolicies",
            "--account", identity.Identity, "--format=json"], token) as JsonArray ?? throw new InvalidOperationException("Invalid GCP deny policy inventory.");
        var existing = listDeny.OfType<JsonObject>().SingleOrDefault(item => item["name"]?.GetValue<string>()?.EndsWith("/denypolicies/" + r.RoleOrPolicy, StringComparison.Ordinal) == true);
        if (existing is null) return new JsonObject { ["attachmentPoint"] = attachment };
        var full = await JsonAsync("gcloud", ["iam", "policies", "get", r.RoleOrPolicy, "--attachment-point", attachment,
            "--kind", "denypolicies", "--account", identity.Identity, "--format=json"], token);
        if (full["name"]?.GetValue<string>() != existing["name"]?.GetValue<string>())
            throw new InvalidOperationException("GCP deny policy scope changed while reading.");
        return new JsonObject { ["attachmentPoint"] = attachment, ["policy"] = full };
    }

    private static JsonObject Proposed(CloudAdministrationRequest r, JsonObject before)
    {
        switch (r.Operation)
        {
            case CloudAdministrationOperation.AzureAssignRole:
                if (before.Count > 0) throw new InvalidOperationException("This role assignment already exists. Use a new preview for another assignment.");
                return new JsonObject { ["properties"] = new JsonObject { ["principalId"] = r.Subject, ["roleDefinitionId"] = AzureRoleId(r) } };
            case CloudAdministrationOperation.AzureRemoveRole:
            case CloudAdministrationOperation.AwsDeleteInlinePolicy:
                return new JsonObject();
            case CloudAdministrationOperation.AzureUpdateCustomRole:
                return AzureRole(Parse(r.PolicyJson)["properties"] as JsonObject ?? Parse(r.PolicyJson), r);
            case CloudAdministrationOperation.AwsAttachManagedPolicy:
            case CloudAdministrationOperation.AwsDetachManagedPolicy:
                return new JsonObject { ["attached"] = r.Operation == CloudAdministrationOperation.AwsAttachManagedPolicy };
            case CloudAdministrationOperation.AwsPutInlinePolicy:
                var aws = Parse(r.PolicyJson);
                if (aws["Statement"] is not (JsonArray or JsonObject)) throw new ArgumentException("IAM policy needs Statement evidence with Allow/Deny effects.");
                if (aws["Statement"] is JsonArray invalidArray && invalidArray.Any(item => item is not JsonObject))
                    throw new ArgumentException("Every inline policy statement must be an object.");
                var statements = aws["Statement"] is JsonArray array ? array.OfType<JsonObject>().ToArray() : [(JsonObject)aws["Statement"]!];
                if (statements.Length == 0 || statements.Any(item => item["Effect"]?.GetValue<string>() is not ("Allow" or "Deny")))
                    throw new ArgumentException("Every inline policy statement needs Effect Allow or Deny.");
                return aws;
            case CloudAdministrationOperation.GcpSetAllowPolicy:
                var allow = Parse(r.PolicyJson);
                if (allow["bindings"] is not JsonArray || before["etag"] is null) throw new ArgumentException("GCP IAM policy needs bindings and a provider etag.");
                if (!JsonNode.DeepEquals(allow["auditConfigs"], before["auditConfigs"]))
                    throw new ArgumentException("Preserve the current auditConfigs exactly. Permission editing must not disable or change audit logging.");
                allow["etag"] = before["etag"]!.DeepClone();
                allow["version"] = ((JsonArray)allow["bindings"]!).Any(binding => binding?["condition"] is not null)
                    ? 3 : before["version"]?.DeepClone() ?? JsonValue.Create(1);
                ValidateGcpAllowPolicyReduction(before, allow);
                return allow;
            case CloudAdministrationOperation.GcpPutDenyPolicy:
                var deny = Parse(r.PolicyJson);
                if (deny["rules"] is not JsonArray rules || rules.Count == 0 || rules.Any(rule => rule?["denyRule"] is not JsonObject))
                    throw new ArgumentException("GCP deny policy needs explicit rules with denyRule objects.");
                foreach (var key in new[] { "name", "uid", "etag", "createTime", "updateTime" }) deny.Remove(key);
                if (before["policy"]?["etag"] is { } etag) deny["etag"] = etag.DeepClone();
                return new JsonObject { ["attachmentPoint"] = before["attachmentPoint"]!.DeepClone(), ["policy"] = deny };
            case CloudAdministrationOperation.GcpDeleteDenyPolicy:
                return new JsonObject { ["attachmentPoint"] = before["attachmentPoint"]!.DeepClone() };
            default: throw new NotSupportedException();
        }
    }

    private static void ValidateGcpAllowPolicyReduction(JsonObject before, JsonObject proposed)
    {
        var existingBindings = before["bindings"] as JsonArray
            ?? throw new InvalidOperationException("Current GCP IAM policy has no valid bindings array.");
        var proposedBindings = proposed["bindings"] as JsonArray
            ?? throw new ArgumentException("Proposed GCP IAM policy needs a bindings array.");

        if (proposedBindings.Any(binding => binding is not JsonObject))
            throw new ArgumentException("Every proposed GCP binding must be a JSON object.");

        var proposedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in proposedBindings.Cast<JsonObject>())
        {
            var role = binding["role"]?.GetValue<string>();
            var members = binding["members"] as JsonArray;
            if (string.IsNullOrWhiteSpace(role) || members is null)
                throw new ArgumentException("Every proposed GCP binding needs a role and member list.");
            var bindingKey = $"{role}\n{binding["condition"]?.ToJsonString() ?? "(unconditional)"}";
            if (!proposedKeys.Add(bindingKey))
                throw new ArgumentException("Proposed GCP policy cannot contain duplicate role/condition bindings.");

            var matchingBindings = existingBindings.OfType<JsonObject>().Where(existing =>
                string.Equals(existing["role"]?.GetValue<string>(), role, StringComparison.Ordinal)
                && JsonNode.DeepEquals(existing["condition"], binding["condition"])).ToArray();
            if (matchingBindings.Length == 0)
                throw new ArgumentException("GCP allow-policy changes may only remove existing role bindings or members; new roles and conditions are blocked.");
            var existingMembers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var existing in matchingBindings)
            {
                if (existing["members"] is not JsonArray existingMemberList)
                    throw new InvalidOperationException("Current GCP IAM binding has no valid member list.");
                foreach (var existingMember in existingMemberList)
                {
                    if (existingMember?.GetValue<string>() is not { } value)
                        throw new InvalidOperationException("Current GCP IAM binding contains an invalid member.");
                    existingMembers.Add(value);
                }
            }
            if (members.Any(member => member?.GetValue<string>() is not { } value || !existingMembers.Contains(value)))
                throw new ArgumentException("GCP allow-policy changes may not add members to existing roles.");
        }

        var beforeMetadata = (JsonObject)before.DeepClone();
        beforeMetadata.Remove("bindings");
        beforeMetadata.Remove("etag");
        beforeMetadata.Remove("version");
        var proposedMetadata = (JsonObject)proposed.DeepClone();
        proposedMetadata.Remove("bindings");
        proposedMetadata.Remove("etag");
        proposedMetadata.Remove("version");
        if (!JsonNode.DeepEquals(beforeMetadata, proposedMetadata))
            throw new ArgumentException("GCP allow-policy updates may not modify fields other than bindings, etag, and version.");

        var removed = existingBindings.Count != proposedBindings.Count
            || existingBindings.OfType<JsonObject>().Any(existing =>
            {
                var matching = proposedBindings.OfType<JsonObject>().FirstOrDefault(candidate =>
                    string.Equals(candidate["role"]?.GetValue<string>(), existing["role"]?.GetValue<string>(), StringComparison.Ordinal)
                    && JsonNode.DeepEquals(candidate["condition"], existing["condition"]));
                var oldMembers = existing["members"] as JsonArray ?? new JsonArray();
                var newMembers = matching?["members"] as JsonArray ?? new JsonArray();
                return oldMembers.Any(member => !newMembers.Any(candidate => JsonNode.DeepEquals(member, candidate)));
            });
        if (!removed)
            throw new ArgumentException("GCP allow-policy updates in this workflow must remove at least one existing binding or member.");
    }

    private static JsonObject AzureRole(JsonObject properties, CloudAdministrationRequest r)
    {
        if (properties["permissions"] is not JsonArray || properties["roleName"] is null
            || properties["assignableScopes"] is not JsonArray scopes || scopes.Count == 0
            || scopes.Any(scope => scope?.GetValue<string>() is not { } text
                || !(text.Equals($"/subscriptions/{r.AccountId}", StringComparison.OrdinalIgnoreCase)
                    || text.StartsWith($"/subscriptions/{r.AccountId}/", StringComparison.OrdinalIgnoreCase))))
            throw new ArgumentException("Custom role needs permissions, roleName and assignableScopes restricted to this subscription. NotActions are not deny rules.");
        return new JsonObject { ["properties"] = new JsonObject {
            ["roleName"] = properties["roleName"]!.DeepClone(), ["description"] = properties["description"]?.DeepClone(),
            ["type"] = "CustomRole", ["permissions"] = properties["permissions"]!.DeepClone(), ["assignableScopes"] = scopes.DeepClone() } };
    }

    private async Task WriteAsync(CloudAdministrationRequest r, CloudAccountVerification identity, JsonObject current, JsonObject desired, CancellationToken token)
    {
        string command;
        List<string> args;
        JsonObject? body = null;
        if (r.Provider == CloudProvider.Azure)
        {
            command = "az";
            args = ["rest", "--method", desired.Count == 0 ? "delete" : "put", "--url", AzureUrl(r, r.Operation == CloudAdministrationOperation.AzureUpdateCustomRole),
                "--subscription", r.AccountId, "--output", "json"];
            if (desired.Count > 0) body = desired;
        }
        else if (r.Provider == CloudProvider.Aws)
        {
            command = "aws";
            if (r.Operation is CloudAdministrationOperation.AwsAttachManagedPolicy or CloudAdministrationOperation.AwsDetachManagedPolicy)
                args = AwsArgs(r, ["iam", $"{(desired["attached"]!.GetValue<bool>() ? "attach" : "detach")}-{r.SubjectKind}-policy",
                    $"--{r.SubjectKind}-name", r.Subject, "--policy-arn", r.RoleOrPolicy]).ToList();
            else
            {
                args = AwsArgs(r, ["iam", $"{(desired.Count == 0 ? "delete" : "put")}-{r.SubjectKind}-policy",
                    $"--{r.SubjectKind}-name", r.Subject, "--policy-name", r.RoleOrPolicy]).ToList();
                if (desired.Count > 0) body = desired;
            }
        }
        else
        {
            command = "gcloud";
            if (r.Operation == CloudAdministrationOperation.GcpSetAllowPolicy)
            {
                body = (JsonObject)desired.DeepClone();
                body["etag"] = current["etag"]?.DeepClone() ?? throw new InvalidOperationException("Missing current GCP etag.");
                args = ["projects", "set-iam-policy", r.AccountId];
            }
            else
            {
                var exists = current["policy"] is not null;
                var remove = desired["policy"] is null;
                args = ["iam", "policies", remove ? "delete" : exists ? "update" : "create", r.RoleOrPolicy,
                    "--attachment-point", current["attachmentPoint"]!.GetValue<string>(), "--kind", "denypolicies"];
                if (exists)
                    args.AddRange(["--etag", current["policy"]?["etag"]?.GetValue<string>() ?? throw new InvalidOperationException("Missing GCP deny-policy etag.")]);
                if (!remove)
                {
                    body = (JsonObject)desired["policy"]!.DeepClone();
                    foreach (var key in new[] { "name", "uid", "etag", "createTime", "updateTime" }) body.Remove(key);
                }
            }
            args.AddRange(["--account", identity.Identity, "--format=json", "--quiet"]);
        }
        string? file = null;
        try
        {
            if (body is not null)
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "YASK-CIEM", "cloud-administration", "staging");
                CloudAdministrationJournal.EnsurePrivateDirectory(directory);
                file = Path.Combine(directory, $"yask-admin-{Guid.NewGuid():N}.json");
                await using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await JsonSerializer.SerializeAsync(stream, body, cancellationToken: token);
                if (r.Provider == CloudProvider.Azure) args.AddRange(["--body", "@" + file]);
                else if (r.Provider == CloudProvider.Aws) args.AddRange(["--policy-document", "file://" + file]);
                else if (r.Operation == CloudAdministrationOperation.GcpSetAllowPolicy) args.Insert(3, file);
                else args.AddRange(["--policy-file", file]);
            }
            var result = await runner.RunAsync(command, args, token);
            if (!result.IsSuccess) throw new InvalidOperationException($"Cloud write returned exit {result.ExitCode}. Outcome requires reconciliation; provider errors/credentials are not displayed.");
        }
        finally { if (file is not null) File.Delete(file); }
    }

    private async Task<JsonNode> JsonAsync(string command, IReadOnlyList<string> args, CancellationToken token)
    {
        var result = await runner.RunAsync(command, args, token);
        if (!result.IsSuccess) throw new InvalidOperationException($"Cloud administration read failed (exit {result.ExitCode}). Check CLI identity and permissions; no write was attempted by this read.");
        if (result.StandardOutput.Length > 1024 * 1024) throw new InvalidOperationException("Cloud policy inventory exceeds the 1 MiB response limit.");
        try { return JsonNode.Parse(result.StandardOutput) ?? throw new InvalidOperationException("Cloud returned no policy evidence."); }
        catch (JsonException) { throw new InvalidOperationException("Cloud returned malformed policy evidence."); }
    }

    private static IReadOnlyList<string> AwsArgs(CloudAdministrationRequest r, IReadOnlyList<string> args)
        => args.Concat(["--no-cli-pager"]).Concat(r.Profile.Length == 0 ? [] : new[] { "--profile", r.Profile }).ToArray();
    private static string AzureRoleId(CloudAdministrationRequest r)
        => $"/subscriptions/{r.AccountId}/providers/Microsoft.Authorization/roleDefinitions/{r.RoleOrPolicy}";
    private static string AzureUrl(CloudAdministrationRequest r, bool role)
        => $"https://management.azure.com{(role ? $"/subscriptions/{r.AccountId}" : r.Scope)}/providers/Microsoft.Authorization/{(role ? "roleDefinitions" : "roleAssignments")}/{(role ? r.RoleOrPolicy : r.SubjectKind)}?api-version=2022-04-01";

    internal static bool Equivalent(JsonObject left, JsonObject right) => Canonical(Normalize(left)) == Canonical(Normalize(right));
    private static JsonObject Normalize(JsonObject original)
    {
        var node = (JsonObject)original.DeepClone();
        node.Remove("etag");
        if (node["policy"] is JsonObject policy)
            foreach (var key in new[] { "etag", "name", "uid", "createTime", "updateTime" }) policy.Remove(key);
        if (node["properties"] is JsonObject properties) properties.Remove("principalType");
        return node;
    }
    private static string Canonical(JsonNode? node)
    {
        if (node is JsonObject obj)
            return "{" + string.Join(",", obj.Where(pair => pair.Value is not null)
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => JsonSerializer.Serialize(pair.Key) + ":" + Canonical(pair.Value))) + "}";
        if (node is JsonArray array) return "[" + string.Join(",", array.Select(Canonical).Order(StringComparer.Ordinal)) + "]";
        return node?.ToJsonString() ?? "null";
    }
}
