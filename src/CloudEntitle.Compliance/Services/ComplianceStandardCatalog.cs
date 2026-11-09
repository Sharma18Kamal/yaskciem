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
        ComplianceStandard.NistCsf2_0 => "NIST Cybersecurity Framework (CSF) 2.0 (NIST CSWP 29, February 26, 2024)",
        ComplianceStandard.PciDss4_0_1 => "PCI DSS v4.0.1 (June 2024)",
        ComplianceStandard.CisMicrosoftAzureFoundationsBenchmark2_0_0 => "CIS Microsoft Azure Foundations Benchmark v2.0.0",
        ComplianceStandard.CisAwsFoundationsBenchmark3_0_0 => "CIS AWS Foundations Benchmark v3.0.0",
        ComplianceStandard.GdprAccessEvidence => "GDPR access-security evidence crosswalk (informative; legal review required)",
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
                Map(ComplianceStandard.Soc2TrustServices, "CC6.2", "Supports authorization evidence for access provisioning."),
                Map(ComplianceStandard.NistCsf2_0, "PR.AA-05", "Provides evidence for reviewing access permissions and entitlements; evidence presence alone does not establish that the outcome is met."),
                Map(ComplianceStandard.PciDss4_0_1, "7.2.1", "Provides access-evidence context for an access-control model; this mapping does not establish PCI DSS compliance."),
                Map(ComplianceStandard.GdprAccessEvidence, "Articles 5(1)(f), 25, 32",
                    "Access evidence can support review of confidentiality safeguards and data protection by design; it does not establish GDPR compliance.",
                    "https://eur-lex.europa.eu/eli/reg/2016/679/oj")
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
                Map(ComplianceStandard.Soc2TrustServices, "CC6.1", "Supports least-privilege logical access controls."),
                Map(ComplianceStandard.NistCsf2_0, "PR.AA-05", "Aligns with explicit classification of access decisions for least-privilege review."),
                Map(ComplianceStandard.PciDss4_0_1, "7.2.2", "Supports evaluation of whether assigned access is limited to role-related need; it is not a PCI DSS assessment."),
                Map(ComplianceStandard.GdprAccessEvidence, "Articles 25, 32",
                    "Evaluated access evidence can inform a review of appropriate technical and organizational security measures; it does not establish GDPR compliance.",
                    "https://eur-lex.europa.eu/eli/reg/2016/679/oj")
            ],
            ["LP-002"] =
            [
                Map(ComplianceStandard.CisControlsV8_1, "6.8", "Connects privileged permissions to an explainable access role."),
                Map(ComplianceStandard.Nist80053Rev5, "AC-6", "Links privileged access to an explainable authorization path."),
                Map(ComplianceStandard.Iso27001_2022, "8.2", "Supports control of privileged access rights."),
                Map(ComplianceStandard.Soc2TrustServices, "CC6.3", "Supports access changes and authorization traceability."),
                Map(ComplianceStandard.NistCsf2_0, "PR.AA-05", "Makes privileged authorization paths available for review; a path is not proof of approval or policy enforcement."),
                Map(ComplianceStandard.PciDss4_0_1, "7.2.2", "Provides context for examining privileged permissions against role-related need; it does not demonstrate requirement fulfillment."),
                Map(ComplianceStandard.GdprAccessEvidence, "Articles 25, 32",
                    "Privileged-access evidence can inform review of data-protection-by-design and security measures; it does not establish GDPR compliance.",
                    "https://eur-lex.europa.eu/eli/reg/2016/679/oj"),
                Map(ComplianceStandard.CisMicrosoftAzureFoundationsBenchmark2_0_0, "1.23",
                    "Azure-specific privileged authorization paths can support investigation of custom subscription administrator roles; an explainable path does not establish that no such roles exist.",
                    "https://learn.microsoft.com/en-us/azure/governance/policy/samples/cis-azure-2-0-0"),
                Map(ComplianceStandard.CisAwsFoundationsBenchmark3_0_0, "1.15",
                    "AWS-specific authorization paths can support review of direct user policy attachments versus group or role access; path presence does not establish that policies are attached only to groups or roles.",
                    "https://docs.aws.amazon.com/securityhub/latest/userguide/iam-controls.html#iam-2")
            ],
            ["IG-001"] =
            [
                Map(ComplianceStandard.CisControlsV8_1, "5.1", "Relates lifecycle evidence to account inventory and management."),
                Map(ComplianceStandard.Nist80053Rev5, "AC-2", "Supports account lifecycle review using activity or disclosed unknown state."),
                Map(ComplianceStandard.Iso27001_2022, "5.16", "Supports identity lifecycle governance."),
                Map(ComplianceStandard.Soc2TrustServices, "CC6.2", "Supports identity authorization and lifecycle evidence."),
                Map(ComplianceStandard.NistCsf2_0, "PR.AA-01", "Makes known or unavailable identity-activity evidence explicit; it does not establish that identity credentials are fully managed."),
                Map(ComplianceStandard.CisAwsFoundationsBenchmark3_0_0, "1.12",
                    "AWS-specific identity activity and disclosed unknown states can support unused-credential investigation; this control does not prove complete credential-level usage coverage or enforce the benchmark's 45-day removal threshold.",
                    "https://docs.aws.amazon.com/securityhub/latest/userguide/iam-controls.html#iam-22"),
                Map(ComplianceStandard.GdprAccessEvidence, "Articles 5(1)(f), 32",
                    "Identity lifecycle evidence can inform security reviews; missing activity telemetry is not proof of inactivity or a GDPR violation.",
                    "https://eur-lex.europa.eu/eli/reg/2016/679/oj")
            ],
            ["IG-002"] =
            [
                Map(ComplianceStandard.CisControlsV8_1, "6.8", "Supports review of privileged role assignments and external access."),
                Map(ComplianceStandard.Nist80053Rev5, "AC-6", "Surfaces external privileged identities for least-privilege review."),
                Map(ComplianceStandard.Iso27001_2022, "8.2", "Supports governance of privileged access rights."),
                Map(ComplianceStandard.Soc2TrustServices, "CC6.1", "Supports restriction and review of privileged access."),
                Map(ComplianceStandard.NistCsf2_0, "PR.AA-05", "Surfaces privileged access for policy and least-privilege review; visibility alone is not enforcement."),
                Map(ComplianceStandard.CisMicrosoftAzureFoundationsBenchmark2_0_0, "1.5",
                    "This control surfaces external privileged identities for review in a way that is relevant to the benchmark's guest-user review recommendation; it does not establish that a regular review occurred or remove guest accounts.",
                    "https://learn.microsoft.com/en-us/azure/governance/policy/samples/cis-azure-2-0-0")
            ],
            ["AR-001"] =
            [
                Map(ComplianceStandard.CisControlsV8_1, "6.2", "Relates periodic review evidence to access-right validation."),
                Map(ComplianceStandard.Nist80053Rev5, "AC-2", "Supports periodic review of privileged account evidence."),
                Map(ComplianceStandard.Iso27001_2022, "5.18", "Supports review and adjustment of access rights."),
                Map(ComplianceStandard.Soc2TrustServices, "CC6.2", "Supports evidence for authorized user access."),
                Map(ComplianceStandard.NistCsf2_0, "PR.AA-05", "Supplies privileged-access evidence that may support review; this control does not record reviewer decisions."),
                Map(ComplianceStandard.PciDss4_0_1, "7.2.4", "Supplies privileged-access evidence that may be used in an access review; this control does not perform or attest to the PCI DSS review."),
                Map(ComplianceStandard.GdprAccessEvidence, "Articles 5(1)(f), 25, 32",
                    "Access-review evidence can inform organizational security review; it does not establish that a GDPR-required measure is effective or sufficient.",
                    "https://eur-lex.europa.eu/eli/reg/2016/679/oj"),
                Map(ComplianceStandard.CisMicrosoftAzureFoundationsBenchmark2_0_0, "1.23",
                    "Azure-specific privileged-access evidence can support a custom subscription administrator role review; this control does not establish absence of custom roles or record approval.",
                    "https://learn.microsoft.com/en-us/azure/governance/policy/samples/cis-azure-2-0-0"),
                Map(ComplianceStandard.CisAwsFoundationsBenchmark3_0_0, "1.15",
                    "AWS-specific privileged-access evidence can support review of policy attachment paths; this control does not establish that user policies have been replaced with group or role policies.",
                    "https://docs.aws.amazon.com/securityhub/latest/userguide/iam-controls.html#iam-2")
            ],
            ["AR-002"] =
            [
                Map(ComplianceStandard.CisControlsV8_1, "8.2", "Relates governance records to collected audit-log evidence."),
                Map(ComplianceStandard.Nist80053Rev5, "AU-9", "Supports protection and traceability of audit information."),
                Map(ComplianceStandard.Iso27001_2022, "8.15", "Supports logging evidence for governance activity."),
                Map(ComplianceStandard.Soc2TrustServices, "CC7.2", "Supports monitoring and investigation using audit records."),
                Map(ComplianceStandard.CisMicrosoftAzureFoundationsBenchmark2_0_0, "5.1.2",
                    "Audit records can support an Azure diagnostic-category coverage review; application audit presence does not establish that Azure diagnostic settings capture the required cloud categories or that activity-log alerts exist.",
                    "https://learn.microsoft.com/en-us/azure/governance/policy/samples/cis-azure-2-0-0"),
                Map(ComplianceStandard.GdprAccessEvidence, "Articles 5(2), 30, 32",
                    "Audit evidence can support accountability and security review; CIEM audit events are not a complete GDPR processing record or breach report.",
                    "https://eur-lex.europa.eu/eli/reg/2016/679/oj")
            ]
        };

    private static ComplianceStandardMapping Map(ComplianceStandard standard, string reference, string rationale, string sourceUri = "")
        => new(standard, reference, rationale) { SourceUri = sourceUri };
}
