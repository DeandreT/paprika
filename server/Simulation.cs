using System.Text.RegularExpressions;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace PaperworkClassifier;

public static class Simulation
{
    public const string Model = "local-simulation-1";
    public const string PromptVersion = "local-simulation-rules-1";
    private const string Notice =
        "Heuristic simulation only: no AI/OCR; illustrative rule scores are not calibrated probabilities. Manual review required.";

    public static void Process(Packet packet, Store store, CancellationToken ct = default)
    {
        packet.PromptVersion = packet.EffectiveWorkflow.LegacyRules
            ? PromptVersion
            : "workflow-simulation-1";
        packet.TaxonomyVersion = packet.EffectiveWorkflow.LegacyRules
            ? Contract.TaxonomyVersion
            : $"{packet.EffectiveWorkflow.ReleaseId}@{packet.EffectiveWorkflow.ReleaseVersion}";
        packet.Processing = new("simulating", Model);
        var ordered = new List<Page>();
        foreach (var source in packet.Sources)
        {
            ct.ThrowIfCancellationRequested();
            if (store.Get(packet.Id)?.Status == "cancelled")
                throw new OperationCanceledException();
            var cached = packet.Pages.Where(p => p.SourceId == source.Id).ToList();
            if (
                cached.Any(p => p.PageNumber < 1 || p.PageNumber > source.PageCount)
                || cached.Select(p => p.PageNumber).Distinct().Count() != cached.Count
            )
                throw new InvalidOperationException(
                    "Simulation checkpoint has invalid page membership."
                );
            // Completed predictions are immutable: only missing pages are extracted/classified.
            string[]? texts = null;
            if (cached.Count != source.PageCount)
            {
                texts = Enumerable.Repeat("", source.PageCount).ToArray();
                if (source.Extension == ".pdf")
                {
                    try
                    {
                        using var pdf = UglyToad.PdfPig.PdfDocument.Open(
                            store.SourcePath(packet, source)
                        );
                        if (pdf.NumberOfPages != source.PageCount)
                            throw new InvalidDataException();
                        for (int i = 0; i < texts.Length; i++)
                            texts[i] = ContentOrderTextExtractor.GetText(pdf.GetPage(i + 1));
                    }
                    catch (Exception)
                    {
                        // Never expose parser details or silently drop pages on extraction failure.
                        throw new InvalidOperationException(
                            "Local simulation PDF text extraction failed. No OCR was performed."
                        );
                    }
                }
            }
            for (int number = 1; number <= source.PageCount; number++)
            {
                ct.ThrowIfCancellationRequested();
                var page = cached.SingleOrDefault(p => p.PageNumber == number);
                if (page is null)
                {
                    page = Worker.MakePage(packet, source, number, texts![number - 1]);
                    Predict(page, packet.EffectiveWorkflow);
                    packet.Pages.Add(page);
                    store.Save(packet);
                }
                ordered.Add(page);
            }
        }
        if (
            ordered.Count != packet.Pages.Count
            || ordered.Select(p => p.Id).Distinct().Count() != ordered.Count
        )
            throw new InvalidOperationException(
                "Simulation checkpoint has invalid page membership."
            );
        packet.Pages = ordered;
    }

    public static void Predict(Page page, WorkflowSnapshot workflow)
    {
        var text = page.Text;
        bool Match(string pattern) =>
            Regex.IsMatch(
                text,
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                WorkflowAssembly.RuleTimeout
            );
        var matches = new List<string>();
        if (workflow.LegacyRules)
        {
            if (Match(@"\b(bill\s+of\s+lading|BOL)\b"))
                matches.Add("bol");
            if (Match(@"\b(proof\s+of\s+delivery|POD)\b"))
                matches.Add("pod");
            if (Match(@"\bpacking\s+list\b"))
                matches.Add("packing_list");
            if (Match(@"\binvoice\b"))
                matches.Add("invoice");
            if (Match(@"\breceipt\b"))
                matches.Add("receipt");
        }
        else
        {
            matches.AddRange(
                workflow
                    .Types.Where(t =>
                        t.Id != "unknown"
                        && t.Keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase))
                    )
                    .Select(t => t.Id)
            );
        }
        page.PredictedType = matches.Count == 1 ? matches[0] : "unknown";
        page.Confidence = matches.Count == 1 ? .8 : 0;
        // Boundary evidence is independent of type. First source page always starts a document.
        bool first = Match(@"\bpage\s+1\s*(?:of|/)\s*\d+\b");
        bool continuation =
            Match(@"\bcontinu(?:ation|ed)\b")
            || Match(@"\bpage\s+(?:[2-9]|[1-9]\d+)\s*(?:of|/)\s*\d+\b");
        page.StartProbability = page.PageNumber == 1 || first || !continuation ? 1 : 0;
        page.ReviewReasons = [Notice];
        page.ReviewReasons.Add(
            page.StartProbability == 0
                ? "Explicit continuation/page numbering suggests continuation; verify boundary."
                : "New document by file boundary, first-page marker, or absence of explicit continuation; same type alone does not merge pages."
        );
        if (string.IsNullOrWhiteSpace(text))
            page.ReviewReasons.Add(
                "No extractable text. No OCR in simulation; image/scanned or empty page needs manual review."
            );
        else if (page.PredictedType == "unknown")
            page.ReviewReasons.Add("No unique type rule matched; manual classification required.");
    }
}
