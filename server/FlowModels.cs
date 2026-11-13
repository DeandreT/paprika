using System.Text.Json.Serialization;

namespace PaperworkClassifier;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FlowPolicy(FlowDefinition Definition);

[JsonConverter(typeof(JsonStringEnumConverter<FlowBlockKind>))]
public enum FlowBlockKind
{
    Reference,
    Extraction,
    Quality,
    Case,
    Output,
    Transform,
    Validate,
}

[JsonConverter(typeof(JsonStringEnumConverter<FlowConditionKind>))]
public enum FlowConditionKind
{
    DocumentType,
    FieldEquals,
    ReferenceVerified,
}

[JsonConverter(typeof(JsonStringEnumConverter<FlowTransformKind>))]
public enum FlowTransformKind
{
    Trim,
    Uppercase,
    Lowercase,
    RequireValue,
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FlowCondition(
    FlowConditionKind Kind,
    string Key,
    string Value = "",
    bool Negate = false
);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FlowOperation(FlowTransformKind Kind, string Source, string Target);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FlowTemplate(
    string Id,
    FlowBlockKind Kind,
    FlowOperation[]? Operations = null
);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FlowBlock(
    string Id,
    FlowBlockKind Kind,
    string[] DependsOn,
    FlowCondition? When = null,
    string? TemplateId = null,
    FlowOperation[]? Operations = null
);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FlowDefinition(
    string Name,
    FlowBlock[] Blocks,
    FlowTemplate[]? Templates = null
);

public sealed record FlowBlockResult(Dictionary<string, string> Fields, string[] VerifiedReferences)
{
    public static FlowBlockResult Empty => new([], []);
}

public sealed record FlowBlockContext(
    Packet Packet,
    FlowBlock Block,
    string IdempotencyKey,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlySet<string> VerifiedReferences
);

public delegate Task<FlowBlockResult> FlowBlockHandler(
    FlowBlockContext context,
    CancellationToken cancellationToken
);

public sealed record FlowExecution(
    string BlockId,
    string Status,
    int Attempt,
    string IdempotencyKey,
    DateTimeOffset UpdatedAt,
    string? Error,
    FlowBlockResult? Result
);

public sealed record FlowRun(
    string PacketId,
    string SnapshotHash,
    string DefinitionHash,
    FlowDefinition Definition,
    FlowExecution[] Executions,
    int PacketRevision = 1
);

public sealed record FlowRetryRequest(int Revision, string BlockId);

public sealed class FlowAwaitingApprovalException : Exception { }

public sealed record ContinuePlatformFlow(string PacketId, int Revision, string? WorkId = null);

public sealed class ContinuePlatformFlowHandler
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        PlatformPipeline,
        SemaphoreSlim
    > Gates = new();

    public static async Task Handle(
        ContinuePlatformFlow message,
        Store store,
        PlatformPipeline pipeline,
        CancellationToken cancellationToken,
        Wolverine.Envelope? envelope = null
    )
    {
        var gate = Gates.GetValue(pipeline, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (envelope is not null && envelope.Headers.ContainsKey(Messaging.FaultHeader))
            {
                if (!ContinuationWork.CanProcess(store, message, finalizing: true))
                    return;
                Messaging.ThrowIfFinalizingFault(envelope);
            }
            if (!ContinuationWork.CanProcess(store, message))
                return;
            try
            {
                var packet = store.Get(message.PacketId)!;
                await pipeline.AfterApprovalAsync(packet, cancellationToken);
                ContinuationWork.Complete(store, message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw Messaging.SafeFailure(ex);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not TransientWorkException and not PermanentWorkException)
        {
            throw Messaging.SafeFailure(ex);
        }
        finally
        {
            gate.Release();
        }
    }
}

public static class FlowValidator
{
    public static void Validate(FlowDefinition definition)
    {
        if (
            definition is null
            || string.IsNullOrWhiteSpace(definition.Name)
            || definition.Name.Length > 120
            || definition.Blocks is not { Length: > 0 and <= 64 }
        )
            throw new WorkflowException(400, "A named flow with 1–64 blocks is required.");
        var templates = definition.Templates ?? [];
        if (
            templates.Length > 64
            || templates.Any(t => t is null || !Key(t.Id))
            || templates.Select(t => t.Id).Distinct().Count() != templates.Length
        )
            throw new WorkflowException(
                400,
                "Reusable block identities must be unique and bounded."
            );
        foreach (var template in templates)
            Operations(template.Kind, template.Operations);
        var seen = new HashSet<string>();
        foreach (var block in definition.Blocks)
        {
            if (
                block is null
                || !Key(block.Id)
                || !seen.Add(block.Id)
                || !Enum.IsDefined(block.Kind)
                || block.DependsOn is null
                || block.DependsOn.Length > 64
                || block.DependsOn.Distinct().Count() != block.DependsOn.Length
                || block.DependsOn.Any(d => d == block.Id || !seen.Contains(d))
            )
                throw new WorkflowException(
                    400,
                    "Blocks must have unique IDs and depend only on earlier blocks; cycles are not allowed."
                );
            if (block.TemplateId is not null)
            {
                var template = templates.SingleOrDefault(t => t.Id == block.TemplateId);
                if (template is null || template.Kind != block.Kind || block.Operations is not null)
                    throw new WorkflowException(
                        400,
                        "A reusable block must match its template kind and cannot override operations."
                    );
            }
            Operations(
                block.Kind,
                block.Operations
                    ?? templates.SingleOrDefault(t => t.Id == block.TemplateId)?.Operations
            );
            if (
                block.When is { } condition
                && (
                    !Enum.IsDefined(condition.Kind)
                    || !Key(condition.Key)
                    || condition.Value is null
                    || condition.Value.Length > 500
                )
            )
                throw new WorkflowException(400, "A bounded typed branch condition is required.");
        }
    }

    private static bool Key(string? key) =>
        !string.IsNullOrWhiteSpace(key)
        && key.Length <= 100
        && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    private static void Operations(FlowBlockKind kind, FlowOperation[]? operations)
    {
        if (
            !Enum.IsDefined(kind)
            || operations is { Length: > 32 }
            || (
                kind is FlowBlockKind.Transform or FlowBlockKind.Validate
                    ? operations is not { Length: > 0 }
                    : operations is { Length: > 0 }
            )
        )
            throw new WorkflowException(
                400,
                "Only supported typed transformations and validations are accepted."
            );
        if (
            operations?.Any(o =>
                o is null
                || !Enum.IsDefined(o.Kind)
                || !Key(o.Source)
                || !Key(o.Target)
                || (kind == FlowBlockKind.Validate && o.Kind != FlowTransformKind.RequireValue)
            ) == true
        )
            throw new WorkflowException(400, "Invalid safe operation.");
    }
}
