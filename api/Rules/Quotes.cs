namespace Eskrb.Api.Rules;

public static class Quotes
{
    /// <summary>
    /// A flag's quote must be the patient's own words. Use the extractor's quote only if it really occurs in
    /// the confirmed text; otherwise fall back to the whole text (never an invented sentence).
    /// </summary>
    public static string Resolve(string text, string? quote) =>
        !string.IsNullOrWhiteSpace(quote) && text.Contains(quote.Trim(), StringComparison.Ordinal)
            ? quote.Trim() : text;
}
