using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;
using CloudEntitle.Normalization.Abstractions;
using CloudEntitle.Normalization.Models;

namespace CloudEntitle.Normalization.Services;

public sealed class EntitlementNormalizationService : IEntitlementNormalizationService
{
    private readonly IReadOnlyDictionary<CloudProvider, IEntitlementNormalizer> _normalizers;

    public EntitlementNormalizationService(IEnumerable<IEntitlementNormalizer>? normalizers = null)
    {
        _normalizers = (normalizers ?? new IEntitlementNormalizer[]
        {
            new AzureEntitlementNormalizer(), new AwsEntitlementNormalizer(), new GcpEntitlementNormalizer()
        }).ToDictionary(normalizer => normalizer.Provider);
    }

    public Task<NormalizationResult> NormalizeAsync(ProviderDiscoveryResult source, CancellationToken cancellationToken = default)
    {
        if (!_normalizers.TryGetValue(source.Provider, out var normalizer))
        {
            return Task.FromResult(new NormalizationResult(source.Principals, source.Roles, source.Policies, source.Permissions, Array.Empty<NormalizedEntitlement>(), Array.Empty<NormalizationWarning>(), new[] { $"Unsupported provider: {source.Provider}" }, new(0, 0, 0, source.AuthorizationRecords.Count, 0)));
        }

        return normalizer.NormalizeAsync(source, cancellationToken);
    }
}

public abstract class ProviderEntitlementNormalizer : IEntitlementNormalizer
{
    public abstract CloudProvider Provider { get; }

    public Task<NormalizationResult> NormalizeAsync(ProviderDiscoveryResult source, CancellationToken cancellationToken = default)
    {
        if (source.Provider != Provider)
        {
            return Task.FromResult(new NormalizationResult(Array.Empty<CloudPrincipal>(), Array.Empty<CloudRole>(), Array.Empty<CloudPolicy>(), Array.Empty<CloudPermission>(), Array.Empty<NormalizedEntitlement>(), Array.Empty<NormalizationWarning>(), new[] { $"Provider mismatch: expected {Provider}, received {source.Provider}" }, new(0, 0, 0, 0, 0)));
        }

        var warnings = new List<NormalizationWarning>();
        var errors = new List<string>();
        var entitlements = new Dictionary<string, NormalizedEntitlement>(StringComparer.Ordinal);
        var processed = 0;
        var partial = 0;
        var failed = 0;
        foreach (var record in source.AuthorizationRecords)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;
            var validation = ValidateRecord(source, record);
            if (validation.Count > 0)
            {
                failed++;
                errors.AddRange(validation);
                continue;
            }

            var conditional = !string.IsNullOrWhiteSpace(record.ConditionExpression);
            var status = conditional ? NormalizationStatus.PartiallyNormalized : NormalizationStatus.Normalized;
            if (conditional)
            {
                partial++;
                warnings.Add(new(Provider, record.SourceId, status, "Condition preserved but not evaluated by the normalization layer."));
            }

            var originalAssignment = record.ProviderSpecificData?.TryGetValue("assignment", out var assignment) == true ? assignment : record.AssignmentType.ToString();
            var evidence = new ProviderEvidence(Provider, record.SourceType, record.SourceId, record.SourceName, record.OriginalDocument, record.OriginalStatement, record.Scope, originalAssignment, source.CollectedAt, record.ProviderSpecificData ?? new Dictionary<string, string>());
            var scope = new NormalizedScope(Provider, ClassifyScope(record.Scope), record.Scope, record.SourceScope, record.TargetScope, record.IsInherited);
            var normalizedAccess = conditional ? NormalizedAccessType.Conditional : record.AccessType switch { AccessType.Allow => NormalizedAccessType.Allow, AccessType.Deny => NormalizedAccessType.Deny, _ => NormalizedAccessType.Unknown };
            var condition = conditional ? new NormalizedCondition(record.ConditionTitle, record.ConditionExpression, "Unknown", record.ConditionExpression) : null;
            var key = string.Join('|', Provider, record.CloudAccountId, record.PrincipalId, record.RoleId, record.PolicyId, record.PermissionId, record.Scope, record.ResourcePattern, normalizedAccess, record.AssignmentType, record.ConditionExpression);
            if (entitlements.ContainsKey(key)) continue;
            entitlements[key] = new NormalizedEntitlement(GuidUtility.Create(GuidUtility.Namespace, key), Provider, record.CloudAccountId, record.PrincipalId, null, record.RoleId, record.PolicyId, record.PermissionId, scope, record.AssignmentType, normalizedAccess, !record.IsInherited && record.AssignmentType is AssignmentType.Direct or AssignmentType.Role or AssignmentType.Policy, record.IsInherited, conditional, condition, status, evidence, source.CollectedAt, null, record.ResourcePattern);
        }

        return Task.FromResult(new NormalizationResult(source.Principals, source.Roles, source.Policies, source.Permissions, entitlements.Values.ToArray(), warnings, errors, new(processed, entitlements.Count, partial, 0, failed)));
    }

    private static IReadOnlyList<string> ValidateRecord(ProviderDiscoveryResult source, ProviderAuthorizationRecord record)
    {
        var errors = new List<string>();
        if (!source.Accounts.Any(account => account.Id == record.CloudAccountId)) errors.Add($"{source.Provider}:{record.SourceId}: cloud account is missing.");
        if (!source.Principals.Any(principal => principal.Id == record.PrincipalId)) errors.Add($"{source.Provider}:{record.SourceId}: principal is missing.");
        if (!source.Permissions.Any(permission => permission.Id == record.PermissionId)) errors.Add($"{source.Provider}:{record.SourceId}: permission is missing.");
        if (record.RoleId.HasValue && !source.Roles.Any(role => role.Id == record.RoleId)) errors.Add($"{source.Provider}:{record.SourceId}: role is missing.");
        if (record.PolicyId.HasValue && !source.Policies.Any(policy => policy.Id == record.PolicyId)) errors.Add($"{source.Provider}:{record.SourceId}: policy is missing.");
        if (string.IsNullOrWhiteSpace(record.Scope)) errors.Add($"{source.Provider}:{record.SourceId}: scope is missing.");
        return errors;
    }

    private static NormalizedScopeType ClassifyScope(string scope)
    {
        if (scope.Contains("managementGroups", StringComparison.OrdinalIgnoreCase)) return NormalizedScopeType.ManagementGroup;
        if (scope.Contains("subscriptions", StringComparison.OrdinalIgnoreCase)) return scope.Contains("resourceGroups", StringComparison.OrdinalIgnoreCase) ? NormalizedScopeType.ResourceGroup : NormalizedScopeType.Subscription;
        if (scope.Contains("folders", StringComparison.OrdinalIgnoreCase)) return NormalizedScopeType.Folder;
        if (scope.Contains("projects", StringComparison.OrdinalIgnoreCase)) return NormalizedScopeType.Project;
        if (scope.StartsWith("arn:", StringComparison.OrdinalIgnoreCase)) return NormalizedScopeType.Resource;
        if (scope.Contains("/", StringComparison.Ordinal)) return NormalizedScopeType.Resource;
        return NormalizedScopeType.Unknown;
    }
}

public sealed class AzureEntitlementNormalizer : ProviderEntitlementNormalizer { public override CloudProvider Provider => CloudProvider.Azure; }
public sealed class AwsEntitlementNormalizer : ProviderEntitlementNormalizer { public override CloudProvider Provider => CloudProvider.Aws; }
public sealed class GcpEntitlementNormalizer : ProviderEntitlementNormalizer { public override CloudProvider Provider => CloudProvider.Gcp; }

internal static class GuidUtility
{
    public static readonly Guid Namespace = Guid.Parse("7c31cbf1-3b9f-4cc2-b9d2-0f3f0f02c6af");
    public static Guid Create(Guid namespaceId, string name)
    {
        var hash = System.Security.Cryptography.SHA1.HashData(namespaceId.ToByteArray().Concat(System.Text.Encoding.UTF8.GetBytes(name)).ToArray());
        var bytes = hash.Take(16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes);
    }
}
