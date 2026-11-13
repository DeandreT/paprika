using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PaperworkClassifier;

public sealed record TrainingModelRef(
    string Id,
    string? Hash = null,
    string? Payload = null,
    bool Approved = false
);

public sealed record TrainingExample(
    string Id,
    string Text,
    string Label,
    bool Synthetic = true,
    string Provenance = "manual",
    string? PacketId = null,
    int? Revision = null,
    string? SourcePage = null,
    DateTimeOffset? ApprovedAt = null
);

public sealed record TrainingEvaluation(
    string Status,
    int Examples,
    int Correct,
    double? Accuracy,
    double? ErrorUpperBound,
    double Threshold,
    string Method = "deterministic held-out split; Wilson 95% error upper bound"
);

public sealed record TrainingArtifact(
    string Algorithm,
    string[] Labels,
    Dictionary<string, Dictionary<string, int>> Counts,
    Dictionary<string, int> Documents,
    Dictionary<string, int> Totals,
    int Vocabulary,
    bool Synthetic,
    int TrainingExamples,
    TrainingEvaluation Evaluation,
    string DatasetHash
);

public sealed record TrainingPrediction(string Label, double Score);

public sealed record TrainRequest(string Name, double Threshold = .95);

public sealed record TrainingModelResponse(
    string Id,
    string Name,
    string Hash,
    bool Approved,
    string? ApprovedBy,
    DateTimeOffset CreatedAt,
    TrainingArtifact Artifact
);

public sealed record TrainingApprovalRequest(string Hash);

public sealed record TrainingImportRequest(TrainingExample[] Examples);

public static partial class TrainingClassifier
{
    public const string Algorithm = "multinomial-naive-bayes-text-v1";

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}_-]{1,49}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    public static string[] Tokens(string text) =>
        TokenPattern()
            .Matches(text[..Math.Min(text.Length, 100_000)].ToLowerInvariant())
            .Select(m => m.Value)
            .Take(10_000)
            .ToArray();

    public static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static TrainingArtifact Fit(
        IReadOnlyCollection<TrainingExample> examples,
        double threshold
    )
    {
        if (examples.Count < 2 || examples.Select(e => e.Label).Distinct().Count() < 2)
            throw new WorkflowException(
                400,
                "Training requires at least two labels and two examples."
            );
        if (!double.IsFinite(threshold) || threshold < .5 || threshold > 1)
            throw new WorkflowException(400, "Training threshold must be between 0.5 and 1.");
        // A correction of the same source page is not an independent new observation.
        // Pick its latest approved label before splitting, even if OCR/text changed.
        var sourceUnique = SourceGroups(examples)
            .Select(group =>
            {
                var latestAt = group.Max(e => e.ApprovedAt ?? DateTimeOffset.MinValue);
                var latestRows = group
                    .Where(e => (e.ApprovedAt ?? DateTimeOffset.MinValue) == latestAt)
                    .ToArray();
                int latest = latestRows.Max(e => e.Revision ?? 0);
                var current = latestRows
                    .Where(e => (e.Revision ?? 0) == latest)
                    .OrderBy(e => e.Id, StringComparer.Ordinal)
                    .ToArray();
                if (current.Select(e => e.Label).Distinct().Count() > 1)
                    throw new WorkflowException(
                        400,
                        "The same source page has conflicting latest labels."
                    );
                return current[0];
            })
            .ToArray();
        // Token normalization also collapses case, punctuation and whitespace-only variants.
        var groups = sourceUnique
            .GroupBy(e => Hash(string.Join(' ', Tokens(e.Text))))
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToArray();
        if (groups.Any(g => g.Select(e => e.Label).Distinct().Count() != 1))
            throw new WorkflowException(
                400,
                "Identical text has conflicting labels. Resolve it before training."
            );
        var unique = groups
            .Select(g => g.OrderBy(e => e.Id, StringComparer.Ordinal).First())
            .ToArray();
        var heldout = new List<TrainingExample>();
        var train = new List<TrainingExample>();
        foreach (
            var label in unique.GroupBy(e => e.Label).OrderBy(g => g.Key, StringComparer.Ordinal)
        )
        {
            var rows = label.ToArray();
            // Small datasets train honestly, but cannot claim calibrated evaluation.
            int testCount = rows.Length >= 5 ? rows.Length / 5 : 0;
            heldout.AddRange(rows.Take(testCount));
            train.AddRange(rows.Skip(testCount));
        }
        var labels = train.Select(e => e.Label).Distinct().Order(StringComparer.Ordinal).ToArray();
        if (labels.Length < 2)
            throw new WorkflowException(
                400,
                "Training requires distinct text for at least two labels."
            );
        var counts = labels.ToDictionary(l => l, _ => new Dictionary<string, int>());
        var docs = labels.ToDictionary(l => l, _ => 0);
        var totals = labels.ToDictionary(l => l, _ => 0);
        var vocabulary = new HashSet<string>();
        foreach (var row in train)
        {
            docs[row.Label]++;
            foreach (var token in Tokens(row.Text))
            {
                counts[row.Label][token] = counts[row.Label].GetValueOrDefault(token) + 1;
                totals[row.Label]++;
                vocabulary.Add(token);
            }
        }
        var artifact = new TrainingArtifact(
            Algorithm,
            labels,
            counts,
            docs,
            totals,
            Math.Max(1, vocabulary.Count),
            examples.Any(e => e.Synthetic),
            train.Count,
            new("not evaluated", 0, 0, null, null, threshold),
            Hash(
                JsonSerializer.Serialize(
                    unique.Select(e => new
                    {
                        e.Text,
                        e.Label,
                        e.Synthetic,
                    }),
                    Contract.Json
                )
            )
        );
        var evaluated = heldout
            .Select(e => (Example: e, Prediction: Predict(artifact, e.Text)))
            .Where(x => x.Prediction.Score >= threshold)
            .ToArray();
        if (heldout.Count == 0 || evaluated.Length == 0)
            return artifact;
        int correct = evaluated.Count(x => x.Example.Label == x.Prediction.Label);
        return artifact with
        {
            Evaluation = new(
                "held-out",
                evaluated.Length,
                correct,
                (double)correct / evaluated.Length,
                ErrorUpperBound(evaluated.Length - correct, evaluated.Length),
                threshold
            ),
        };
    }

    private static IEnumerable<TrainingExample[]> SourceGroups(
        IReadOnlyCollection<TrainingExample> examples
    )
    {
        var rows = examples.OrderBy(e => e.Id, StringComparer.Ordinal).ToArray();
        var parents = Enumerable.Range(0, rows.Length).ToArray();
        var owners = new Dictionary<string, int>(StringComparer.Ordinal);
        int Find(int index)
        {
            while (parents[index] != index)
            {
                parents[index] = parents[parents[index]];
                index = parents[index];
            }
            return index;
        }
        for (int i = 0; i < rows.Length; i++)
        {
            var example = rows[i];
            var aliases = new List<string> { "example:" + example.Id };
            if (!string.IsNullOrWhiteSpace(example.SourcePage))
                aliases.Add("source:" + example.SourcePage);
            // A new source-hash alias also links older captured rows with only packet/page IDs.
            if (
                example.PacketId is not null
                && example.Id.StartsWith(
                    "review:" + example.PacketId + ":",
                    StringComparison.Ordinal
                )
            )
            {
                var parts = example.Id.Split(':');
                if (parts.Length == 4)
                    aliases.Add("page:" + example.PacketId + ":" + parts[3]);
            }
            foreach (string alias in aliases)
            {
                if (owners.TryGetValue(alias, out int owner))
                    parents[Find(i)] = Find(owner);
                else
                    owners[alias] = i;
            }
        }
        return rows.Select((row, index) => (row, root: Find(index)))
            .GroupBy(x => x.root)
            .Select(g => g.Select(x => x.row).ToArray());
    }

    public static TrainingPrediction Predict(TrainingArtifact model, string text)
    {
        var tokens = Tokens(text);
        if (tokens.Length == 0)
            return new("unknown", 0);
        var vocabulary = model.Counts.Values.SelectMany(c => c.Keys).ToHashSet();
        tokens = tokens.Where(vocabulary.Contains).ToArray();
        if (tokens.Length == 0)
            return new("unknown", 0);
        int documents = model.Documents.Values.Sum();
        var scores = model
            .Labels.Select(label =>
                (
                    Label: label,
                    Log: Math.Log((model.Documents[label] + 1d) / (documents + model.Labels.Length))
                        + tokens.Sum(t =>
                            Math.Log(
                                (model.Counts[label].GetValueOrDefault(t) + 1d)
                                    / (model.Totals[label] + model.Vocabulary)
                            )
                        )
                )
            )
            .ToArray();
        var best = scores.MaxBy(s => s.Log);
        double sum = scores.Sum(s => Math.Exp(s.Log - best.Log));
        return new(best.Label, 1 / sum);
    }

    public static double ErrorUpperBound(int errors, int count)
    {
        if (count < 1)
            return 1;
        const double z = 1.959963984540054;
        double p = (double)errors / count,
            z2 = z * z;
        return (
                p
                + z2 / (2 * count)
                + z * Math.Sqrt(p * (1 - p) / count + z2 / (4d * count * count))
            ) / (1 + z2 / count);
    }
}
