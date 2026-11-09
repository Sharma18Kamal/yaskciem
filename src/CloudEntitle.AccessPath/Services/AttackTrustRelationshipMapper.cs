using System.Security.Cryptography;
using System.Text;
using CloudEntitle.AccessPath.Models;
using CloudEntitle.Domain;

namespace CloudEntitle.AccessPath.Services;

public static class AttackTrustRelationshipMapper
{
    public static IReadOnlyList<AttackTrustRelationship> FromProviderEvidence(
        IReadOnlyList<CloudPrincipal> principals)
    {
        ArgumentNullException.ThrowIfNull(principals);
        var result = new List<AttackTrustRelationship>();

        foreach (var target in principals)
        {
            foreach (var trust in target.FederationTrusts)
            {
                if (TryReadAwsPrincipal(trust.Issuer, out var trustedPrincipal))
                {
                    var matchingSources = principals.Where(source => source.Id != target.Id
                        && source.CloudProvider == CloudProviderType.Aws
                        && AwsPrincipalMatches(trustedPrincipal, source.PrincipalId)).ToArray();
                    if (matchingSources.Length > 0)
                    {
                        foreach (var source in matchingSources)
                        {
                            var conditionState = string.IsNullOrWhiteSpace(trust.ConditionExpression)
                                ? target.FederationInventoryComplete == true ? "Satisfied" : "Unknown"
                                : "Unknown";
                            result.Add(CreateTrust(source.Id, source.AccountId, CloudProvider.Aws,
                                target, CloudProvider.Aws, "AWS AssumeRole", target.FederationInventoryComplete == true ? "Confirmed" : "Potential",
                                trust.Source, $"{target.PrincipalId ?? target.Name}|{trustedPrincipal}",
                                true, "sts:AssumeRole", conditionState,
                                string.IsNullOrWhiteSpace(trust.ConditionExpression)
                                    ? "AWS AssumeRole trust policy was inventoried; source-side sts:AssumeRole access is evaluated separately."
                                    : $"AWS trust condition was retained but not evaluated against runtime request context: {trust.ConditionExpression}"));
                        }
                    }
                    else
                    {
                        var sourceNode = CreateExternalSource(target, CloudProvider.Aws, trustedPrincipal,
                            "Trusted AWS account or principal; source identity was not inventoried.");
                        result.Add(CreateTrust(sourceNode.Id, sourceNode.AccountId, CloudProvider.Aws,
                            target, CloudProvider.Aws, "AWS AssumeRole", "Potential", trust.Source,
                            $"{target.PrincipalId ?? target.Name}|{trustedPrincipal}",
                            true, "sts:AssumeRole", "Unknown",
                            "The trust policy names this AWS principal, but the source identity and its sts:AssumeRole permission were not inventoried.",
                            observedAt: ObservedAt(target), sourceName: sourceNode.Name,
                            sourceDetails: sourceNode.Details, createSourceNode: true, sourceIsInitialAccessPoint: true));
                    }
                    continue;
                }

                if (trust.Source.Contains("AssumeRoleWithWebIdentity", StringComparison.OrdinalIgnoreCase))
                {
                    var claims = FederationClaims(trust);
                    var sourceNode = CreateExternalSource(target, CloudProvider.Aws, trust.Issuer,
                        $"External OIDC source; subject={trust.Subject}; audiences={string.Join(",", trust.Audiences)}");
                    result.Add(CreateTrust(sourceNode.Id, sourceNode.AccountId, CloudProvider.Aws,
                        target, CloudProvider.Aws, "AWS web identity federation", "Potential", trust.Source,
                        claims, false, null, "Unknown",
                        $"Issuer, subject, audience and trust-condition evidence were inventoried. Runtime token claims and AWS request context were not observed. Condition={trust.ConditionExpression ?? "none"}",
                        observedAt: ObservedAt(target), sourceName: sourceNode.Name,
                        sourceDetails: sourceNode.Details, createSourceNode: true, sourceIsInitialAccessPoint: true));
                    continue;
                }

                AddFederationTrust(principals, target, trust, result);
            }

            foreach (var trust in target.IdentityTrusts)
            {
                var matchingSources = principals.Where(source => source.Id != target.Id
                    && source.CloudProvider == target.CloudProvider
                    && PrincipalMatches(trust.TrustedPrincipal, source)).ToArray();
                if (matchingSources.Length > 0)
                {
                    foreach (var source in matchingSources)
                    {
                        result.Add(CreateTrust(source.Id, source.AccountId, ToProvider(source.CloudProvider),
                            target, ToProvider(target.CloudProvider), trust.RelationshipType, trust.Status,
                            trust.Source, $"{target.PrincipalId ?? target.Name}|{trust.TrustedPrincipal}",
                            false, null, trust.ConditionState,
                            trust.ConditionEvidence ?? BuildConditionEvidence(trust),
                            ObservedAt(target), sourceDetails: trust.ConditionExpression));
                    }
                    continue;
                }

                var canCreateExternalSource = trust.IsExternalSource
                    || IsExplicitExternalPrincipal(trust.TrustedPrincipal);
                var sourceProvider = ToProvider(target.CloudProvider);
                var sourceNode = CreateExternalSource(target, sourceProvider, trust.TrustedPrincipal,
                    $"Provider IAM principal={trust.TrustedPrincipal}; {trust.RelationshipType}");
                result.Add(CreateTrust(sourceNode.Id, sourceNode.AccountId, sourceProvider,
                    target, ToProvider(target.CloudProvider), trust.RelationshipType, trust.Status,
                    trust.Source, $"{target.PrincipalId ?? target.Name}|{trust.TrustedPrincipal}",
                    false, null, trust.ConditionState, trust.ConditionEvidence ?? BuildConditionEvidence(trust),
                    ObservedAt(target), sourceNode.Name, sourceNode.Details,
                    createSourceNode: true,
                    sourceIsInitialAccessPoint: canCreateExternalSource && trust.IsInitialAccessPoint));
            }
        }

        return result.DistinctBy(item => (item.SourcePrincipalId, item.TargetPrincipalId,
            item.RelationshipType, item.EvidenceReference)).ToArray();
    }

    private static void AddFederationTrust(IReadOnlyList<CloudPrincipal> principals, CloudPrincipal target,
        WorkloadFederationTrust trust, ICollection<AttackTrustRelationship> result)
    {
        var issuer = NormalizeIssuer(trust.Issuer);
        var matchingSources = principals.Where(source => source.Id != target.Id
            && !string.IsNullOrWhiteSpace(source.FederationIssuer)
            && string.Equals(NormalizeIssuer(source.FederationIssuer), issuer, StringComparison.OrdinalIgnoreCase)
            && string.Equals(source.FederationSubject, trust.Subject, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(source.FederationAudience)
            && source.FederationAudience.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(audience => trust.Audiences.Contains(audience, StringComparer.Ordinal))).ToArray();
        var claims = FederationClaims(trust);
        if (matchingSources.Length > 0)
        {
            foreach (var source in matchingSources)
            {
                var verified = source.FederationTrustVerified == true && target.FederationTrustVerified == true
                    && source.FederationInventoryComplete == true && target.FederationInventoryComplete == true
                    && string.IsNullOrWhiteSpace(trust.ConditionExpression);
                result.Add(CreateTrust(source.Id, source.AccountId, ToProvider(source.CloudProvider),
                    target, ToProvider(target.CloudProvider), "Federated workload identity",
                    verified ? "Confirmed" : "Potential",
                    string.IsNullOrWhiteSpace(trust.Source) ? "Federation inventory" : trust.Source,
                    claims, false, null,
                    verified ? "Satisfied" : "Unknown",
                    verified ? "Provider trust inventory verified." :
                        $"Issuer, subject, audience and claims constraints were retained; runtime assertion context was not observed. Condition={trust.ConditionExpression ?? "none"}",
                    ObservedAt(target)));
            }
            return;
        }

        var provider = InferIssuerProvider(trust.Issuer, target.CloudProvider);
        var sourceIdentity = CreateExternalSource(target, provider, $"{issuer}|{trust.Subject}|{string.Join(",", trust.Audiences.Order(StringComparer.Ordinal))}",
            $"Federated identity issuer={issuer}; subject={trust.Subject}; audiences={string.Join(",", trust.Audiences)}" +
            (string.IsNullOrWhiteSpace(trust.ConditionExpression) ? string.Empty : $"; claims expression={trust.ConditionExpression}"));
        result.Add(CreateTrust(sourceIdentity.Id, sourceIdentity.AccountId, provider,
            target, ToProvider(target.CloudProvider), "Federated workload identity", "Potential",
            string.IsNullOrWhiteSpace(trust.Source) ? "Federation inventory" : trust.Source,
            claims, false, null, "Unknown",
            $"The provider trust configuration is explicit, but the source workload and runtime token claims were not independently observed. Condition={trust.ConditionExpression ?? "none"}",
            observedAt: ObservedAt(target), sourceName: sourceIdentity.Name,
            sourceDetails: sourceIdentity.Details, createSourceNode: true, sourceIsInitialAccessPoint: true));
    }

    private static AttackTrustRelationship CreateTrust(Guid sourceId, Guid sourceAccountId, CloudProvider sourceProvider,
        CloudPrincipal target, CloudProvider targetProvider, string type, string status, string evidenceSource,
        string evidenceReference, bool RequiresSourcePermission, string? requiredPermission,
        string conditionState, string? conditionEvidence, DateTime? observedAt = null,
        string? sourceName = null, string? sourceDetails = null, bool createSourceNode = false,
        bool sourceIsInitialAccessPoint = false)
        => new(sourceId, sourceAccountId, sourceProvider, target.Id, target.AccountId, targetProvider,
            type, status, evidenceSource, evidenceReference, observedAt ?? ObservedAt(target),
            RequiresSourcePermission, requiredPermission, conditionState, conditionEvidence,
            sourceName, sourceDetails, createSourceNode, sourceIsInitialAccessPoint);

    private static (Guid Id, Guid AccountId, string Name, string Details) CreateExternalSource(
        CloudPrincipal target, CloudProvider provider, string identity, string details)
    {
        var stable = $"{provider}|{identity}";
        return (StableGuid($"external-principal|{stable}"), StableGuid($"external-account|{stable}"),
            $"Trust source: {identity}", details);
    }

    private static bool TryReadAwsPrincipal(string issuer, out string principal)
    {
        const string prefix = "aws-principal:";
        principal = issuer.StartsWith(prefix, StringComparison.Ordinal) ? issuer[prefix.Length..] : string.Empty;
        return principal.Length > 0;
    }

    private static bool PrincipalMatches(string trustedPrincipal, CloudPrincipal source)
        => string.Equals(trustedPrincipal, source.PrincipalId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trustedPrincipal, source.Email, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trustedPrincipal, $"{(source.IdentityType == IdentityType.ServiceAccount ? "serviceAccount" : "user")}:{source.Email}", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trustedPrincipal, $"{(source.IdentityType == IdentityType.ServiceAccount ? "serviceAccount" : "user")}:{source.PrincipalId}", StringComparison.OrdinalIgnoreCase);

    private static bool IsExplicitExternalPrincipal(string principal)
        => principal.StartsWith("principal://", StringComparison.OrdinalIgnoreCase)
            || principal.StartsWith("principalSet://", StringComparison.OrdinalIgnoreCase)
            || principal.StartsWith("system:serviceaccount:", StringComparison.OrdinalIgnoreCase);

    private static string BuildConditionEvidence(CloudIdentityTrust trust)
        => string.IsNullOrWhiteSpace(trust.ConditionExpression)
            ? "Provider IAM trust binding was inventoried."
            : $"Provider condition state={trust.ConditionState}; expression={trust.ConditionExpression}";

    private static string FederationClaims(WorkloadFederationTrust trust)
        => $"{NormalizeIssuer(trust.Issuer)}|{trust.Subject}|{string.Join(",", trust.Audiences.Order(StringComparer.Ordinal))}";

    private static DateTime ObservedAt(CloudPrincipal principal)
        => (principal.UpdatedAt ?? principal.CreatedAt).ToUniversalTime();

    private static string NormalizeIssuer(string? issuer) => issuer?.Trim().TrimEnd('/') ?? string.Empty;

    private static CloudProvider InferIssuerProvider(string issuer, CloudProviderType targetProvider)
    {
        if (issuer.Contains("amazonaws.com", StringComparison.OrdinalIgnoreCase)) return CloudProvider.Aws;
        if (issuer.Contains("googleapis.com", StringComparison.OrdinalIgnoreCase)
            || issuer.Contains("googleusercontent.com", StringComparison.OrdinalIgnoreCase)) return CloudProvider.Gcp;
        if (issuer.Contains("microsoftonline.com", StringComparison.OrdinalIgnoreCase)
            || issuer.Contains("windows.net", StringComparison.OrdinalIgnoreCase)) return CloudProvider.Azure;
        if (targetProvider is CloudProviderType.Aws or CloudProviderType.Azure or CloudProviderType.Gcp)
            return ToProvider(targetProvider);
        return CloudProvider.Kubernetes;
    }

    private static bool AwsPrincipalMatches(string trustedPrincipal, string? sourcePrincipal)
    {
        if (string.IsNullOrWhiteSpace(sourcePrincipal)) return false;
        if (string.Equals(trustedPrincipal, sourcePrincipal, StringComparison.Ordinal)) return true;
        const string prefix = "arn:aws:iam::";
        const string suffix = ":root";
        if (!trustedPrincipal.StartsWith(prefix, StringComparison.Ordinal)
            || !trustedPrincipal.EndsWith(suffix, StringComparison.Ordinal)
            || !sourcePrincipal.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var trustedAccount = trustedPrincipal[prefix.Length..^suffix.Length];
        var separator = sourcePrincipal.IndexOf(':', prefix.Length);
        return separator > prefix.Length
            && string.Equals(sourcePrincipal[prefix.Length..separator], trustedAccount, StringComparison.Ordinal);
    }

    private static Guid StableGuid(string value)
        => new(SHA256.HashData(Encoding.UTF8.GetBytes(value))[..16]);

    private static CloudProvider ToProvider(CloudProviderType provider) => provider switch
    {
        CloudProviderType.Aws => CloudProvider.Aws,
        CloudProviderType.Azure => CloudProvider.Azure,
        CloudProviderType.Gcp => CloudProvider.Gcp,
        _ => CloudProvider.Kubernetes
    };
}
