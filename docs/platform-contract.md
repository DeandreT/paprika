# Platform expansion integration contract

`server/PlatformCapabilities.cs` defines the optional capabilities a workflow can enable. The API and review client use the same frozen workflow configuration for a packet.

## Immutable configuration

`WorkflowConfig.Capabilities` and `WorkflowSnapshot.Capabilities` are optional `WorkflowCapabilities` records with null omitted during serialization. Its nullable properties are Quality, Training, Reference, Operations, Extraction, Recognition, Preprocessing, Output, Flow, Case and ReviewCollection. ReviewCollection identifies the dataset for human-approved page labels, independently of the Training artifact. Policies belong to the corresponding module. Missing capabilities on existing workflows retain previous behavior and hash serialization.

Validation rejects unknown/unsupported settings before catalog save/publication and intake. Exact embedded definitions, version/hash references and self-contained trained parameters are pinned before the accepted snapshot hash is calculated. Helpers called inside registry resolution must use the existing SQLite connection/transaction rather than opening another write transaction.

## Persistence and review

Module-owned tables live in `jobs.sqlite` and initialize idempotently. Results bind packet ID, revision and workflow hash. Mutations re-read authoritative state in a transaction and compare expected revision. Packet review now reads inside its write transaction; original predictions and review snapshots remain immutable. Never overwrite a current packet with a stale `Store.Save` copy.

Module review callbacks receive connection, transaction, before/after packet and server-resolved actor. Separate QA does not rewrite completed output. Corrections invalidate stale reference/extraction/approval claims appropriately.

## Authorization

`OperationsAccess.Identity(HttpContext)` returns `OperationsIdentity` with Name, Roles, Workflows, Administrator, CanRead(workflow) and CanReview(workflow). Use the server-resolved Name for audit; request bodies cannot choose an authoritative actor. Shared mode fails closed. Existing source/history/export/download routes and all new module endpoints are covered. Global configuration changes require administrative permissions; packet operations require workflow permission and any active reviewer claim.

## Retention participation

Operations defines `IOperationsRetentionParticipant`:

```csharp
string Name { get; }
string[] Tables { get; }
string[] Blockers(SqliteConnection connection, SqliteTransaction transaction, Packet packet);
void Purge(SqliteConnection connection, SqliteTransaction transaction, Packet packet);
```

Register a singleton implementation for module-owned tables. Return blockers for references that cannot safely be removed; delete removable packet-bound rows in the supplied transaction. Declare every owned table, including durable ledgers and training examples. Purge fails closed for unknown tables, active work, dependencies and retained history. Physical purge is separately configured and disabled by default. Logical archive is not a privacy purge. File derivatives and exported copies need explicit graph accounting.

## UI and contracts

Standalone panels: QualityPanelComponent, ReferencePanelComponent, OperationsPanelComponent, ExtractionPanelComponent, CasePanelComponent and FlowPanelComponent. Selectors app-quality-panel, app-reference-panel, app-operations-panel, app-extraction-panel, app-case-panel, app-flow-panel. Common inputs packetId:string and revision:number, refreshed:void output. Empty packet ID supports catalog/global actions before selection. Main embeds panels and owns shared workflow editor preservation of capabilities.

Every API uses typed request/response metadata and named operations. No secret values in catalog, responses, snapshots or logs. Real transport acknowledgments are distinguished from business acceptance. Synthetic reference/model measurements retain explicit provenance.
