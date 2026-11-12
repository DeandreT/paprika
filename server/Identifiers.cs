using System.Text.RegularExpressions;

namespace PaperworkClassifier;

// Visible labelled references only; candidates are never business-system validation or association.
public static class Identifiers
{
    private static readonly Regex Reference = new(
        @"\b(?<label>load|shipment|purchase[ \t]+order|order|po|bol|bill[ \t]+of[ \t]+lading|receipt|mc|usdot|dot|trailer|container|chassis)[ \t]*(?:(?:number\b|no\b\.?|id\b|reference\b|ref\b\.?)[ \t]*[:#=\-]?[ \t]*|[:#=\-][ \t]*):?[ \t]*(?<value>[A-Za-z0-9][A-Za-z0-9._/\-]{0,63})(?![A-Za-z0-9._/\-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1)
    );

    public static List<IdentifierCandidate> Extract(IEnumerable<Page> pages)
    {
        var result = new List<IdentifierCandidate>();
        foreach (var page in pages)
        foreach (Match match in Reference.Matches(page.Text))
        {
            var label = match.Groups["label"].Value;
            var kind = label.ToLowerInvariant() switch
            {
                var l when l.StartsWith("bill") => "bol",
                var l when l.StartsWith("purchase") || l == "po" => "order",
                "usdot" => "dot",
                var l => l,
            };
            var value = match.Groups["value"].Value;
            var existing = result.Find(c => c.Kind == kind && c.Value == value);
            if (existing is not null)
            {
                if (!existing.PageIds.Contains(page.Id))
                    existing.PageIds.Add(page.Id);
                continue;
            }
            var evidence = match.Value;
            result.Add(new(kind, value, [page.Id], evidence[..Math.Min(300, evidence.Length)]));
        }
        return result;
    }
}
