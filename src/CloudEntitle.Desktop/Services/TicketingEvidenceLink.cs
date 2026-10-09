namespace CloudEntitle.Desktop.Services;

public static class TicketingEvidenceLink
{
    private const string Scheme = "yask-ciem";

    public static string ForRecommendation(Guid recommendationId)
        => $"{Scheme}://recommendations/{recommendationId:D}";

    public static bool TryGetRecommendationId(string? value, out Guid recommendationId)
    {
        recommendationId = Guid.Empty;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase)
            && uri.Host.Equals("recommendations", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(uri.AbsolutePath.Trim('/'), out recommendationId)
            && uri.Query.Length == 0
            && uri.Fragment.Length == 0;
    }
}
