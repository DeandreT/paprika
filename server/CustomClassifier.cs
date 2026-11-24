using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace paprika;

public sealed partial class Providers
{
    private const int ClassifierResponseLimit = 64 * 1024;

    private async Task<(
        string Choice,
        double Confidence,
        double Start,
        string? Model
    )> CustomClassify(
        Packet packet,
        Page page,
        Page? previous,
        WorkflowSnapshot workflow,
        Dictionary<string, string> criteria,
        CancellationToken ct
    )
    {
        var endpoint = settings.CustomClassifierEndpoint;
        var model = settings.CustomClassifierModel;
        var key = settings.CustomClassifierKey;
        // Send to the same local variables that were fingerprinted, even with
        // reloadable configuration. Never read a URL or key from the workflow.
        var configured = Settings.CustomClassifierBinding(endpoint, model);
        if (
            configured is null
            || workflow.ClassifierBindingHash != configured
            || workflow.Model != model
        )
            throw new WorkflowException(
                400,
                "The approved self-hosted classifier binding changed. Publish and select a new workflow version."
            );
        if (string.IsNullOrWhiteSpace(key))
            throw new WorkflowException(400, "The self-hosted classifier requires an API key.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        using var response = await Send(
            packet,
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                request.Content = JsonContent.Create(
                    new
                    {
                        schemaVersion = Settings.CustomClassifierProtocol,
                        model,
                        instructions = workflow.Instructions,
                        types = criteria
                            .Select(pair => new { id = pair.Key, description = pair.Value })
                            .ToArray(),
                        state = new
                        {
                            currentPage = page.Text,
                            previousPage = previous?.SourceId == page.SourceId ? previous.Text : "",
                        },
                    }
                );
                return request;
            },
            deadline.Token,
            HttpCompletionOption.ResponseHeadersRead
        );
        if (response.Content.Headers.ContentLength > ClassifierResponseLimit)
            throw new InvalidOperationException("Self-hosted classifier response exceeded 64 KiB.");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        var buffer = new byte[ClassifierResponseLimit + 1];
        var length = await stream.ReadAtLeastAsync(buffer, buffer.Length, false, deadline.Token);
        if (length > ClassifierResponseLimit)
            throw new InvalidOperationException("Self-hosted classifier response exceeded 64 KiB.");

        try
        {
            using var json = JsonDocument.Parse(buffer.AsMemory(0, length));
            var result = json.RootElement;
            if (
                result.GetProperty("schemaVersion").GetInt32() != Settings.CustomClassifierProtocol
                || result.GetProperty("model").GetString() != model
                || result.GetProperty("documentType").GetString() is not { } choice
                || !criteria.ContainsKey(choice)
            )
                throw new InvalidOperationException();
            var confidence = Probability(result.GetProperty("confidence"));
            var start = Probability(result.GetProperty("startProbability"));
            // Retain only the validated result, not unknown fields or the response body.
            store.Artifact(
                packet.Id,
                "classification-" + page.Id,
                JsonSerializer.Serialize(
                    new
                    {
                        provider = "custom",
                        api = "paprika-classifier-v1",
                        requestedModel = model,
                        workflowHash = workflow.Hash,
                        response = new
                        {
                            model,
                            documentType = choice,
                            confidence,
                            startProbability = start,
                        },
                    },
                    Contract.Json
                )
            );
            return (choice, confidence, start, model);
        }
        catch (Exception ex)
            when (ex
                    is JsonException
                        or InvalidOperationException
                        or KeyNotFoundException
                        or FormatException
            )
        {
            throw new InvalidOperationException(
                "Self-hosted classifier returned an invalid v1 result."
            );
        }
    }
}
