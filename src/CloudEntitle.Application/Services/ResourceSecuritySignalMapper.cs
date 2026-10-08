using System.Globalization;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;
using CloudEntitle.Domain.Entities;

namespace CloudEntitle.Application.Services;

public sealed record ResourceSecuritySignalMapping(
    IReadOnlyList<ResourceSecuritySignal> Signals,
    IReadOnlyList<ResourceSecurityEvidenceCoverage> Coverage);

public static class ResourceSecuritySignalMapper
{
    private static readonly IReadOnlyDictionary<ResourceSecuritySignalType, string[]> SignalTags = new Dictionary<ResourceSecuritySignalType, string[]>
    {
        [ResourceSecuritySignalType.PublicExposure] = ["PubliclyAccessible", "InternetFacing", "PublicAccess", "Exposure", "PublicNetworkAccess", "AllowBlobPublicAccess", "NetworkDefaultAction"],
        [ResourceSecuritySignalType.SensitiveData] = ["Sensitivity", "DataClassification", "ContainsSensitiveData", "DataSensitivity"],
        [ResourceSecuritySignalType.CriticalAsset] = ["CriticalAsset", "BusinessCriticality", "Criticality", "AssetTier"],
        [ResourceSecuritySignalType.WorkloadVulnerability] = ["WorkloadVulnerability", "Vulnerability", "CVE", "CveId", "ImageCve"]
    };

    public static ResourceSecuritySignalMapping MapTags(CloudResource resource, CloudProviderType provider, DateTime? observedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var observedAt = (observedAtUtc ?? DateTime.UtcNow).ToUniversalTime();
        var source = $"{provider} resource tags/labels";
        var signals = new List<ResourceSecuritySignal>();
        foreach (var pair in SignalTags)
        {
            var tag = resource.Tags.FirstOrDefault(item => pair.Value.Contains(item.Key, StringComparer.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(tag.Key) || !IsSignalValue(pair.Key, tag.Value)) continue;
            var severity = GetSeverity(pair.Key, resource.Tags);
            var cvss = TryGetDouble(resource.Tags, "CvssScore", "CVSS", "CVSSScore");
            signals.Add(new ResourceSecuritySignal(resource.Id, pair.Key, tag.Value.Trim(), source, severity,
                IsConfirmed: false,
                IsExploitable: pair.Key == ResourceSecuritySignalType.WorkloadVulnerability && IsTrue(GetTag(resource.Tags, "Exploitable", "KnownExploited")),
                CvssScore: pair.Key == ResourceSecuritySignalType.WorkloadVulnerability ? cvss : null,
                Details: $"Inferred from tag {tag.Key}; validate against the provider's authoritative security service.",
                ObservedAtUtc: observedAt));
        }

        var coverage = Enum.GetValues<ResourceSecuritySignalType>().Where(type => type != ResourceSecuritySignalType.WorkloadIdentityRisk)
            .Select(type => new ResourceSecurityEvidenceCoverage(resource.Id, type, SecurityEvidenceCoverageStatus.Partial, observedAt,
                "Resource tags/labels are a partial heuristic; provider-native security findings are not implied."))
            .ToArray();
        return new ResourceSecuritySignalMapping(signals, coverage);
    }

    public static ResourceSecuritySignalMapping MapWorkloadTrust(
        KubernetesWorkloadTrace trace,
        CloudResource workload,
        DateTime? observedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(workload);
        var observedAt = (observedAtUtc ?? DateTime.UtcNow).ToUniversalTime();
        var status = trace.Trust.Status switch
        {
            WorkloadTrustStatus.Verified or WorkloadTrustStatus.Mismatch => SecurityEvidenceCoverageStatus.Complete,
            WorkloadTrustStatus.Denied or WorkloadTrustStatus.Unavailable => SecurityEvidenceCoverageStatus.Unavailable,
            WorkloadTrustStatus.Unsupported or WorkloadTrustStatus.NotConfigured => SecurityEvidenceCoverageStatus.Unsupported,
            _ => SecurityEvidenceCoverageStatus.Unknown
        };
        var signal = trace.Trust.Status == WorkloadTrustStatus.Mismatch
            ? new[] { new ResourceSecuritySignal(workload.Id, ResourceSecuritySignalType.WorkloadIdentityRisk,
                $"{trace.WorkloadKind}/{trace.WorkloadName} cloud identity trust mismatch", $"Kubernetes workload trust: {trace.Cluster}/{trace.Namespace}",
                RiskSeverity.High, IsConfirmed: true, Details: trace.Trust.Details, ObservedAtUtc: observedAt) }
            : Array.Empty<ResourceSecuritySignal>();
        var coverage = new ResourceSecurityEvidenceCoverage(workload.Id, ResourceSecuritySignalType.WorkloadIdentityRisk,
            status, observedAt, trace.Trust.Details);
        return new ResourceSecuritySignalMapping(signal, new[] { coverage });
    }

    private static bool IsSignalValue(ResourceSecuritySignalType type, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (type == ResourceSecuritySignalType.WorkloadVulnerability)
        {
            var candidate = value.Trim();
            if (new[] { "false", "none", "no", "0", "not-applicable", "no vulnerabilities", "not vulnerable" }.Contains(candidate, StringComparer.OrdinalIgnoreCase)) return false;
            return IsTrue(candidate)
                || candidate.Contains("CVE-", StringComparison.OrdinalIgnoreCase)
                || candidate.Contains("vulnerab", StringComparison.OrdinalIgnoreCase)
                || new[] { "critical", "high", "medium", "low" }.Contains(candidate, StringComparer.OrdinalIgnoreCase);
        }
        if (type == ResourceSecuritySignalType.SensitiveData)
            return IsTrue(value) || new[] { "sensitive", "confidential", "restricted", "high", "critical", "pii", "phi", "pci", "tier0" }.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);
        if (type == ResourceSecuritySignalType.CriticalAsset)
            return IsTrue(value) || new[] { "critical", "tier0", "crown-jewel", "crown jewel" }.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);
        return IsTrue(value) || new[] { "public", "internet", "internet-facing", "enabled", "allow" }.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    private static RiskSeverity GetSeverity(ResourceSecuritySignalType type, IReadOnlyDictionary<string, string> tags)
    {
        var text = GetTag(tags, "VulnerabilitySeverity", "Severity", "RiskSeverity");
        if (!string.IsNullOrWhiteSpace(text) && Enum.TryParse<RiskSeverity>(text, true, out var parsed)) return parsed;
        return type switch
        {
            ResourceSecuritySignalType.PublicExposure => RiskSeverity.High,
            ResourceSecuritySignalType.SensitiveData => RiskSeverity.High,
            ResourceSecuritySignalType.CriticalAsset => RiskSeverity.High,
            ResourceSecuritySignalType.WorkloadVulnerability => RiskSeverity.Medium,
            _ => RiskSeverity.Low
        };
    }

    private static bool IsTrue(string? value)
        => value is not null && new[] { "true", "yes", "1", "enabled", "public", "internet-facing", "allow" }.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    private static string? GetTag(IReadOnlyDictionary<string, string> tags, params string[] names)
        => tags.FirstOrDefault(item => names.Contains(item.Key, StringComparer.OrdinalIgnoreCase)).Value;

    private static double? TryGetDouble(IReadOnlyDictionary<string, string> tags, params string[] names)
        => double.TryParse(GetTag(tags, names), NumberStyles.Float, CultureInfo.InvariantCulture, out var score) && score is >= 0 and <= 10 ? score : null;
}
