using System.Net.Mail;

namespace CloudEntitle.Domain.ValueObjects;

public sealed record CloudResourceIdentifier
{
    public CloudResourceIdentifier(string value)
    {
        Value = value?.Trim() ?? throw new ArgumentNullException(nameof(value));
        if (string.IsNullOrWhiteSpace(Value))
        {
            throw new ArgumentException("A resource identifier cannot be empty.", nameof(value));
        }
    }

    public string Value { get; }
}

public sealed record CloudScope
{
    public CloudScope(string value)
    {
        Value = value?.Trim() ?? throw new ArgumentNullException(nameof(value));
        if (string.IsNullOrWhiteSpace(Value))
        {
            throw new ArgumentException("A cloud scope cannot be empty.", nameof(value));
        }
    }

    public string Value { get; }
}

public sealed record EmailAddress
{
    public EmailAddress(string value)
    {
        Value = value?.Trim() ?? throw new ArgumentNullException(nameof(value));
        try
        {
            _ = new MailAddress(Value);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The email address is invalid.", nameof(value), exception);
        }
    }

    public string Value { get; }
}

public sealed record PermissionIdentifier
{
    public PermissionIdentifier(string value)
    {
        Value = value?.Trim() ?? throw new ArgumentNullException(nameof(value));
        if (string.IsNullOrWhiteSpace(Value))
        {
            throw new ArgumentException("A permission identifier cannot be empty.", nameof(value));
        }
    }

    public string Value { get; }
}

public sealed record ExternalIdentity
{
    public ExternalIdentity(string value, string? organization = null, string? domain = null)
    {
        Value = value?.Trim() ?? throw new ArgumentNullException(nameof(value));
        if (string.IsNullOrWhiteSpace(Value))
        {
            throw new ArgumentException("An external identity cannot be empty.", nameof(value));
        }

        Organization = organization?.Trim();
        Domain = domain?.Trim();
    }

    public string Value { get; }
    public string? Organization { get; }
    public string? Domain { get; }
}

public sealed record RiskEvidence
{
    public RiskEvidence(string summary, IReadOnlyDictionary<string, string>? details = null)
    {
        Summary = summary?.Trim() ?? throw new ArgumentNullException(nameof(summary));
        if (string.IsNullOrWhiteSpace(Summary))
        {
            throw new ArgumentException("Risk evidence requires a summary.", nameof(summary));
        }

        Details = new Dictionary<string, string>(details ?? new Dictionary<string, string>());
    }

    public string Summary { get; }
    public IReadOnlyDictionary<string, string> Details { get; }
}
