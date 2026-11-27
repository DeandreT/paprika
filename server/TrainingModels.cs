using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace paprika;

public sealed record TrainingModelRef(
    string Id,
    string? Hash = null,
    string? Payload = null,
    bool Approved = false
);

public sealed record TrainingDataset(string Id, string Name);

public sealed record TrainingDatasetRequest(string Name);

public sealed record TrainingDatasetRenameRequest(string Name, string ExpectedName);

public sealed record TrainingExampleSummary(string Id, string Label, bool Synthetic);

public sealed record TrainingPageRef(string PacketId, string PageId);

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
)
{
    // Populated from retained predictions. The portable export includes these pages and
    // their full provenance under teacherReview.predictions.
    [JsonIgnore]
    public TrainingPageRef[]? ReviewedSources { get; init; }
}

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
    string DatasetHash,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        TrainingDataset? Dataset = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? DatasetSnapshotHash = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? DatasetExampleCount = null
);

public sealed record TrainingPrediction(string Label, double Score);

public sealed record TrainRequest(string Name, double Threshold = .95)
    : TrainingDatasetScopedRequest;

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

public sealed record TrainingImportRequest(TrainingExample[] Examples)
    : TrainingDatasetScopedRequest;

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
    ) => Fit(examples, threshold, []);

    public static TrainingArtifact Fit(
        IReadOnlyCollection<TrainingExample> examples,
        double threshold,
        IReadOnlyCollection<TrainingExample> reviewedTeacherExamples
    )
    {
        if (examples.Count + reviewedTeacherExamples.Count < 2)
            throw new WorkflowException(
                400,
                "Training requires at least two labels and two examples."
            );
        if (!double.IsFinite(threshold) || threshold < .5 || threshold > 1)
            throw new WorkflowException(400, "Training threshold must be between 0.5 and 1.");
        var split = SplitExamples(examples, reviewedTeacherExamples);
        if (split.Any(row => row.Split == "conflict"))
            throw new WorkflowException(
                400,
                "The same source page or identical text has conflicting labels. Resolve it before training."
            );
        var train = split
            .Where(row => row.Split == "train")
            .Select(row => row.Example)
            .OrderBy(e => e.Label, StringComparer.Ordinal)
            .ToArray();
        var heldout = split
            .Where(row => row.Split == "held-out")
            .Select(row => row.Example)
            .ToArray();
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
        var evaluation = new TrainingEvaluation("not evaluated", 0, 0, null, null, threshold);
        if (reviewedTeacherExamples.Count > 0)
            evaluation = evaluation with
            {
                Method =
                    "independently labeled held-out groups only; teacher-linked groups train only; Wilson 95% error upper bound",
            };
        var artifact = new TrainingArtifact(
            Algorithm,
            labels,
            counts,
            docs,
            totals,
            Math.Max(1, vocabulary.Count),
            examples.Any(e => e.Synthetic) || reviewedTeacherExamples.Any(e => e.Synthetic),
            train.Length,
            evaluation,
            DatasetHash(split)
        );
        var evaluated = heldout
            .Select(e => (Example: e, Prediction: Predict(artifact, e.Text)))
            .Where(x => x.Prediction.Score >= threshold)
            .ToArray();
        if (evaluated.Length == 0)
            return artifact;
        int correct = evaluated.Count(x => x.Example.Label == x.Prediction.Label);
        return artifact with
        {
            Evaluation = evaluation with
            {
                Status = "held-out",
                Examples = evaluated.Length,
                Correct = correct,
                Accuracy = (double)correct / evaluated.Length,
                ErrorUpperBound = ErrorUpperBound(evaluated.Length - correct, evaluated.Length),
            },
        };
    }

    private static string DatasetHash(TrainingSplitExample[] split)
    {
        // The dataset snapshot captures full provenance; this hash describes unique labeled text.
        var unique = split
            .Where(row => row.Split is "train" or "held-out" or "held-out-related")
            .Select(row => new
            {
                row.Example.Text,
                row.Example.Label,
                row.Example.Synthetic,
            });
        using var hash = SHA256.Create();
        using var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
        JsonSerializer.Serialize(stream, unique, Contract.Json);
        stream.FlushFinalBlock();
        return Convert.ToHexStringLower(hash.Hash!);
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
