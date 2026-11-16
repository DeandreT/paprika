using Microsoft.OpenApi;

namespace paprika;

// Explicit public contracts keep persistence/provider details out of the API schema.
public sealed record ApiError(string Error);

public sealed record HealthResponse(string Status);

public sealed record DocumentTypeResponse(string Id, string Label);

public sealed record ConfigResponse(
    string Mode,
    bool OcrConfigured,
    bool ClassifierConfigured,
    DocumentTypeResponse[] DocumentTypes,
    int MaxUploadMb
);

public sealed record PacketResponse(
    string Id,
    string Name,
    string Status,
    string Mode,
    DateTimeOffset CreatedAt,
    int Revision,
    string? Error,
    List<Page> Pages,
    List<Document> Documents,
    Processing? Processing,
    List<Audit> Audit,
    string? ParentPacketId,
    int RunNumber,
    List<IdentifierCandidate> IdentifierCandidates,
    WorkflowSnapshot Workflow,
    bool TestRun
)
{
    public static PacketResponse From(Packet p) =>
        new(
            p.Id,
            p.Name,
            p.Status,
            p.Mode,
            p.CreatedAt,
            p.Revision,
            p.Error,
            p.Pages,
            p.Documents,
            p.Processing,
            p.Audit,
            p.ParentPacketId,
            p.RunNumber,
            p.IdentifierCandidates,
            p.EffectiveWorkflow,
            p.TestRun
        );
}

public sealed record UploadRequest(
    IFormFile[] Files,
    string? WorkflowId,
    int? WorkflowVersion,
    bool? TestRun,
    int? WorkflowRevision
);

public sealed record HistoryResponse(
    int Revision,
    string Status,
    DateTimeOffset At,
    List<Document> Documents,
    string Action,
    WorkflowSnapshot? Workflow = null,
    bool TestRun = false
);

public static class ApiDescription
{
    public static IServiceCollection AddApiDescription(this IServiceCollection services)
    {
        services.AddOpenApi("v1", options =>
        {
            options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_0;
            options.AddDocumentTransformer(
                (document, context, cancellationToken) =>
                {
                    document.Info = new OpenApiInfo
                    {
                        Title = "paprika API",
                        Version = "v1",
                        Description =
                            "Document-processing platform. Processing is asynchronous: poll packets until review, approved or failed. Default local simulation uses embedded PDF text and uncalibrated rules with manual approval. Optional immutable capabilities add learned classification, quality sampling, extraction, reference verification, cases and approval-aware flows. Shared mode authenticates configured identities and enforces workflow permissions. X-paprika is an anti-cross-origin request marker, not authentication. Synthetic evaluations are not live-provider accuracy or downstream acceptance.",
                    };
                    return Task.CompletedTask;
                }
            );
            options.AddOperationTransformer(
                (operation, context, cancellationToken) =>
                {
                    operation.OperationId ??=
                        context.Description.HttpMethod
                        + "_"
                        + System
                            .Text.RegularExpressions.Regex.Replace(
                                context.Description.RelativePath ?? "root",
                                "[^A-Za-z0-9]+",
                                "_"
                            )
                            .Trim('_');
                    operation.Summary ??= context.Description.ActionDescriptor.DisplayName
                        ?? $"{context.Description.HttpMethod} {context.Description.RelativePath}";
                    operation.Responses ??= new OpenApiResponses();
                    operation.Responses.TryAdd(
                        "403",
                        new OpenApiResponse
                        {
                            Description =
                                "The request marker, origin, role or workflow permission is not allowed.",
                        }
                    );
                    operation.Responses.TryAdd(
                        "500",
                        new OpenApiResponse
                        {
                            Description =
                                "The operation failed. The response contains a sanitized error.",
                        }
                    );
                    operation.Responses.TryAdd(
                        "401",
                        new OpenApiResponse
                        {
                            Description =
                                "Shared mode requires an authenticated configured identity.",
                        }
                    );
                    var method = context.Description.HttpMethod;
                    if (method is "POST" or "PUT")
                    {
                        operation.Parameters ??= [];
                        operation.Parameters.Add(
                            new OpenApiParameter
                            {
                                Name = "X-paprika",
                                In = ParameterLocation.Header,
                                Required = true,
                                Description =
                                    "Required request marker. Send the literal value 1. This is not an API key.",
                                Schema = new OpenApiSchema
                                {
                                    Type = JsonSchemaType.String,
                                    Enum = [System.Text.Json.Nodes.JsonValue.Create("1")!],
                                },
                            }
                        );
                    }
                    if (operation.OperationId == "UploadPacket")
                    {
                        operation.Parameters ??= [];
                        operation.Parameters.Add(
                            new OpenApiParameter
                            {
                                Name = "Idempotency-Key",
                                In = ParameterLocation.Header,
                                Required = false,
                                Description =
                                    "Optional case-sensitive retry key. Same key and ordered content returns the accepted packet; different content returns 409. Without a key uploads create independent packets.",
                                Schema = new OpenApiSchema
                                {
                                    Type = JsonSchemaType.String,
                                    MinLength = 1,
                                    MaxLength = 128,
                                    Pattern = "^[ -~]+$",
                                },
                            }
                        );
                        operation.RequestBody = new OpenApiRequestBody
                        {
                            Required = true,
                            Description =
                                "Repeat files for 1–50 PDF/PNG/JPEG files, at most 25 MiB and 250 source pages. Omit workflowId/workflowVersion to select the published default. Draft previews require testRun=true and workflowRevision. Simulation uses local text extraction and literal keyword rules; live requires the supported provider profile. Idempotent replay retains its original snapshot before checking the current default or readiness.",
                            Content = new Dictionary<string, OpenApiMediaType>
                            {
                                ["multipart/form-data"] = new()
                                {
                                    Schema = new OpenApiSchema
                                    {
                                        Type = JsonSchemaType.Object,
                                        Required = new HashSet<string> { "files" },
                                        Properties = new Dictionary<string, IOpenApiSchema>
                                        {
                                            ["files"] = new OpenApiSchema
                                            {
                                                Type = JsonSchemaType.Array,
                                                MinItems = 1,
                                                MaxItems = 50,
                                                Items = new OpenApiSchema
                                                {
                                                    Type = JsonSchemaType.String,
                                                    Format = "binary",
                                                },
                                            },
                                            ["workflowId"] = new OpenApiSchema
                                            {
                                                Type = JsonSchemaType.String,
                                            },
                                            ["workflowVersion"] = new OpenApiSchema
                                            {
                                                Type = JsonSchemaType.Integer,
                                                Minimum = "1",
                                            },
                                            ["workflowRevision"] = new OpenApiSchema
                                            {
                                                Type = JsonSchemaType.Integer,
                                                Minimum = "1",
                                            },
                                            ["testRun"] = new OpenApiSchema
                                            {
                                                Type = JsonSchemaType.Boolean,
                                                Description =
                                                    "Exact saved draft revision preview; simulation only.",
                                            },
                                        },
                                    },
                                },
                            },
                        };
                    }
                    if (
                        operation.OperationId is "GetSource" or "ExportPacket" or "DownloadDocument"
                    )
                    {
                        foreach (
                            var response in operation.Responses!.Where(r => r.Key is "200" or "206")
                        )
                        {
                            string[] contentTypes =
                                operation.OperationId == "ExportPacket" ? ["application/zip"]
                                : operation.OperationId == "DownloadDocument" ? ["application/pdf"]
                                : ["application/pdf", "image/png", "image/jpeg"];
                            ((OpenApiResponse)response.Value).Content = contentTypes.ToDictionary(
                                type => type,
                                _ => new OpenApiMediaType
                                {
                                    Schema = new OpenApiSchema
                                    {
                                        Type = JsonSchemaType.String,
                                        Format = "binary",
                                    },
                                }
                            );
                        }
                    }
                    if (operation.OperationId == "GetPacketHistory")
                    {
                        operation.Responses!["200"].Description =
                            "Saved immutable review snapshots and the current revision, ordered by revision ascending.";
                        operation.Responses["404"].Description = "Packet not found.";
                    }
                    if (operation.OperationId == "DownloadDocument")
                    {
                        operation.Responses!["200"].Description =
                            "Approved document PDF in reviewed page order.";
                        operation.Responses["404"].Description = "Packet or document not found.";
                        operation.Responses["409"].Description =
                            "The current packet revision is not approved.";
                    }
                    return Task.CompletedTask;
                }
            );
        });
        return services;
    }
}
