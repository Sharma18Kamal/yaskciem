namespace CloudEntitle.Integration.Contracts;

public sealed record SecurityConnectorCapability(
    string Kind,
    string Product,
    string Protocol,
    bool ExportEvents,
    bool ImportAlerts,
    bool ExecuteResponseActions);

public static class SecurityConnectorCatalog
{
    public const string Splunk = "SplunkHec";
    public const string QRadar = "QRadarTls";
    public const string Sentinel = "SentinelLogs";

    public static IReadOnlyList<string> OutboundKinds { get; } = Array.AsReadOnly(new[]
    {
        "SignedWebhook", "Jira", Splunk, QRadar, Sentinel
    });

    public static IReadOnlyList<SecurityConnectorCapability> Capabilities { get; } = Array.AsReadOnly(new[]
    {
        new SecurityConnectorCapability(Splunk, "Splunk", "HTTPS HEC event", true, false, false),
        new SecurityConnectorCapability(QRadar, "IBM QRadar", "TLS syslog / LEEF 2.0", true, false, false),
        new SecurityConnectorCapability(Sentinel, "Microsoft Sentinel", "Azure Monitor Logs Ingestion API", true, false, false),
        new SecurityConnectorCapability("SignedWebhook", "Client-managed SIEM/SOAR bridge", "HTTPS HMAC webhook", true, false, false),
        new SecurityConnectorCapability("Jira", "Jira Software Cloud", "HTTPS REST v3 issue creation", true, false, false),
        new SecurityConnectorCapability("InboundClientBridge", "Splunk / QRadar / Microsoft Sentinel / client bridge",
            "Authenticated normalized HTTPS batch push (client-owned product polling)", false, true, false),
        new SecurityConnectorCapability("CarbonBlackCloud", "Carbon Black Cloud (deferred)", "Not configured", false, false, false)
    });
}
