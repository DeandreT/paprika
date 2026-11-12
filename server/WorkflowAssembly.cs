using System.Text.RegularExpressions;

namespace PaperworkClassifier;

// Automatic grouping is always source-local. Business references are independently reviewed.
public static class WorkflowAssembly
{
    public const int MaxRuleText = 100_000;
    public static readonly TimeSpan RuleTimeout = TimeSpan.FromMilliseconds(100);

    public static Regex Rule(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RuleTimeout);

    public static List<Document> Assemble(Packet packet)
    {
        var workflow = packet.EffectiveWorkflow;
        var result = new List<Document>();
        Page? previous = null;
        SplitPolicy? previousPolicy = null;
        string? identifier = null;
        bool closed = false;
        bool hasFirst = false;
        Document? current = null;

        void Finish()
        {
            if (current is null || previousPolicy is null)
                return;
            if (previousPolicy.Mode == "fixed" && current.PageIds.Count != previousPolicy.PageCount)
                Reason(current, "Incomplete fixed-page group; verify the boundary.");
            if (previousPolicy.Mode == "last" && !closed)
                Reason(current, "Missing last-page marker; verify the boundary.");
            if (previousPolicy.Mode == "first" && !hasFirst)
                Reason(current, "Missing first-page marker; verify the boundary.");
        }

        foreach (var page in packet.Pages)
        {
            var type = workflow.Types.SingleOrDefault(t => t.Id == page.PredictedType);
            var policy = type?.Split ?? workflow.DefaultSplit;
            if (policy.Mode == "inherit")
                policy = workflow.DefaultSplit;
            bool sourceStart = previous is null || previous.SourceId != page.SourceId;
            bool policyChange = previousPolicy is not null && previousPolicy != policy;
            bool typeChange = previous is not null && previous.PredictedType != page.PredictedType;
            var reasons = new List<string>();
            bool marker = false;
            string? value = null;
            bool ambiguous = false;
            if (policy.Mode is "first" or "last" or "identifier")
            {
                if (page.Text.Length > MaxRuleText)
                    reasons.Add("Split rule text limit reached; verify the boundary.");
                try
                {
                    var matches = Rule(policy.Pattern)
                        .Matches(page.Text[..Math.Min(page.Text.Length, MaxRuleText)]);
                    if (policy.Mode == "identifier")
                    {
                        var values = matches
                            .SelectMany(m =>
                                m.Groups.Count > 1 && m.Groups[1].Captures.Count > 0
                                    ? m.Groups[1].Captures.Select(c => c.Value.Trim())
                                    : [""]
                            )
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                        ambiguous = values.Length > 1 || values.Any(string.IsNullOrWhiteSpace);
                        value = !ambiguous && values.Length == 1 ? values[0] : null;
                        if (value is null)
                            reasons.Add(
                                ambiguous
                                    ? "Ambiguous captured identifier; verify the boundary."
                                    : "Missing captured identifier; verify the boundary."
                            );
                    }
                    else
                    {
                        marker = matches.Count > 0;
                        ambiguous = matches.Count > 1;
                        if (ambiguous)
                            reasons.Add("Conflicting page markers; verify the boundary.");
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    ambiguous = true;
                    reasons.Add("Split rule timed out; verify the boundary.");
                }
            }
            bool start = sourceStart || policyChange || (typeChange && policy.Mode != "classifier");
            start |= policy.Mode switch
            {
                "classifier" => page.StartProbability >= .5,
                "separate" or "manual" => true,
                "consecutive" => false,
                "fixed" => current?.PageIds.Count >= policy.PageCount,
                "first" => marker || ambiguous,
                "last" => closed || ambiguous,
                "identifier" => ambiguous
                    || (
                        value is not null
                            ? !string.Equals(value, identifier, StringComparison.OrdinalIgnoreCase)
                            : page.StartProbability >= .5
                    ),
                _ => true,
            };
            if (start || current is null)
            {
                Finish();
                current = new Document
                {
                    Type = type?.Id ?? "unknown",
                    ReviewReasons = ["Manual approval required."],
                };
                result.Add(current);
                identifier = null;
                hasFirst = false;
            }
            if (policy.Mode == "manual")
                reasons.Add("Manual document-boundary review required.");
            if (type is null || page.PredictedType == "unknown")
                reasons.Add("Document type is unknown.");
            if (typeChange && !start)
                reasons.Add("Page predictions differ within this document.");
            current.PageIds.Add(page.Id);
            foreach (var reason in reasons)
            {
                Reason(current, reason);
                if (!page.ReviewReasons.Contains(reason))
                    page.ReviewReasons.Add(reason);
            }
            identifier = value ?? identifier;
            hasFirst |= marker && policy.Mode == "first";
            closed = marker && policy.Mode == "last";
            previous = page;
            previousPolicy = policy;
        }
        Finish();
        return result;
    }

    public static void RequiredIdentifiers(Packet packet)
    {
        foreach (var doc in packet.Documents)
        foreach (var kind in packet.EffectiveWorkflow.RequiredIdentifierKinds)
        {
            if (
                !packet.IdentifierCandidates.Any(c =>
                    c.Kind == kind && c.PageIds.Any(doc.PageIds.Contains)
                )
            )
                Reason(
                    doc,
                    $"Required {kind} identifier is missing; review the business reference."
                );
        }
    }

    private static void Reason(Document document, string reason)
    {
        if (!document.ReviewReasons.Contains(reason))
            document.ReviewReasons.Add(reason);
    }
}
