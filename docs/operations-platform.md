# Shared review operations and retention

`/api/operations-platform` is separate from the existing `/api/operations` summary. The default remains local, loopback-only operation without signing in. Shared mode adds configured identity, workflow permissions, persistent reviewer tasks and explicit retention actions. The standalone `OperationsPanelComponent` (`app-operations-panel`) provides sign-in, queue/claim controls, SLA metrics, task history and retention preview/execution. Its inputs are `packetId: string`, `revision: number`; its `refreshed` event requests a parent refresh. It can render before a packet is selected.

## Registration and hosting

```csharp
builder.Services.AddOperationsPlatform(builder.Configuration);
// If using an ASP.NET identity scheme, register its AddAuthentication/AddAuthorization
// and place app.UseAuthentication() before the next middleware.
app.UseOperationsPlatform();
api.MapOperationsPlatform(); // api is the /api RouteGroupBuilder
```

Place the operations middleware after routing and the application's host/origin/request-header safety middleware, before API handlers. Program wires these hooks. All writes require `X-paprika: 1` (the legacy marker remains accepted; see [rename notes](renaming.md)).

Shared configuration uses the standard ASP.NET configuration hierarchy. A non-secret example:

```json
{
  "paprika": {
    "Operations": {
      "Mode": "shared",
      "UseAspNetIdentity": true,
      "Users": [
        { "Name": "domain\\reviewer", "Roles": ["reviewer"], "Workflows": ["general-logistics"] },
        { "Name": "domain\\auditor", "Roles": ["viewer"], "Workflows": ["general-logistics"] },
        { "Name": "domain\\operator", "Roles": ["administrator"], "Workflows": [] }
      ],
      "ReviewSlaHours": 24,
      "Retention": { "MinimumAgeDays": 90, "ExecutionEnabled": false, "PermanentDeletionEnabled": false }
    },
    "Hosting": {
      "Url": "https://paprika.example.test:5080",
      "AllowedHosts": ["paprika.example.test"],
      "AllowedOrigins": ["https://paprika.example.test:5080"]
    }
  }
}
```

The shared hosting policy permits an explicitly configured listen URL and exact allowed hosts/origins. It does not enable wildcard hosts or origins. Local mode retains its original loopback and localhost origin defaults. Configure HTTPS and the deployment's authentication scheme/certificate or TLS reverse proxy before exposing shared mode. Forwarded headers and any organization identity challenge/sign-out integration belong to the trusted hosting configuration; caller-supplied identity headers are not accepted as authentication.

Alternatively, configure a per-user `paprika:Operations:Users:<index>:Token` through the approved secret/environment provider. Tokens must be at least 32 characters. Do not put reusable tokens in source, JSON examples, URLs, browser storage or logs. `UseAspNetIdentity` can be false for this mode. There is no API to read or update credentials. A matching authenticated ASP.NET identity or a configured bearer credential is required; an unknown name, missing configuration or caller-supplied roles cannot grant access. Duplicate allowlisted names fail closed.

The panel exchanges a bearer credential using `POST /session`. The server returns only the resolved identity and creates an eight-hour protected, HTTP-only, Strict SameSite cookie, secure on HTTPS. The panel immediately clears the token input; it does not retain tokens in local/session storage. The current allowlist is consulted on every request, including cookie sessions. `DELETE /session` clears the browser cookie; ASP.NET SSO sign-out still belongs to the configured identity provider. Removing an account revokes its access. Browser sign-out is not global revocation of an independently copied session ticket.

## Access rules

Roles and workflow IDs are case-sensitive server-side allowlists. `administrator` has global access, `reviewer` can read and change permitted workflows, and `viewer` can read permitted workflows. Workflow permission is resolved from the saved packet, never from a caller's asserted workflow. A role with no permitted workflows sees no packets; a workflow list with no recognized role grants no access.

All `/api` requests are authenticated in shared mode, except the non-sensitive `GET /operations-platform/session` sign-in status. Authenticated `/api/config` remains available. `/api/packets` is filtered; packet details, originals, history, document downloads, exports and packet writes enforce saved workflow permission and archive visibility. Missing/foreign/archived packet targets return 404. The nonadmin workflow catalog exposes only published allowed workflows and their referenced releases/type versions and allowed statistics; global provider/model settings are removed. Its default is an accessible workflow, or the sentinel `{id:"",version:0}` if none is accessible.

Global upload/demo, configuration/catalog changes, training/reference datasets, messaging, delivery-global APIs and future API groups are administrator-only unless explicitly marked with a scoped contract. This deny-by-default rule prevents older endpoints from becoming authentication bypasses. A reviewer cannot use an arbitrary new module API merely because it lives beneath `/api`.

Packet-scoped module handlers opt in with:

```csharp
handler.WithOperationsPacketAccess("packetId", requireClaim: true);
```

The parameter resolves from route/query; missing, multiple or conflicting targets are rejected. Reads require `CanRead`, writes require `CanReview`, and archived packets are rejected in both local and shared modes. With `requireClaim: true`, writes also call the mutation precheck. This middleware precheck does not replace a transaction check. For filtered collections and opaque task IDs, `.WithOperationsScopedAccess()` delegates permission enforcement to the handler: it must use the stored resource, filter lists, check reviewer role for writes and reject archived packets. Do not apply it to unfiltered global configuration CRUD.

`OperationsAccess.Identity(HttpContext)` returns `OperationsIdentity(Name, Roles, Workflows)` with `Administrator`, `CanRead(workflow)` and `CanReview(workflow)`. `OperationsAccess.Actor(HttpContext)` returns the configured actor name. These are available after middleware execution; `OperationsAccess.Resolve(context)` is the independent DI resolver.

Inside the same write transaction as the operation:

```csharp
operations.ValidateReview(connection, transaction, packet, actor);   // string? error
operations.ValidateMutation(connection, transaction, packet, actor); // string? error
operations.RecordReview(connection, transaction, packetId, originalRevision, actor, outcome);
```

`ValidateReview` requires ownership of an unexpired current-revision claim in shared mode, including approved-packet corrections. `ValidateMutation` requires the same for review-state packets, while permitting authorized recovery of failed/processing/queued/approved packets unless another actor owns an active current-revision claim. Core review uses the strict helper; recovery/QA/extraction/case mutations use the mutation helper. Both preserve local behavior. Return a conflict on a non-null error. `RecordReview` completes the original task and records the actual saved/approved outcome atomically with the packet review. Store's core review path calls these hooks.

## Queue and typed API

SQLite in the existing jobs database persists tasks, claims, optimistic revisions, outcome/history and tombstones. Immediate write transactions serialize competing claims across processes. Lease expiration clears ownership and increments the task revision, so an expired owner cannot renew or release using stale state. One task exists per packet review revision. Reading the queue synchronizes new review work, expires leases and supersedes obsolete revisions; it does not start external work. Ordering is open before closed, priority descending, due date ascending, then creation/ID. Metrics are workflow-filtered. A completed queue outcome alone is not document approval; actual packet approval remains the core review API.

Workflow capabilities may contain `OperationsPolicy(ReviewSlaHours=24, DefaultPriority=0, LeaseSeconds=900)`. Validation accepts SLA 1–8760 hours, priority 0–100 and leases 30–3600 seconds. SLA starts at packet creation for automatically generated tasks; explicit correction requests start their due date when opened. Unconfigured workflows use the server SLA fallback. Task claim/renew requests can override lease seconds within the bounds.

Base path: `/api/operations-platform`. Dates are ISO-8601 strings; JSON property names are camelCase. Conflict/validation errors use `{error:string}` and HTTP 409/400, authentication 401, role failure 403, hidden/missing targets 404.

```typescript
interface Identity { name: string; roles: string[]; workflows: string[]; administrator: boolean }
interface Session { mode: 'local' | 'shared'; authenticated: boolean; identity: Identity | null }
interface TaskEvent { at: string; actor: string; action: string; revision: number; outcome: string | null }
interface ReviewTask {
  id: string; packetId: string; workflowId: string; packetRevision: number; revision: number;
  state: 'available' | 'claimed' | 'completed' | 'superseded'; priority: number;
  createdAt: string; dueAt: string; claimedBy: string | null; leaseUntil: string | null;
  outcome: string | null; history: TaskEvent[]; leaseSeconds: number;
}
interface TaskChange { revision: number; leaseSeconds?: number; outcome?: string }
interface Candidate { packetId: string; revision: number; eligible: boolean; reasons: string[] }
interface Tombstone { packetId: string; packetRevision: number; workflowId: string; at: string; actor: string; action: string }
```

| Method/path | Request | Response |
| --- | --- | --- |
| GET `/session` | none; anonymous allowed | `Session` |
| POST `/session` | bearer authentication, empty body | `Session` plus protected cookie |
| DELETE `/session` | authenticated | 204, cookie cleared |
| GET `/tasks?packetId=...` | optional packet filter | `ReviewTask[]`, permission-filtered |
| POST `/tasks/open` | `{packetId,revision}` | `ReviewTask`; explicitly opens/reopens review/correction work |
| POST `/tasks/{id}/claim` | `TaskChange` | `ReviewTask`; available task only |
| POST `/tasks/{id}/renew` | `TaskChange` | `ReviewTask`; active owner only |
| POST `/tasks/{id}/release` | `TaskChange` | `ReviewTask`; active owner only |
| POST `/tasks/{id}/complete` | `TaskChange` with 1–1000-character outcome | `ReviewTask`; active owner only; queue disposition, not approval |
| PUT `/tasks/{id}/schedule` | `{revision,priority,dueAt}` | `ReviewTask`; administrator only |
| GET `/stats` | none | `{total,available,claimed,completed,overdue}` |
| GET `/retention` | administrator | `{policy:{minimumAgeDays,executionEnabled},executionMode,candidates:Candidate[]}` |
| POST `/retention/execute` | `{packetId,revision,confirmArchive:true}` | `Tombstone`; administrator and execution opt-in required |
| GET `/retention/tombstones` | administrator | latest `Tombstone[]` |
| GET `/retention/audit` | administrator | append-only retention actions as `Tombstone[]` |
| GET `/retention/{id}/purge-plan` | administrator | `{packetId,eligible,blockers:string[]}` |
| POST `/retention/purge` | `{packetId,revision,confirmPermanentDeletion:true}` | `Tombstone`; separate permanent-deletion opt-in required |

## Retention safety and limitations

There is no unattended deletion job. Both execution settings default false; preview remains available. Archive rechecks packet revision, terminal state, minimum age since creation and recorded activity, open tasks, durable packet work, in-progress deliveries, descendants and registered participant blockers inside a write transaction. Archiving records an audit/tombstone and hides the packet from packet/list/module-scoped APIs, while retaining originals/history. It is not privacy erasure; operational evidence or snapshots in global module views may still exist.

Physical deletion is real but deliberately narrow. It requires an already archived packet, a second explicit confirmation, both execution settings, current revision and a fresh dependency check. Saved review/document history, any delivery snapshot/artifact, reprocess descendants, open work, filesystem links or any unknown SQLite table block it. Registered module participants check their own graph dependencies. Eligible purge deletes only that archived packet's source directory plus packet, artifact and intake-key database records, calls registered module purges, and keeps the tombstone, retention audit and task outcome/history. It never selects originals from an active packet or broad filesystem paths.

The database and filesystem are not a distributed transaction. A durable `permanent deletion requested` audit is committed before deletion; dependencies are checked again in a write transaction immediately before file removal. If interrupted or filesystem deletion fails, the archived packet metadata/request audit remains and an administrator can retry after investigating. A partially removed original cannot be restored by rolling back SQLite. Backups, external exports, delivery snapshots, provider copies, filesystem recovery and retained audit/task text are not erased by this operation. Do not describe it as comprehensive privacy purge.

Module graph extension:

```csharp
public interface IOperationsRetentionParticipant {
    string Name { get; }
    string[] Tables { get; }
    string[] Blockers(SqliteConnection connection, SqliteTransaction transaction, Packet packet);
    void Purge(SqliteConnection connection, SqliteTransaction transaction, Packet packet);
}
```

Register participants as `IOperationsRetentionParticipant` singletons. Table ownership must be exhaustive. `Blockers` runs for archive and physical purge under the caller's write transaction; it must protect active/in-progress, case, document-history, training-evidence and other dependencies. `Purge` performs only module-owned database changes in that same transaction, must not commit it, and must not delete unrelated files or external resources. Unknown table ownership blocks physical purge rather than guessing safety.

## Validation

`OperationsPlatformTests` and `OperationsApiTests` cover configured/ASP.NET auth boundaries, protected cookie sign-in/sign-out, filtered packet/catalog lists, legacy source/history/export boundaries, concurrent claim races, stale revisions/lease expiry, priority/SLA ordering, review completion/reopening, transactional recovery guards and unsafe retention candidates. Tests use synthetic fixtures and a transport that forbids external requests. Build artifacts and test data are isolated beneath `%LOCALAPPDATA%/Temp/opencode/operations-tests`.

`tests/platform-operations.spec.ts` covers the local team panel queue, claim/renew/release, history, retention preview and phone-width layout. Shared browser sign-in behavior is covered through the isolated API factory rather than requiring reusable credentials in browser test configuration.
