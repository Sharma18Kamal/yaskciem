namespace CloudEntitle.Cloud.Aws;

using System.Text.Json;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Cloud.Aws.Mappers;
using CloudEntitle.Cloud.Aws.Services;

public class AwsProvider : ICloudProvider, IIdentityProvider, IResourceProvider, IRoleProvider, IPermissionProvider, ICloudEffectiveAccessProvider, IAccessAnalyzerFindingsProvider, ICloudOrganizationProvider
{
    private readonly ICloudCommandRunner? _commandRunner;
    private readonly bool _useLiveDiscovery;
    private readonly Dictionary<Guid, CloudAccount> _accounts = new();
    private readonly Dictionary<Guid, string?> _accountProfiles = new();
    private readonly List<string> _providerMessages = new();

    public AwsProvider(ICloudCommandRunner? commandRunner = null, bool useLiveDiscovery = false)
    {
        _commandRunner = commandRunner;
        _useLiveDiscovery = useLiveDiscovery && commandRunner is not null;
    }

    public CloudProviderType ProviderType => CloudProviderType.Aws;

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery) return true;
        var profiles = await GetConfiguredProfilesAsync(cancellationToken);
        foreach (var profile in profiles)
        {
            var result = await RunAwsAsync(new[] { "sts", "get-caller-identity", "--output", "json" }, cancellationToken, profile);
            if (result.IsSuccess) return true;
        }
        return false;
    }

    public async Task<IReadOnlyList<CloudAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery) return MockAccounts();
        var profiles = await GetConfiguredProfilesAsync(cancellationToken);
        var accounts = new List<CloudAccount>();
        var discoveredProfiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles)
        {
            if (profile is not null && !discoveredProfiles.Add(profile)) continue;
            var result = await RunAwsAsync(new[] { "sts", "get-caller-identity", "--output", "json" }, cancellationToken, profile);
            if (!result.IsSuccess) continue;

            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            var externalAccountId = root.TryGetProperty("Account", out var accountId) ? accountId.GetString() : null;
            if (string.IsNullOrWhiteSpace(externalAccountId)) continue;
            var arnText = root.TryGetProperty("Arn", out var arn) ? arn.GetString() : null;
            var permissionSet = ExtractRoleName(arnText);
            var account = new CloudAccount
            {
                Id = Guid.NewGuid(),
                Name = arnText ?? $"AWS {externalAccountId}",
                Description = string.IsNullOrWhiteSpace(profile) ? $"Permission set: {permissionSet}" : $"CLI profile: {profile}; permission set: {permissionSet}",
                CloudProvider = CloudProviderType.Aws,
                ExternalAccountId = externalAccountId,
                AccountType = AccountType.AwsAccount,
                ConnectionStatus = ConnectionStatus.Connected,
                IsReadOnly = true,
                LastScanAt = DateTime.UtcNow
            };
            _accounts[account.Id] = account;
            _accountProfiles[account.Id] = profile;
            accounts.Add(account);
        }
        return accounts;
    }

    public async Task<CloudOrganizationScanResult> DiscoverOrganizationAccountsAsync(CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery) return new CloudOrganizationScanResult(Array.Empty<CloudAccount>(), Array.Empty<string>());

        var messages = new List<string>(_providerMessages);
        _providerMessages.Clear();
        foreach (var profile in _accountProfiles.Values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var result = await RunAwsAsync(new[] { "organizations", "list-accounts", "--output", "json" }, cancellationToken, profile);
            if (!result.IsSuccess)
            {
                messages.Add($"AWS Organizations roster unavailable for profile {profile ?? "default"}: {result.StandardError.Trim()}");
                continue;
            }

            using var document = JsonDocument.Parse(result.StandardOutput);
            if (!document.RootElement.TryGetProperty("Accounts", out var accounts) || accounts.ValueKind != JsonValueKind.Array)
            {
                messages.Add("AWS Organizations returned no account roster.");
                continue;
            }

            var roster = accounts.EnumerateArray().Select(item =>
            {
                var externalAccountId = item.TryGetProperty("Id", out var id) ? id.GetString() : null;
                var hasOrganizationStatus = item.TryGetProperty("Status", out var organizationStatus);
                var isProfileConnected = externalAccountId is not null && _accounts.Values.Any(account => account.ExternalAccountId == externalAccountId);
                return new CloudAccount
                {
                    Id = Guid.NewGuid(),
                    Name = item.TryGetProperty("Name", out var name) ? name.GetString() ?? "AWS member account" : "AWS member account",
                    CloudProvider = CloudProviderType.Aws,
                    ExternalAccountId = externalAccountId,
                    AccountType = AccountType.AwsAccount,
                    ConnectionStatus = isProfileConnected ? ConnectionStatus.Connected : ConnectionStatus.Disconnected,
                    IsReadOnly = true,
                    Description = hasOrganizationStatus ? $"AWS Organizations status: {organizationStatus.GetString()}" : "AWS Organizations member account"
                };
            }).Where(account => !string.IsNullOrWhiteSpace(account.ExternalAccountId)).ToArray();
            return new CloudOrganizationScanResult(roster, messages);
        }

        return new CloudOrganizationScanResult(Array.Empty<CloudAccount>(), messages);
    }

    public async Task<IReadOnlyList<CloudPrincipal>> DiscoverIdentitiesAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery) return MockIdentities(accountId);
        using var document = JsonDocument.Parse(await GetAwsOutputAsync(new[] { "iam", "list-users", "--output", "json" }, cancellationToken, accountId));
        var principals = document.RootElement.GetProperty("Users").EnumerateArray().Select(user => new CloudPrincipal
        {
            Id = Guid.NewGuid(), AccountId = accountId, CloudProvider = CloudProviderType.Aws,
            Name = user.GetProperty("UserName").GetString() ?? "unknown-user",
            PrincipalId = user.TryGetProperty("UserId", out var userId) ? userId.GetString() : null,
            LastActivity = user.TryGetProperty("PasswordLastUsed", out var passwordLastUsed) && passwordLastUsed.ValueKind == JsonValueKind.String
                ? $"Console password used {passwordLastUsed.GetString()}"
                : null,
            IdentityType = IdentityType.User, IsActive = true
        }).ToList();

        using var authorization = JsonDocument.Parse(await GetAwsOutputAsync(
            new[] { "iam", "get-account-authorization-details", "--filter", "Role", "--output", "json" }, cancellationToken, accountId));
        if (authorization.RootElement.TryGetProperty("RoleDetailList", out var roles) && roles.ValueKind == JsonValueKind.Array)
        {
            foreach (var role in roles.EnumerateArray())
            {
                var trustDocument = ReadTrustDocument(role);
                var federationTrusts = ReadWorkloadTrust(trustDocument);
                if (federationTrusts is null) continue;

                var roleName = ReadString(role, "RoleName") ?? "AWS workload role";
                var roleArn = ReadString(role, "Arn");
                var tagsAvailable = role.TryGetProperty("Tags", out var tags) && tags.ValueKind == JsonValueKind.Array;
                var ownerIds = tagsAvailable
                    ? tags.EnumerateArray()
                        .Where(tag => ReadString(tag, "Key") is string key && IsOwnerTag(key))
                        .Select(tag => ReadString(tag, "Value"))
                        .Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                    : new List<string>();
                var hasUsageHistory = role.TryGetProperty("RoleLastUsed", out var lastUsed) && lastUsed.ValueKind == JsonValueKind.Object;
                var lastActivity = hasUsageHistory ? ReadDate(lastUsed, "LastUsedDate") : null;
                var principal = new CloudPrincipal
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    CloudProvider = CloudProviderType.Aws,
                    Name = roleName,
                    PrincipalId = roleArn ?? roleName,
                    IdentityType = IdentityType.ServiceAccount,
                    IsActive = true,
                    OwnerIds = ownerIds,
                    OwnerInventoryComplete = tagsAvailable,
                    OwnerEvidenceSource = tagsAvailable ? "AWS IAM role tags (owner, owner-email, business-owner)" : null,
                    ActivityEvidenceComplete = hasUsageHistory,
                    ActivityObservationDays = hasUsageHistory ? 400 : null,
                    ActivityEvidenceSource = hasUsageHistory ? "AWS IAM RoleLastUsed; maximum 400-day history" : null,
                    LastActivityAt = lastActivity,
                    FederationInventoryComplete = true,
                    FederationTrusts = federationTrusts.ToList()
                };
                if (principal.FederationTrusts.Count > 0)
                {
                    principal.FederationIssuer = principal.FederationTrusts[0].Issuer;
                    principal.FederationSubject = principal.FederationTrusts[0].Subject;
                    principal.FederationAudience = string.Join(",", principal.FederationTrusts[0].Audiences);
                }
                principals.Add(principal);
            }
        }
        return principals;
    }

    private static DateTime? ReadDate(JsonElement element, string property)
        => DateTime.TryParse(ReadString(element, property), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var date)
                ? date
                : null;

    private static bool IsOwnerTag(string key)
        => key.Equals("owner", StringComparison.OrdinalIgnoreCase)
            || key.Equals("owner-email", StringComparison.OrdinalIgnoreCase)
            || key.Equals("business-owner", StringComparison.OrdinalIgnoreCase);

    private static JsonElement ReadTrustDocument(JsonElement role)
    {
        if (!role.TryGetProperty("AssumeRolePolicyDocument", out var document)) return default;
        if (document.ValueKind == JsonValueKind.Object) return document;
        if (document.ValueKind == JsonValueKind.String && document.GetString() is { } json)
        {
            try
            {
                using var parsed = JsonDocument.Parse(json);
                return parsed.RootElement.Clone();
            }
            catch (JsonException) { }
        }
        return default;
    }

    private static IReadOnlyList<WorkloadFederationTrust>? ReadWorkloadTrust(JsonElement trustDocument)
    {
        if (trustDocument.ValueKind != JsonValueKind.Object || !trustDocument.TryGetProperty("Statement", out var statementElement)) return null;
        var statements = statementElement.ValueKind == JsonValueKind.Array ? statementElement.EnumerateArray().ToArray() : new[] { statementElement };
        var isWorkload = false;
        var trusts = new List<WorkloadFederationTrust>();
        foreach (var statement in statements)
        {
            if (!statement.TryGetProperty("Principal", out var principal) || principal.ValueKind != JsonValueKind.Object) continue;
            var action = statement.TryGetProperty("Action", out var actionElement)
                ? (actionElement.ValueKind == JsonValueKind.Array ? actionElement.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()).ToArray() : new[] { actionElement.GetString() })
                : Array.Empty<string?>();
            var services = ReadPrincipalValues(principal, "Service");
            if (services.Count > 0 && action.Any(value => value?.StartsWith("sts:AssumeRole", StringComparison.OrdinalIgnoreCase) == true)) isWorkload = true;

            var federated = ReadPrincipalValues(principal, "Federated");
            if (federated.Count == 0 || !action.Any(value => string.Equals(value, "sts:AssumeRoleWithWebIdentity", StringComparison.OrdinalIgnoreCase))) continue;
            isWorkload = true;
            var condition = statement.TryGetProperty("Condition", out var conditionElement) ? conditionElement : default;
            var conditionValues = EnumerateConditionValues(condition).ToArray();
            var subject = conditionValues.FirstOrDefault(item => item.Key.EndsWith(":sub", StringComparison.OrdinalIgnoreCase)).Value;
            var audience = conditionValues.Where(item => item.Key.EndsWith(":aud", StringComparison.OrdinalIgnoreCase)).Select(item => item.Value).Distinct(StringComparer.Ordinal).ToArray();
            trusts.Add(new WorkloadFederationTrust(federated[0], subject ?? string.Empty, audience, "AWS IAM AssumeRoleWithWebIdentity trust policy"));
        }
        return isWorkload ? trusts : null;
    }

    private static IReadOnlyList<string> ReadPrincipalValues(JsonElement principal, string property)
    {
        if (!principal.TryGetProperty(property, out var value)) return Array.Empty<string>();
        return value.ValueKind == JsonValueKind.String ? new[] { value.GetString()! }
            : value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray()
                : Array.Empty<string>();
    }

    private static IEnumerable<KeyValuePair<string, string>> EnumerateConditionValues(JsonElement condition)
    {
        if (condition.ValueKind != JsonValueKind.Object) yield break;
        foreach (var operatorElement in condition.EnumerateObject())
        {
            if (operatorElement.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (var item in operatorElement.Value.EnumerateObject())
            {
                if (item.Value.ValueKind == JsonValueKind.String) yield return new(item.Name, item.Value.GetString()!);
                else if (item.Value.ValueKind == JsonValueKind.Array)
                    foreach (var value in item.Value.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String))
                        yield return new(item.Name, value.GetString()!);
            }
        }
    }

    public async Task<IReadOnlyList<CloudPermission>> DiscoverPermissionsAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery) return MockPermissions(accountId);
        using var document = JsonDocument.Parse(await GetAwsOutputAsync(new[] { "iam", "list-policies", "--scope", "Local", "--only-attached", "--output", "json" }, cancellationToken, accountId));
        return document.RootElement.GetProperty("Policies").EnumerateArray().Select(policy => new CloudPermission
        {
            Id = Guid.NewGuid(), AccountId = accountId,
            Name = policy.GetProperty("PolicyName").GetString() ?? "Unknown policy",
            Action = policy.TryGetProperty("PolicyArn", out var arn) ? arn.GetString() : null,
            Resource = policy.TryGetProperty("PolicyArn", out arn) ? arn.GetString() : null,
            Scope = "AWS account", Source = "AWS IAM managed policy"
        }).ToArray();
    }

    public async Task<IReadOnlyList<CloudRole>> DiscoverRolesAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery) return MockRoles(accountId);
        using var document = JsonDocument.Parse(await GetAwsOutputAsync(new[] { "iam", "list-roles", "--output", "json" }, cancellationToken, accountId));
        return document.RootElement.GetProperty("Roles").EnumerateArray().Select(role => new CloudRole
        {
            Id = Guid.NewGuid(), AccountId = accountId,
            Name = role.GetProperty("RoleName").GetString() ?? "Unknown role",
            RoleId = role.TryGetProperty("Arn", out var arn) ? arn.GetString() ?? string.Empty : string.Empty,
            Description = role.TryGetProperty("Description", out var description) ? description.GetString() : null,
            Scope = "AWS account"
        }).ToArray();
    }

    public async Task<IReadOnlyList<CloudEffectiveAccessAssignment>> DiscoverEffectiveAccessAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery) return Array.Empty<CloudEffectiveAccessAssignment>();

        using var document = JsonDocument.Parse(await GetAwsOutputAsync(
            new[] { "iam", "get-account-authorization-details", "--filter", "User", "Role", "Group", "--output", "json" },
            cancellationToken,
            accountId));
        var assignments = new List<CloudEffectiveAccessAssignment>();
        AddAssignments("UserDetailList", "UserName", "AttachedManagedPolicies", "UserPolicyList", "IAM user policy", "UserPolicyName");
        AddAssignments("GroupDetailList", "GroupName", "AttachedManagedPolicies", "GroupPolicyList", "IAM group policy", "GroupPolicyName");
        AddAssignments("RoleDetailList", "RoleName", "AttachedManagedPolicies", "RolePolicyList", "IAM role policy", "RolePolicyName");
        AddRoleUsage();
        await AddCloudTrailChangeTimesAsync(cancellationToken);
        return assignments;

        async Task AddCloudTrailChangeTimesAsync(CancellationToken token)
        {
            var profile = _accountProfiles.TryGetValue(accountId, out var configuredProfile) ? configuredProfile : null;
            var result = await RunAwsAsync(new[]
            {
                "cloudtrail", "lookup-events", "--lookup-attributes", "AttributeKey=EventSource,AttributeValue=iam.amazonaws.com",
                "--max-items", "50", "--region", "us-east-1", "--output", "json"
            }, token, profile);
            if (!result.IsSuccess)
            {
                _providerMessages.Add($"AWS IAM change history unavailable: {result.StandardError.Trim()}");
                return;
            }

            using var eventsDocument = JsonDocument.Parse(result.StandardOutput);
            if (!eventsDocument.RootElement.TryGetProperty("Events", out var events) || events.ValueKind != JsonValueKind.Array) return;
            var latestByTarget = new Dictionary<string, (DateTime ChangedAt, string? ChangedBy)>(StringComparer.OrdinalIgnoreCase);
            foreach (var cloudTrailEvent in events.EnumerateArray())
            {
                if (!cloudTrailEvent.TryGetProperty("EventName", out var eventName) || eventName.ValueKind != JsonValueKind.String) continue;
                var name = eventName.GetString();
                if (name is null || !IsAccessChangeEvent(name)
                    || !cloudTrailEvent.TryGetProperty("EventTime", out var eventTime)
                    || !DateTime.TryParse(eventTime.GetString(), out var changedAt)
                    || !cloudTrailEvent.TryGetProperty("CloudTrailEvent", out var serializedEvent)
                    || serializedEvent.ValueKind != JsonValueKind.String) continue;

                using var eventDocument = JsonDocument.Parse(serializedEvent.GetString()!);
                string? changedBy = null;
                if (eventDocument.RootElement.TryGetProperty("userIdentity", out var userIdentity) && userIdentity.ValueKind == JsonValueKind.Object)
                {
                    changedBy = userIdentity.TryGetProperty("arn", out var actorArn) ? actorArn.GetString()
                        : userIdentity.TryGetProperty("principalId", out var actorId) ? actorId.GetString()
                        : null;
                }
                if (!eventDocument.RootElement.TryGetProperty("requestParameters", out var requestParameters)
                    || requestParameters.ValueKind != JsonValueKind.Object) continue;
                foreach (var targetProperty in new[] { "roleName", "userName", "groupName", "policyName" })
                {
                    if (!requestParameters.TryGetProperty(targetProperty, out var target) || target.ValueKind != JsonValueKind.String) continue;
                    var targetName = target.GetString();
                    if (string.IsNullOrWhiteSpace(targetName)) continue;
                    if (!latestByTarget.TryGetValue(targetName, out var existing) || changedAt > existing.ChangedAt)
                    {
                        latestByTarget[targetName] = (changedAt.ToUniversalTime(), changedBy);
                    }
                }
            }

            for (var index = 0; index < assignments.Count; index++)
            {
                var assignment = assignments[index];
                var change = latestByTarget.Where(item => item.Key.Equals(assignment.Principal, StringComparison.OrdinalIgnoreCase)
                        || item.Key.Equals(assignment.Role, StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.Value)
                    .OrderByDescending(item => item.ChangedAt)
                    .FirstOrDefault();
                if (change.ChangedAt != default)
                {
                    assignments[index] = assignment with { LastChangedAt = change.ChangedAt, LastChangedBy = change.ChangedBy, Source = $"{assignment.Source}; CloudTrail IAM change" };
                }
            }
        }

        static bool IsAccessChangeEvent(string eventName)
            => eventName.StartsWith("Create", StringComparison.OrdinalIgnoreCase)
                || eventName.StartsWith("Delete", StringComparison.OrdinalIgnoreCase)
                || eventName.StartsWith("Put", StringComparison.OrdinalIgnoreCase)
                || eventName.StartsWith("Attach", StringComparison.OrdinalIgnoreCase)
                || eventName.StartsWith("Detach", StringComparison.OrdinalIgnoreCase)
                || eventName.StartsWith("Update", StringComparison.OrdinalIgnoreCase)
                || eventName.StartsWith("Add", StringComparison.OrdinalIgnoreCase)
                || eventName.StartsWith("Remove", StringComparison.OrdinalIgnoreCase);

        void AddRoleUsage()
        {
            if (!document.RootElement.TryGetProperty("RoleDetailList", out var roles) || roles.ValueKind != JsonValueKind.Array) return;
            foreach (var role in roles.EnumerateArray())
            {
                var hasUsageHistory = role.TryGetProperty("RoleLastUsed", out var lastUsed) && lastUsed.ValueKind == JsonValueKind.Object;
                DateTime? lastUsedAt = null;
                if (hasUsageHistory && lastUsed.TryGetProperty("LastUsedDate", out var lastUsedDate)
                    && lastUsedDate.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(lastUsedDate.GetString(), out var parsedDate))
                    lastUsedAt = DateTime.SpecifyKind(parsedDate, DateTimeKind.Utc);
                var roleName = role.TryGetProperty("RoleName", out var name) ? name.GetString() ?? "IAM role" : "IAM role";
                var region = hasUsageHistory && lastUsed.TryGetProperty("Region", out var regionValue) ? regionValue.GetString() : null;
                var usageDetails = lastUsedAt.HasValue
                    ? $"AWS IAM role last used {lastUsedAt:O}{(string.IsNullOrWhiteSpace(region) ? string.Empty : $" in {region}")}."
                    : hasUsageHistory
                        ? "AWS IAM reports no role use within the available 400-day RoleLastUsed history."
                        : "AWS did not return RoleLastUsed metadata for this role.";
                assignments.Add(new CloudEffectiveAccessAssignment(
                    roleName,
                    roleName,
                    "AWS account",
                    $"AWS IAM RoleLastUsed; {usageDetails}",
                    roleName.Contains("Administrator", StringComparison.OrdinalIgnoreCase),
                    "Role usage",
                    lastUsedAt,
                    UsageHistoryComplete: hasUsageHistory,
                    UsageHistoryDays: 400,
                    UsageEvidenceSource: "AWS IAM ListRoles.RoleLastUsed"));
            }
        }

        void AddAssignments(string collectionName, string principalProperty, string attachedProperty, string inlineProperty, string source, string inlinePolicyProperty)
        {
            if (!document.RootElement.TryGetProperty(collectionName, out var principals) || principals.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var item in principals.EnumerateArray())
            {
                var principal = item.TryGetProperty(principalProperty, out var principalElement) ? principalElement.GetString() ?? "Unknown principal" : "Unknown principal";
                if (item.TryGetProperty(attachedProperty, out var attached) && attached.ValueKind == JsonValueKind.Array)
                {
                    foreach (var policy in attached.EnumerateArray())
                    {
                        Add(principal, policy.TryGetProperty("PolicyName", out var name) ? name.GetString() ?? "Managed policy" : "Managed policy", source);
                    }
                }
                if (item.TryGetProperty(inlineProperty, out var inlinePolicies) && inlinePolicies.ValueKind == JsonValueKind.Array)
                {
                    foreach (var policy in inlinePolicies.EnumerateArray())
                    {
                        Add(principal, policy.TryGetProperty(inlinePolicyProperty, out var name) ? name.GetString() ?? "Inline policy" : "Inline policy", source);
                    }
                }
            }
        }

        void Add(string principal, string policyName, string source)
        {
            var privileged = policyName.Contains("Administrator", StringComparison.OrdinalIgnoreCase)
                || policyName.Contains("PowerUser", StringComparison.OrdinalIgnoreCase)
                || policyName.Contains("FullAccess", StringComparison.OrdinalIgnoreCase);
            assignments.Add(new CloudEffectiveAccessAssignment(principal, policyName, "AWS account", source, privileged));
        }
    }

    public async Task<IReadOnlyList<RiskFinding>> DiscoverAccessAnalyzerFindingsAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery) return Array.Empty<RiskFinding>();
        var analyzersJson = await GetAwsOutputAsync(new[] { "accessanalyzer", "list-analyzers", "--output", "json" }, cancellationToken, accountId);
        using var analyzersDocument = JsonDocument.Parse(analyzersJson);
        if (!analyzersDocument.RootElement.TryGetProperty("analyzers", out var analyzers) || analyzers.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<RiskFinding>();
        }

        var findings = new List<RiskFinding>();
        foreach (var analyzer in analyzers.EnumerateArray())
        {
            if (!string.Equals(ReadString(analyzer, "status"), "ACTIVE", StringComparison.OrdinalIgnoreCase)
                || ReadString(analyzer, "arn") is not { } analyzerArn)
            {
                continue;
            }

            using var findingsDocument = JsonDocument.Parse(await GetAwsOutputAsync(
                new[] { "accessanalyzer", "list-findings-v2", "--analyzer-arn", analyzerArn, "--output", "json" },
                cancellationToken,
                accountId));
            if (!findingsDocument.RootElement.TryGetProperty("findings", out var analyzerFindings) || analyzerFindings.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var finding in analyzerFindings.EnumerateArray())
            {
                if (!string.Equals(ReadString(finding, "status"), "ACTIVE", StringComparison.OrdinalIgnoreCase)) continue;
                var resource = ReadString(finding, "resource") ?? "Unknown AWS resource";
                var findingType = ReadString(finding, "findingType") ?? "Access Analyzer finding";
                var principal = finding.TryGetProperty("principal", out var principalValue) ? principalValue.ToString() : "External or unspecified principal";
                var createdAt = DateTime.TryParse(ReadString(finding, "createdAt"), out var created) ? created.ToUniversalTime() : DateTime.UtcNow;
                findings.Add(new RiskFinding
                {
                    Id = Guid.NewGuid(),
                    RuleId = Guid.NewGuid(),
                    CloudProvider = CloudProviderType.Aws,
                    CloudAccountId = accountId,
                    Severity = findingType.Contains("Unused", StringComparison.OrdinalIgnoreCase) ? RiskSeverity.Medium : RiskSeverity.High,
                    Title = $"AWS Access Analyzer: {findingType}",
                    Description = $"Access Analyzer reported active access for {resource}.",
                    Principal = principal,
                    Resource = resource,
                    Permission = ReadString(finding, "action") ?? findingType,
                    Evidence = finding.GetRawText(),
                    Recommendation = "Review the Access Analyzer finding and confirm the resource policy is intentional.",
                    CreatedAt = createdAt,
                    UpdatedAt = DateTime.UtcNow
                });
            }
        }
        return findings;
    }

    private static string? ReadString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    public Task<IReadOnlyList<CloudPrincipal>> GetPrincipalsAsync(Guid accountId, CancellationToken cancellationToken = default) => DiscoverIdentitiesAsync(accountId, cancellationToken);
    public Task<IReadOnlyList<CloudPermission>> GetPermissionsAsync(Guid accountId, CancellationToken cancellationToken = default) => DiscoverPermissionsAsync(accountId, cancellationToken);
    public Task<IReadOnlyList<CloudRole>> GetRolesAsync(Guid accountId, CancellationToken cancellationToken = default) => DiscoverRolesAsync(accountId, cancellationToken);
    public async Task<IReadOnlyList<CloudResource>> GetResourcesAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery) return Array.Empty<CloudResource>();
        using var document = JsonDocument.Parse(await GetAwsOutputAsync(
            new[] { "resourcegroupstaggingapi", "get-resources", "--resources-per-page", "100", "--output", "json" },
            cancellationToken, accountId));
        if (!document.RootElement.TryGetProperty("ResourceTagMappingList", out var resources) || resources.ValueKind != JsonValueKind.Array)
            return Array.Empty<CloudResource>();

        return resources.EnumerateArray().Select(resource =>
        {
            var arn = resource.TryGetProperty("ResourceARN", out var resourceArn) ? resourceArn.GetString() ?? string.Empty : string.Empty;
            var tags = resource.TryGetProperty("Tags", out var tagList) && tagList.ValueKind == JsonValueKind.Array
                ? tagList.EnumerateArray().Where(tag => tag.TryGetProperty("Key", out _) && tag.TryGetProperty("Value", out _))
                    .ToDictionary(tag => tag.GetProperty("Key").GetString() ?? string.Empty,
                        tag => tag.GetProperty("Value").GetString() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var resourceSeparator = Math.Max(arn.LastIndexOf('/'), arn.LastIndexOf(':'));
            var resourceName = resourceSeparator >= 0 ? arn[(resourceSeparator + 1)..] : arn;
            var type = arn.StartsWith("arn:", StringComparison.OrdinalIgnoreCase)
                ? arn.Split(':').Skip(2).FirstOrDefault() ?? "AWS resource"
                : "AWS resource";
            return new CloudResource
            {
                Id = Guid.NewGuid(), AccountId = accountId, ExternalId = arn,
                Name = string.IsNullOrWhiteSpace(resourceName) ? arn : resourceName,
                Type = type, ResourceType = ResourceType.Resource, ResourceUri = arn, Scope = arn, Tags = tags
            };
        }).Where(resource => !string.IsNullOrWhiteSpace(resource.ExternalId)).ToArray();
    }

    public async Task<IReadOnlyList<CloudGroup>> DiscoverGroupsAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery) return new[] { new CloudGroup { Id = Guid.NewGuid(), AccountId = accountId, Name = "AWS Demo Security Group", GroupId = "demo-group" } };
        using var document = JsonDocument.Parse(await GetAwsOutputAsync(new[] { "iam", "list-groups", "--output", "json" }, cancellationToken, accountId));
        return document.RootElement.GetProperty("Groups").EnumerateArray().Select(group => new CloudGroup
        {
            Id = Guid.NewGuid(), AccountId = accountId, Name = group.TryGetProperty("GroupName", out var name) ? name.GetString() ?? "AWS group" : "AWS group", GroupId = group.TryGetProperty("GroupId", out var id) ? id.GetString() : null, ProviderPath = group.TryGetProperty("Path", out var path) ? path.GetString() : null
        }).ToArray();
    }

    public async Task<IReadOnlyList<CloudPolicy>> DiscoverPoliciesAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!_useLiveDiscovery) return new[] { new CloudPolicy { Id = Guid.NewGuid(), AccountId = accountId, Name = "AWS Demo Policy", ExternalId = "demo-policy", PolicyType = "CustomerManaged", Document = "{\"Version\":\"2012-10-17\",\"Statement\":[{\"Effect\":\"Allow\",\"Action\":\"*\",\"Resource\":\"*\"}]}" } };
        using var document = JsonDocument.Parse(await GetAwsOutputAsync(new[] { "iam", "list-policies", "--scope", "Local", "--output", "json" }, cancellationToken, accountId));
        return document.RootElement.GetProperty("Policies").EnumerateArray().Select(policy => new CloudPolicy
        {
            Id = Guid.NewGuid(), AccountId = accountId, Name = policy.TryGetProperty("PolicyName", out var name) ? name.GetString() ?? "AWS policy" : "AWS policy", ExternalId = policy.TryGetProperty("Arn", out var arn) ? arn.GetString() : null, PolicyType = policy.TryGetProperty("PolicyType", out var type) ? type.GetString() : "CustomerManaged", Document = policy.TryGetProperty("Document", out var document) ? document.GetString() : null, DocumentVersion = policy.TryGetProperty("DefaultVersionId", out var version) ? version.GetString() : null, IsManaged = true
        }).ToArray();
    }

    private async Task<string> GetAwsOutputAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, Guid? accountId = null)
    {
        var profile = accountId.HasValue && _accountProfiles.TryGetValue(accountId.Value, out var configuredProfile) ? configuredProfile : null;
        var result = await RunAwsAsync(arguments, cancellationToken, profile);
        if (!result.IsSuccess) throw new InvalidOperationException($"AWS CLI command failed: {result.StandardError}");
        return result.StandardOutput;
    }

    private Task<CloudCommandResult> RunAwsAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, string? profile = null)
    {
        var profiledArguments = string.IsNullOrWhiteSpace(profile)
            ? arguments
            : new[] { "--profile", profile }.Concat(arguments).ToArray();
        return RunAwsWithRetryAsync(profiledArguments, cancellationToken);
    }

    private async Task<IReadOnlyList<string?>> GetConfiguredProfilesAsync(CancellationToken cancellationToken)
    {
        var result = await _commandRunner!.RunAsync("aws", new[] { "configure", "list-profiles" }, cancellationToken);
        if (!result.IsSuccess)
        {
            return new string?[] { null };
        }

        var profiles = result.StandardOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .Cast<string?>()
            .ToArray();
        return profiles.Length == 0 ? new string?[] { null } : profiles;
    }

    private static string ExtractRoleName(string? arn)
    {
        const string assumedRoleMarker = ":assumed-role/";
        var markerIndex = arn?.LastIndexOf(assumedRoleMarker, StringComparison.OrdinalIgnoreCase) ?? -1;
        return markerIndex < 0 ? string.Empty : arn![(markerIndex + assumedRoleMarker.Length)..].Split('/')[0];
    }

    public async Task<AwsScanResult> ScanAsync(Guid? accountId = null, CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;
        var accounts = new List<CloudAccount>();
        var principals = new List<CloudPrincipal>();
        var groups = new List<CloudGroup>();
        var roles = new List<CloudRole>();
        var policies = new List<CloudPolicy>();
        var permissions = new List<CloudPermission>();
        var entitlements = new List<Entitlement>();
        var trusts = new List<AwsTrustRelationship>();
        var errors = new List<string>();
        try
        {
            accounts.AddRange(await DiscoverAccountsAsync(cancellationToken));
            if (accountId.HasValue) accounts = accounts.Where(account => account.Id == accountId.Value).ToList();
            foreach (var account in accounts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    principals.AddRange(await DiscoverIdentitiesAsync(account.Id, cancellationToken));
                    groups.AddRange(await DiscoverGroupsAsync(account.Id, cancellationToken));
                    roles.AddRange(await DiscoverRolesAsync(account.Id, cancellationToken));
                    policies.AddRange(await DiscoverPoliciesAsync(account.Id, cancellationToken));
                    foreach (var policy in policies.Where(policy => policy.AccountId == account.Id && !string.IsNullOrWhiteSpace(policy.Document) && policy.Document!.TrimStart().StartsWith("{"))) permissions.AddRange(AwsPolicyMapper.MapPolicy(account.Id, policy.Name, policy.ExternalId, policy.Document!));
                    foreach (var role in roles.Where(role => role.AccountId == account.Id))
                    {
                        var trust = await GetRoleTrustPolicyAsync(account, role, cancellationToken);
                        trusts.AddRange(AwsPolicyMapper.MapTrustPrincipals(trust).Select(principal => new AwsTrustRelationship(principals.FirstOrDefault(item => item.PrincipalId == principal)?.Id ?? Guid.Empty, role.Id, principal, "AWS IAM AssumeRole trust policy")));
                    }
                    entitlements.AddRange(permissions.Where(permission => permission.AccountId == account.Id).Select(permission => new Entitlement { Id = Guid.NewGuid(), PermissionId = permission.Id, CloudAccountId = account.Id, AssignmentType = AssignmentType.Policy, GrantedAt = startedAt, Source = "AWS IAM policy" }));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    errors.Add($"{account.Name}: {exception.Message}");
                }
            }

            var scan = new Scan { Id = Guid.NewGuid(), CloudAccountId = accounts.FirstOrDefault()?.Id ?? Guid.Empty, StartedAt = startedAt, CompletedAt = DateTime.UtcNow, Status = errors.Count == accounts.Count && accounts.Count > 0 ? ScanStatus.Failed : ScanStatus.Completed, ItemsDiscovered = policies.Count + groups.Count, IdentitiesDiscovered = principals.Count, PermissionsDiscovered = permissions.Count, ErrorMessage = errors.Count == 0 ? null : string.Join(" | ", errors) };
            return new AwsScanResult(scan, accounts, principals, groups, roles, policies, permissions, entitlements, trusts, errors);
        }
        catch (OperationCanceledException)
        {
            return new AwsScanResult(new Scan { Id = Guid.NewGuid(), CloudAccountId = accounts.FirstOrDefault()?.Id ?? Guid.Empty, StartedAt = startedAt, CompletedAt = DateTime.UtcNow, Status = ScanStatus.Cancelled, ErrorMessage = "AWS scan cancelled." }, accounts, principals, groups, roles, policies, permissions, entitlements, trusts, errors);
        }
    }

    private async Task<string> GetRoleTrustPolicyAsync(CloudAccount account, CloudRole role, CancellationToken cancellationToken)
    {
        var result = await GetAwsOutputAsync(new[] { "iam", "get-role", "--role-name", role.Name, "--output", "json" }, cancellationToken, account.Id);
        using var document = JsonDocument.Parse(result);
        return document.RootElement.TryGetProperty("Role", out var roleElement) && roleElement.TryGetProperty("AssumeRolePolicyDocument", out var policy) ? policy.GetString() ?? "{}" : "{}";
    }

    private async Task<CloudCommandResult> RunAwsWithRetryAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        CloudCommandResult? result = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = await _commandRunner!.RunAsync("aws", arguments, cancellationToken);
            if (result.IsSuccess || attempt == 2) return result;
            await Task.Delay(TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt)), cancellationToken);
        }
        return result!;
    }

    private static IReadOnlyList<CloudAccount> MockAccounts() => new[] { new CloudAccount { Id = Guid.NewGuid(), Name = "AWS Demo Account", CloudProvider = CloudProviderType.Aws, ExternalAccountId = "123456789012", ConnectionStatus = ConnectionStatus.Connected, IsReadOnly = true } };
    private static IReadOnlyList<CloudPrincipal> MockIdentities(Guid accountId) => new[] { new CloudPrincipal { Id = Guid.NewGuid(), AccountId = accountId, CloudProvider = CloudProviderType.Aws, Name = "admin-user", IdentityType = IdentityType.User, IsPrivileged = true, IsActive = true } };
    private static IReadOnlyList<CloudPermission> MockPermissions(Guid accountId) => new[] { new CloudPermission { Id = Guid.NewGuid(), AccountId = accountId, Name = "AdministratorAccess", Action = "*", Resource = "*", Scope = "account:123456789012", Source = "AWS IAM Managed Policy" } };
    private static IReadOnlyList<CloudRole> MockRoles(Guid accountId) => new[] { new CloudRole { Id = Guid.NewGuid(), AccountId = accountId, Name = "AdministratorAccess", RoleId = "arn:aws:iam::123456789012:role/Admin", Description = "Full administrative access", Scope = "account:123456789012" } };
}
