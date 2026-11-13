using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace PaperworkClassifier;

// Optional capabilities keep previously published workflow snapshots byte-compatible.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowCapabilities(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        QualityPolicy? Quality = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        TrainingModelRef? Training = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ReferencePolicy? Reference = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        OperationsPolicy? Operations = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ExtractionPolicy? Extraction = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        RecognitionPolicy? Recognition = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        PreprocessingPolicy? Preprocessing = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        OutputPolicy? Output = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FlowPolicy? Flow = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CasePolicy? Case = null
);

public static class PlatformPolicies
{
    public static WorkflowCapabilities? Freeze(
        SqliteConnection c,
        SqliteTransaction tx,
        WorkflowCapabilities? capabilities
    )
    {
        if (capabilities is null)
            return null;
        static void Check(string? error)
        {
            if (error is not null)
                throw new WorkflowException(400, error);
        }
        Check(QualityPolicy.Validate(capabilities.Quality));
        Check(OperationsPolicy.Validate(capabilities.Operations));
        if (capabilities.Preprocessing is { } preprocessing)
            Check(PreprocessingPlatform.Validate(preprocessing));
        if (capabilities.Output is { } output)
            Check(OutputPlatform.Validate(output));
        if (capabilities.Flow is { } flow)
        {
            FlowValidator.Validate(flow.Definition);
            foreach (var block in flow.Definition.Blocks)
            {
                var configured = block.Kind switch
                {
                    FlowBlockKind.Reference => capabilities.Reference is not null,
                    FlowBlockKind.Extraction => capabilities.Extraction is not null,
                    FlowBlockKind.Case => capabilities.Case is not null,
                    FlowBlockKind.Output => capabilities.Output is not null,
                    _ => true,
                };
                if (!configured)
                    throw new WorkflowException(
                        400,
                        $"The {block.Kind} flow block requires its corresponding capability policy."
                    );
            }
        }
        if (capabilities.Case is { } casePolicy)
            CaseValidator.Validate(casePolicy);
        try
        {
            var frozen = capabilities with
            {
                Training = capabilities.Training is { } training
                    ? TrainingStore.Freeze(c, tx, training)
                    : null,
                Reference = ReferencePolicies.Freeze(c, tx, capabilities.Reference),
                Extraction = capabilities.Extraction is { } extraction
                    ? ExtractionPlatform.Freeze(c, tx, extraction)
                    : null,
                Recognition = capabilities.Recognition is { } recognition
                    ? RecognitionPlatform.Freeze(c, tx, recognition)
                    : null,
            };
            QualityValidation.Validate(frozen.Quality, frozen.Training);
            return frozen;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            throw new WorkflowException(
                400,
                "A referenced platform definition has not been created."
            );
        }
    }

    public static void ValidateTaxonomy(
        WorkflowCapabilities? capabilities,
        IEnumerable<string> types
    )
    {
        if (capabilities is null)
            return;
        var allowed = types.ToHashSet(StringComparer.Ordinal);
        if (
            capabilities.Training is { } training
            && TrainingStore.Resolve(training).Labels.Any(label => !allowed.Contains(label))
        )
            throw new WorkflowException(
                400,
                "Training labels must belong to the selected release."
            );
        if (
            capabilities.Recognition is { } recognition
            && recognition.Templates.Any(t =>
                t.Template is null || !allowed.Contains(t.Template.Label)
            )
        )
            throw new WorkflowException(
                400,
                "Visual template labels must belong to the selected release."
            );
        if (
            capabilities.Extraction?.Definition?.DocumentType is { } type
            && !allowed.Contains(type)
        )
            throw new WorkflowException(
                400,
                "Extraction document type must belong to the selected release."
            );
        if (
            capabilities.Case is { } casePolicy
            && casePolicy.ExpectedDocumentTypes.Any(t => !allowed.Contains(t))
        )
            throw new WorkflowException(
                400,
                "Case checklist types must belong to the selected release."
            );
        if (
            capabilities.Flow?.Definition.Blocks.Any(b =>
                b.When is { Kind: FlowConditionKind.DocumentType } condition
                && !allowed.Contains(condition.Key)
            ) == true
        )
            throw new WorkflowException(
                400,
                "Document-type flow conditions must belong to the selected release."
            );
    }
}
