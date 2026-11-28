using System.Text.RegularExpressions;

namespace paprika;

public sealed record TrainingSplitExample(
    TrainingExample Example,
    string GroupId,
    bool TeacherReviewed,
    string Split
);

public static partial class TrainingClassifier
{
    private sealed class SplitRow(TrainingExample example, bool teacherReviewed, int index)
    {
        public TrainingExample Example { get; } = example;
        public bool TeacherReviewed { get; } = teacherReviewed;
        public int Index { get; } = index;
        public string TextHash { get; } = Hash(string.Join(' ', Tokens(example.Text)));
        public string GroupId { get; set; } = "";
        public string Split { get; set; } = "train";
    }

    // The input collection, not a label, ID prefix or provenance string, determines authority.
    // Export keeps every row and its disposition; fitting consumes this same partition.
    public static TrainingSplitExample[] SplitExamples(
        IReadOnlyCollection<TrainingExample> independentExamples,
        IReadOnlyCollection<TrainingExample> reviewedTeacherExamples
    )
    {
        var rows = independentExamples
            .Select(e => (Example: e, Teacher: false))
            .Concat(reviewedTeacherExamples.Select(e => (Example: e, Teacher: true)))
            .OrderBy(x => x.Example.Id, StringComparer.Ordinal)
            .ThenBy(x => x.Teacher)
            .Select((x, index) => new SplitRow(x.Example, x.Teacher, index))
            .ToArray();
        var groups = new SourcePartition(rows.Length);
        foreach (var row in rows)
        {
            var e = row.Example;
            groups.Link("id:" + e.Id, row.Index);
            if (!string.IsNullOrWhiteSpace(e.SourcePage))
                groups.Link("source:" + e.SourcePage, row.Index);
            if (row.TeacherReviewed && e.ReviewedSources is { } sources)
                foreach (var page in sources)
                    groups.Link("source:" + page.PacketId + ":" + page.PageId, row.Index);
            // A modern captured row can bridge a source hash to older packet/page-only rows.
            if (
                e.PacketId is not null
                && e.Id.StartsWith("review:" + e.PacketId + ":", StringComparison.Ordinal)
                && e.Id.Split(':') is { Length: 4 } parts
            )
                groups.Link("source:" + e.PacketId + ":" + parts[3], row.Index);
        }
        foreach (var source in rows.GroupBy(r => groups.Root(r.Index)))
            ResolvePage(source);

        bool mixed = reviewedTeacherExamples.Count > 0;
        foreach (var row in rows)
        {
            // Even superseded OCR text can connect a teacher page to a copy in another packet.
            if (mixed || row.Split == "train")
                groups.Link("text:" + row.TextHash, row.Index);
            if (!mixed)
                continue;
            var e = row.Example;
            if (!string.IsNullOrWhiteSpace(e.PacketId))
                groups.Link("packet:" + e.PacketId, row.Index);
            if (row.TeacherReviewed && e.ReviewedSources is { } sources)
                foreach (var source in sources)
                    groups.Link("packet:" + source.PacketId, row.Index);
            // Modern review capture stores SHA256:page. Reprocessing the same file can assign
            // fresh packet/page IDs; keep its pages in one split even when their text differs.
            if (
                e.SourcePage is { Length: > 65 } page
                && page[64] == ':'
                && !page.AsSpan(0, 64).ContainsAnyExcept("0123456789abcdefABCDEF")
                && int.TryParse(page.AsSpan(65), out _)
            )
                groups.Link("file:" + page[..64].ToLowerInvariant(), row.Index);
        }
        foreach (var exact in rows.Where(r => r.Split == "train").GroupBy(r => r.TextHash))
            ResolveIdenticalText(exact);
        if (mixed)
            LinkNearCopies(rows, groups);

        var independentGroups = new List<SplitRow[]>();
        foreach (var group in rows.GroupBy(r => groups.Root(r.Index)))
        {
            var active = group
                .Where(r => r.Split == "train")
                .OrderBy(r => r.TextHash, StringComparer.Ordinal)
                .ToArray();
            string groupId = active.FirstOrDefault()?.TextHash
                ?? Hash("conflict:" + group.First().Example.Id);
            foreach (var row in group)
                row.GroupId = groupId;
            if (
                active.Length > 0
                && !group.Any(r => r.TeacherReviewed || r.Split == "conflict")
                && active.All(r => r.Example.Label == active[0].Example.Label)
            )
                independentGroups.Add(active);
        }
        foreach (var label in independentGroups.GroupBy(g => g[0].Example.Label))
        {
            var ordered = label.OrderBy(g => g[0].GroupId, StringComparer.Ordinal).ToArray();
            // Count independent groups, not rows. Multi-label and teacher-linked groups train.
            foreach (var group in ordered.Take(ordered.Length / 5))
            {
                foreach (var row in group)
                    row.Split = "held-out-related";
                group[0].Split = "held-out";
            }
        }
        return rows.OrderBy(r => r.TextHash, StringComparer.Ordinal)
            .ThenBy(r => r.Example.Id, StringComparer.Ordinal)
            .Select(r => new TrainingSplitExample(r.Example, r.GroupId, r.TeacherReviewed, r.Split))
            .ToArray();
    }

    private static void ResolvePage(IEnumerable<SplitRow> source)
    {
        // Prefer an independent review whenever the same physical page is also teacher-derived.
        var ordered = source
            .OrderBy(r => r.TeacherReviewed)
            .ThenByDescending(r => r.Example.ApprovedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(r => r.Example.Revision ?? 0)
            .ThenBy(r => r.Example.Id, StringComparer.Ordinal)
            .ToArray();
        var first = ordered[0];
        var latest = ordered.TakeWhile(r =>
            r.TeacherReviewed == first.TeacherReviewed
            && (r.Example.ApprovedAt ?? DateTimeOffset.MinValue)
                == (first.Example.ApprovedAt ?? DateTimeOffset.MinValue)
            && (r.Example.Revision ?? 0) == (first.Example.Revision ?? 0)
        );
        foreach (var row in ordered.Skip(1))
            row.Split =
                row.TextHash == first.TextHash && row.Example.Label == first.Example.Label
                    ? "duplicate"
                    : "superseded";
        if (latest.Any(r => r.Example.Label != first.Example.Label))
            foreach (var row in latest)
                row.Split = "conflict";
    }

    private static void ResolveIdenticalText(IEnumerable<SplitRow> exact)
    {
        var ordered = exact
            .OrderBy(r => r.TeacherReviewed)
            .ThenBy(r => r.Example.Id, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Any(r => r.Example.Label != ordered[0].Example.Label))
        {
            foreach (var row in ordered)
                row.Split = "conflict";
            return;
        }
        foreach (var row in ordered.Skip(1))
            row.Split = "duplicate";
    }

    private sealed class SourcePartition(int count)
    {
        private readonly int[] parents = Enumerable.Range(0, count).ToArray();
        private readonly Dictionary<string, int> owners = new(StringComparer.Ordinal);

        public int Root(int index)
        {
            while (parents[index] != index)
            {
                parents[index] = parents[parents[index]];
                index = parents[index];
            }
            return index;
        }

        public void Join(int left, int right) => parents[Root(left)] = Root(right);

        public void Link(string alias, int row)
        {
            if (!owners.TryAdd(alias, row))
                Join(row, owners[alias]);
        }
    }

    [GeneratedRegex(@"[\p{L}]+|[\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex TemplateTokens();

    private sealed record Template(int Row, HashSet<string> Words);

    private static void LinkNearCopies(SplitRow[] rows, SourcePartition groups)
    {
        // Ignore punctuation, word order and variable numeric identifiers in this grouping key.
        // It affects independence only: the classifier still trains on the original page text.
        var templates = new Dictionary<string, Template>(StringComparer.Ordinal);
        var frequency = new Dictionary<string, int>(StringComparer.Ordinal);
        void AddTemplate(int index, HashSet<string> terms)
        {
            string key = Hash(string.Join(' ', terms.Order(StringComparer.Ordinal)));
            if (templates.TryGetValue(key, out var same))
                groups.Join(index, same.Row);
            else
            {
                templates.Add(key, new(index, terms));
                foreach (string term in terms)
                    frequency[term] = frequency.GetValueOrDefault(term) + 1;
            }
        }
        foreach (var row in rows)
        {
            string text = row.Example.Text;
            var tokens = TemplateTokens()
                .Matches(text[..Math.Min(text.Length, 100_000)].ToLowerInvariant())
                .Select(m => char.IsNumber(m.Value, 0) ? "#" : m.Value)
                .ToArray();
            AddTemplate(row.Index, tokens.ToHashSet(StringComparer.Ordinal));
            // On short OCR snippets a single typo can replace a large fraction of the words.
            // Character trigrams also catch minor spelling and word-boundary variants there.
            if (text.Length <= 256 && string.Concat(tokens) is { Length: >= 12 } compact)
                AddTemplate(
                    row.Index,
                    Enumerable
                        .Range(0, compact.Length - 2)
                        .Select(i => ":" + compact.Substring(i, 3))
                        .ToHashSet(StringComparer.Ordinal)
                );
        }

        // Near copies share at least 80% of the larger feature set (and at least three terms).
        // Prefix filtering: in rarest-first order a matching pair must share a term in both
        // prefixes. Verify overlap before joining; sharing boilerplate alone isn't enough.
        var prefixes = new Dictionary<string, List<Template>>(StringComparer.Ordinal);
        foreach (var template in templates.Values)
        {
            int size = template.Words.Count;
            int minimum = MinimumOverlap(size);
            if (size < minimum)
                continue;
            var prefix = template
                .Words.OrderBy(w => frequency[w])
                .ThenBy(w => w, StringComparer.Ordinal)
                .Take(size - minimum + 1)
                .ToArray();
            var seen = new HashSet<int>();
            foreach (string word in prefix)
            {
                if (!prefixes.TryGetValue(word, out var bucket))
                    prefixes.Add(word, bucket = []);
                foreach (var other in bucket)
                {
                    if (!seen.Add(other.Row) || groups.Root(template.Row) == groups.Root(other.Row))
                        continue;
                    int required = MinimumOverlap(Math.Max(size, other.Words.Count));
                    if (
                        Math.Min(size, other.Words.Count) >= required
                        && template.Words.Count(other.Words.Contains) >= required
                    )
                        groups.Join(template.Row, other.Row);
                }
                bucket.Add(template);
            }
        }
    }

    private static int MinimumOverlap(int size) => Math.Max(3, (size * 4 + 4) / 5);
}
