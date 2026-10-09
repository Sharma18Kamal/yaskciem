using CloudEntitle.Compliance.Models;

namespace CloudEntitle.Compliance.Services;

public static class ComplianceStandardCatalog
{
    public static IReadOnlyList<ComplianceStandard> Standards { get; } = Enum.GetValues<ComplianceStandard>();
    public const string MappingCatalogVersion = ComplianceStandardCatalogVersion.Current;

    public static string GetStandardVersion(ComplianceStandard standard) => standard switch
    {
        ComplianceStandard.CisControlsV8_1 => "CIS Controls v8.1",
        ComplianceStandard.Nist80053Rev5 => "NIST SP 800-53 Rev. 5",
        ComplianceStandard.Iso27001_2022 => "ISO/IEC 27001:2022",
        ComplianceStandard.Soc2TrustServices => "SOC 2 Trust Services Criteria (2017)",
        _ => throw new ArgumentOutOfRangeException(nameof(standard), standard, "Unsupported compliance standard.")
    };

    public static IReadOnlyList<ComplianceStandardMapping> GetMappings(string controlId, IEnumerable<ComplianceStandard> selectedStandards)
    {
        var selected = selectedStandards.ToHashSet();
        return Mappings.TryGetValue(controlId, out var mappings)
            ? mappings.Where(mapping => selected.Contains(mapping.Standard)).Select(mapping => mapping with
            {
                SourceControlId = controlId,
                StandardVersion = GetStandardVersion(mapping.Standard),
                MappingCatalogVersion = MappingCatalogVersion
            }).ToArray()
            : Array.Empty<ComplianceStandardMapping>();
    }

    private static readonly IReadOnlyDictionary<string, ComplianceStandardMapping[]> Mappings =
        new Dictionary<string, ComplianceStandardMapping[]>(StringComparer.Ordinal)
        {
            ["CIEM-BASE-001"] =
            [
                Map(ComplianceStandard.CisControlsV8_1, "4.1", "Relates cloud discovery posture to a documented secure-configuration process."),
                Map(ComplianceStandard.Nist80053Rev5, "AC-6", "Restrict discovered cloud access to least privilege."),
                Map(ComplianceStandard.Iso27001_2022, "5.15", "Relates read-only discovery posture to access-control governance."),
                Map(ComplianceStandard.Soc2TrustServices, "CC6.1", "Supports logical access restrictions and least privilege.")
            ],
            ["CIEM-BASE-002"] =
            [
                Map(ComplianceStandard.CisControlsV8_1, "6.8", "Relates access evidence to defined role-based access control."),
                Map(ComplianceStandard.Nist80053Rev5, "AC-2", "Supports account management through attributable access evidence."),
                Map(ComplianceStandard.Iso27001_2022, "5.18", "Supports reviewable access-right evidence."),
                Map(ComplianceStandard.Soc2TrustServices, "CC6.2", "Supports authorization evidence for access provisioning.")
            ],
            ["CIEM-BASE-003"] =
            [
                Map(ComplianceStandard.CisControlsV8_1, "7.5", "Relates finding visibility to documented remediation tracking."),
                Map(ComplianceStandard.Nist80053Rev5, "RA-5", "Supports vulnerability and risk monitoring follow-up."),
                Map(ComplianceStandard.Iso27001_2022, "8.8", "Relates to management of technical vulnerabilities."),
                Map(ComplianceStandard.Soc2TrustServices, "CC7.2", "Supports monitoring and evaluation of security events.")
            ],
            ["LP-001"] =
            [
                Map(ComplianceStandard.CisControlsV8_1, "6.8", "Supports role-based access decisions based on evaluated permissions."),
                Map(ComplianceStandard.Nist80053Rev5, "AC-6", "Requires evaluated access decisions before least-privilege conclusions."),
                Map(ComplianceStandard.Iso27001_2022, "5.15", "Supports access-control decisions grounded in evaluated permissions."),
                Map(ComplianceStandard.Soc2TrustServices, "CC6.1", "Supports least-privilege logical access controls.")
            ],
            ["LP-002"] =
            [
                Map(ComplianceStandard.CisControlsV8_1, "6.8", "Connects privileged permissions to an explainable access role."),
                Map(ComplianceStandard.Nist80053Rev5, "AC-6", "Links privileged access to an explainable authorization path."),
                Map(ComplianceStandard.Iso27001_2022, "8.2", "Supports control of privileged access rights."),
                Map(ComplianceStandard.Soc2TrustServices, "CC6.3", "Supports access changes and authorization traceability.")
            ],
            ["IG-001"] =
            [
                Map(ComplianceStandard.CisControlsV8_1, "5.1", "Relates lifecycle evidence to account inventory and management."),
                Map(ComplianceStandard.Nist80053Rev5, "AC-2", "Supports account lifecycle review using activity or disclosed unknown state."),
                Map(ComplianceStandard.Iso27001_2022, "5.16", "Supports identity lifecycle governance."),
                Map(ComplianceStandard.Soc2TrustServices, "CC6.2", "Supports identity authorization and lifecycle evidence.")
            ],
            ["IG-002"] =
            [
                Map(ComplianceStandard.CisControlsV8_1, "6.8", "Supports review of privileged role assignments and external access."),
                Map(ComplianceStandard.Nist80053Rev5, "AC-6", "Surfaces external privileged identities for least-privilege review."),
                Map(ComplianceStandard.Iso27001_2022, "8.2", "Supports governance of privileged access rights."),
                Map(ComplianceStandard.Soc2TrustServices, "CC6.1", "Supports restriction and review of privileged access.")
            ],
            ["AR-001"] =
            [
                Map(ComplianceStandard.CisControlsV8_1, "6.2", "Relates periodic review evidence to access-right validation."),
                Map(ComplianceStandard.Nist80053Rev5, "AC-2", "Supports periodic review of privileged account evidence."),
                Map(ComplianceStandard.Iso27001_2022, "5.18", "Supports review and adjustment of access rights."),
                Map(ComplianceStandard.Soc2TrustServices, "CC6.2", "Supports evidence for authorized user access.")
            ],
            ["AR-002"] =
            [
                Map(ComplianceStandard.CisControlsV8_1, "8.2", "Relates governance records to collected audit-log evidence."),
                Map(ComplianceStandard.Nist80053Rev5, "AU-9", "Supports protection and traceability of audit information."),
                Map(ComplianceStandard.Iso27001_2022, "8.15", "Supports logging evidence for governance activity."),
                Map(ComplianceStandard.Soc2TrustServices, "CC7.2", "Supports monitoring and investigation using audit records.")
            ]
        };

    private static ComplianceStandardMapping Map(ComplianceStandard standard, string reference, string rationale)
        => new(standard, reference, rationale);
}
