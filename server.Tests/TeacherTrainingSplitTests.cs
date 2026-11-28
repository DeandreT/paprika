using paprika;
using Xunit;

namespace paprika.Tests;

public sealed class TeacherTrainingSplitTests
{
    [Fact]
    public void TeacherOnlyRowsCanTrainButNeverEvaluateEvenAfterReview()
    {
        var teachers = QualityDatabase.Examples(true);
        var split = TrainingClassifier.SplitExamples([], teachers);
        Assert.All(split, r =>
        {
            Assert.True(r.TeacherReviewed);
            Assert.Equal("train", r.Split);
        });
        var model = TrainingClassifier.Fit([], .5, teachers);
        Assert.Equal(teachers.Length, model.TrainingExamples);
        Assert.Equal("invoice", TrainingClassifier.Predict(model, "invoice payment").Label);
        Assert.Equal("not evaluated", model.Evaluation.Status);
        Assert.Equal(0, model.Evaluation.Examples);
        Assert.Null(model.Evaluation.ErrorUpperBound);
        Assert.True(model.Synthetic);
    }

    [Fact]
    public void MixedFitUsesIndependentGroupsAndMatchesPortableSplitInEitherInputOrder()
    {
        // All text here is artificial. Different words avoid treating generated sequence
        // numbers as if they created independent packets.
        string[] documents = ["quartz", "granite", "jasper", "basalt", "marble", "onyx"];
        var independent = documents
            .SelectMany(stone =>
                new[]
                {
                    new TrainingExample("inv-" + stone, $"invoice payment {stone}", "invoice"),
                    new TrainingExample("pod-" + stone, $"delivery signed {stone}", "pod"),
                }
            )
            .ToArray();
        TrainingExample[] teachers =
        [
            new("teacher:one", "Invoice payment quartz!", "invoice"),
            new("teacher:two", "delivery docket teacheronly", "pod"),
        ];
        var split = TrainingClassifier.SplitExamples(independent, teachers);
        var reversed = TrainingClassifier.SplitExamples(
            independent.Reverse().ToArray(),
            teachers.Reverse().ToArray()
        );
        Assert.Equal(split, reversed);
        Assert.Equal(independent.Length + teachers.Length, split.Length);
        Assert.Equal(2, split.Count(r => r.Split == "held-out"));
        Assert.All(split.Where(r => r.Split == "held-out"), r => Assert.False(r.TeacherReviewed));
        Assert.Equal("duplicate", split.Single(r => r.Example.Id == "teacher:one").Split);
        Assert.Equal("train", split.Single(r => r.Example.Id == "inv-quartz").Split);
        var model = TrainingClassifier.Fit(independent, .5, teachers);
        Assert.Equal(1, model.Counts["pod"]["teacheronly"]);
        Assert.Equal(split.Count(r => r.Split == "train"), model.TrainingExamples);
        Assert.Equal(split.Count(r => r.Split == "held-out"), model.Evaluation.Examples);
        Assert.Equal(model.Evaluation.Examples, model.Evaluation.Correct);
    }

    [Fact]
    public void LinksSupersededPageSourceFilePacketAndNearCopiesAcrossDifferentLabelsTransitively()
    {
        string file = new string('a', 64);
        TrainingExample[] independent =
        [
            new(
                "review:original:1:page",
                "superseded OCR",
                "invoice",
                PacketId: "original",
                Revision: 1
            ),
            new(
                "review:original:2:page",
                "current OCR",
                "invoice",
                PacketId: "original",
                Revision: 2,
                SourcePage: file + ":1"
            ),
            new(
                "same-file",
                "refund notice",
                "credit",
                PacketId: "reprocessed",
                SourcePage: file + ":9"
            ),
            new(
                "same-packet",
                "alpha beta gamma delta epsilon REF001",
                "invoice",
                PacketId: "reprocessed"
            ),
            new("near-copy", "ALPHA, beta gamma delta zeta ref-456", "invoice"),
            new("near-copy-of-copy", "alpha beta gamma eta zeta ref-78", "invoice"),
        ];
        TrainingExample[] teachers = [new("teacher:old-ocr-copy", "SUPERSEDED OCR!", "invoice")];
        var split = TrainingClassifier.SplitExamples(independent, teachers);
        Assert.Single(split.Select(r => r.GroupId).Distinct());
        Assert.Equal(
            "superseded",
            split.Single(r => r.Example.Id == "review:original:1:page").Split
        );
        Assert.All(split.Where(r => r.Example.Id != "review:original:1:page"), r =>
            Assert.Equal("train", r.Split)
        );
        Assert.Equal(
            "not evaluated",
            TrainingClassifier.Fit(independent, .5, teachers).Evaluation.Status
        );
    }

    [Fact]
    public void TeacherCannotSupersedeAnIndependentLabelOrLoseItsOriginOnExport()
    {
        var independent = new TrainingExample(
            "manual",
            "dated manual observation",
            "invoice",
            SourcePage: "source"
        );
        var teacher = new TrainingExample(
            "teacher:newer",
            "teacher text",
            "pod",
            SourcePage: "source",
            ApprovedAt: DateTimeOffset.UtcNow
        );
        var rows = TrainingClassifier.SplitExamples([independent], [teacher]);
        Assert.Equal("train", rows.Single(r => !r.TeacherReviewed).Split);
        Assert.Equal("superseded", rows.Single(r => r.TeacherReviewed).Split);
        Assert.Single(rows.Select(r => r.GroupId).Distinct());
    }

    [Fact]
    public void AgreementCarriesEveryContributingPacketEvenWhenLegacyCaptureHasNoSourceHash()
    {
        var teacher = new TrainingExample(
            "teacher:agreement",
            "teacher OCR",
            "invoice",
            PacketId: "a"
        )
        {
            ReviewedSources = [new("a", "first-page"), new("b", "second-page")],
        };
        TrainingExample[] independent =
        [
            new(
                "review:b:3:second-page",
                "later independent OCR",
                "invoice",
                PacketId: "b",
                Revision: 3
            ),
            new(
                "review:b:3:other-page",
                "entirely different delivery",
                "pod",
                PacketId: "b",
                Revision: 3
            ),
        ];
        var split = TrainingClassifier.SplitExamples(independent, [teacher]);
        Assert.Single(split.Select(r => r.GroupId).Distinct());
        Assert.Equal("superseded", split.Single(r => r.TeacherReviewed).Split);
        Assert.All(split.Where(r => !r.TeacherReviewed), r => Assert.Equal("train", r.Split));
    }

    [Fact]
    public void WithheldPacketContributesOneIndependentScoreAndNoTrainingPages()
    {
        string[] minerals =
        [
            "quartz",
            "granite",
            "jasper",
            "basalt",
            "marble",
            "onyx",
            "ruby",
            "agate",
            "topaz",
            "diamond",
        ];
        var pages = minerals
            .SelectMany(mineral =>
            {
                string packet = "packet-" + mineral;
                return new[]
                {
                    new TrainingExample(
                        packet + "-1",
                        $"invoice payment {mineral}",
                        "invoice",
                        PacketId: packet
                    ),
                    new TrainingExample(
                        packet + "-2",
                        $"invoice credit {mineral}",
                        "invoice",
                        PacketId: packet
                    ),
                };
            })
            .ToArray();
        TrainingExample[] teachers = [new("teacher:pod", "delivery receipt", "pod")];
        var split = TrainingClassifier.SplitExamples(pages, teachers);
        Assert.Equal(2, split.Count(r => r.Split == "held-out"));
        Assert.Equal(2, split.Count(r => r.Split == "held-out-related"));
        var withheld = split.Where(r => r.Split == "held-out").Select(r => r.GroupId).ToHashSet();
        Assert.DoesNotContain(split, r => r.Split == "train" && withheld.Contains(r.GroupId));
        var model = TrainingClassifier.Fit(pages, .5, teachers);
        Assert.Equal(17, model.TrainingExamples);
        Assert.Equal(2, model.Evaluation.Examples);
    }

    [Fact]
    public void ShortTeacherOcrWithTypoOrInsertedSpaceStaysWithManualCopy()
    {
        var teacher = new TrainingExample(
            "teacher:ocr",
            "Freight consignment receivable",
            "invoice"
        );
        TrainingExample[] manual =
        [
            new("typo", "Freight consognment receivable", "invoice"),
            new("split-word", "Freight consign ment receivable", "invoice"),
        ];
        var split = TrainingClassifier.SplitExamples(manual, [teacher]);
        Assert.Single(split.Select(row => row.GroupId).Distinct());
    }

    [Fact]
    public void PortableSplitRetainsAllConflictsAndWorksBeforeDatasetIsTrainable()
    {
        Assert.Empty(TrainingClassifier.SplitExamples([], []));
        var first = new TrainingExample("manual", "same text", "invoice");
        Assert.Equal("train", Assert.Single(TrainingClassifier.SplitExamples([first], [])).Split);
        var other = new TrainingExample("teacher:copy", "SAME, text!", "pod");
        var split = TrainingClassifier.SplitExamples([first], [other]);
        Assert.Equal(2, split.Length);
        Assert.All(split, r => Assert.Equal("conflict", r.Split));
        Assert.Single(split.Select(r => r.GroupId).Distinct());
        Assert.Throws<WorkflowException>(() => TrainingClassifier.Fit([first], .5, [other]));
    }
}
