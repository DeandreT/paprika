# Offline backup and recovery

Use PowerShell 7 on Windows. Stop **every application instance using the data directory** before backup or restore, and keep it stopped until the command completes. These are offline filesystem copies, not SQLite online snapshots. The scripts reject a listener on the application's fixed port 5080 and files held open exclusively, but those checks are only safeguards: they cannot prove that an arbitrary process is not writing the directory. Do not start a server during either operation.

Keep runtime data and backups on local, access-controlled storage outside OneDrive. Backups contain private originals, extracted content, review history and simulated delivery data. The manifest contains lengths and SHA-256 hashes for corruption detection; it is not a signature or protection against someone replacing both the files and manifest. Restore only trusted backups.

## Back up

```powershell
./scripts/backup.ps1 -OutputDirectory "$env:LOCALAPPDATA/Backups/paprika-before-upgrade"
```

Choose a new destination outside the source directory. The complete data tree is copied, including `jobs.sqlite`, any SQLite WAL/SHM files left by the stopped application, source files and `deliveries` (outbox database and ZIP artifacts). Every file receives a length and SHA-256 entry in `manifest.json`. A staging directory is published only after copying completes.

Without `-DataDirectory`, backup uses `paprika__DataDirectory` when set, otherwise the same new/legacy default selection as the application. For a data root selected through another configuration provider, pass its actual path explicitly. See [rename notes](renaming.md#existing-data). The manifest format remains `paperwork-backup-1` so existing backups remain readable.

## Restore to a new directory

```powershell
./scripts/restore.ps1 -BackupDirectory "$env:LOCALAPPDATA/Backups/paprika-before-upgrade" -DataDirectory "$env:LOCALAPPDATA/paprika-recovered"
$env:paprika__DataDirectory = "$env:LOCALAPPDATA/paprika-recovered"
# Start the published application with this environment variable.
```

Existing destinations are never overwritten. Restore verifies every manifest file before copying and verifies the copied bytes again before publishing the new directory. Reparse points, including those in ancestors, are rejected. Manifest paths must be relative canonical segments: traversal, absolute paths, alternate streams, duplicate paths, device names and trailing-dot/space aliases are rejected. Failed operations clean their own staging directory. The trusted local directories must not be changed concurrently during validation/copying.

Current persistence does **not** serialize absolute source or delivery artifact filesystem paths. Original paths are derived from the configured data root, packet ID, source ID and extension; delivery artifacts are derived from the root and delivery ID. Saved review revisions and delivery snapshots use the same path-free source records. Consequently restoring to another root needs no JSON/SQLite rewrite and preserves original bytes and immutable delivery artifacts. These instructions describe the current application schema, not migration of an unknown historical schema.

## Repeatable isolated verification

Publish the current application, stop other port-5080 instances, then run:

```powershell
./scripts/recovery-check.ps1 -PackageDirectory ./artifacts/publish-platform -SeedPackageDirectory ./artifacts/publish-workflows
```

The check creates a unique temporary tree under `%LOCALAPPDATA%/Temp/opencode`, launches only its own published process in Simulation mode with provider keys removed, and creates a synthetic demo packet. It saves a review, approves it, exports it and completes a local simulated delivery. It stops the server before backup/restore, restores into a fresh directory, **deletes the original data tree**, and restarts using the recovered root. It verifies approval/revision, original-source SHA-256 hashes, exact review history, export entry names/provenance, and the exact delivery record and artifact hash. Regenerated export ZIP/PDF bytes are not compared because generated timestamps can differ. It also checks that a modified backup and nine unsafe manifest paths fail without publishing a target. Finally it terminates its own process and removes its temporary data, even after failure. It never stops an existing process and makes no provider or delivery network calls.

The check also starts a new processing run from the restored originals, approves it and completes a new simulated delivery, verifying that recovery supports new work as well as archived reads. The approved parent's history must remain unchanged.

For upgrade compatibility, pass `-SeedPackageDirectory ./artifacts/publish-workflows` along with the new `-PackageDirectory`. The previous release creates the synthetic source data; the new release opens the restored copy and executes the fresh work. The check chooses each package's DLL, configuration prefix and request marker independently, so a legacy seed package still uses its original contract. The original release folder and any regular application data remain untouched.

Run `scripts/durability-check.ps1 -PackageDirectory ./artifacts/publish-platform` against the current paprika package (Python 3 required). It queues a repeated transient delivery failure and force-stops its own process. A read-only SQLite probe then requires a non-handled envelope for that delivery's current generation and no matching dead letter, and records the actual stopped attempt count. This prevents a completed failure racing with shutdown from falsely passing as recovered pending work. The check backs up/restores the stopped data, deletes the old root, and requires additional attempts before exhaustion. It also verifies dead-letter persistence across another restart, operator replay, and unchanged approved history. It never fabricates Wolverine queue rows. Every process and data directory is allocated for that check and cleaned in `finally`.

The full backup must include active Wolverine inbox/outbox, scheduled retry and dead-letter state. Legacy `deliveries/outbox.sqlite` may remain after migration as preserved historical data; the new runtime's consolidated database is authoritative. Do not restore just the legacy outbox into an upgraded directory or run an old binary on upgraded data. For rollback, restore a complete pre-upgrade backup into a fresh directory.

Previous verification on 2026-11-12 against `artifacts/publish-lifecycle`: passed with a nine-file backup, one original synthetic PDF, restored review history/export/delivery, tampering rejection and all nine unsafe path cases. Port 5080 was released and temporary data removed at completion.

Wolverine upgrade verification on 2026-11-12: passed using a standalone copy of `artifacts/publish-wolverine` outside the checkout and `artifacts/publish-lifecycle` as the seed package. All preservation checks and fresh reprocessing/delivery passed. The temporary package/data and owned server were cleaned up.

Durable messaging verification on 2026-11-12: passed using a standalone copy of `artifacts/publish-durable`, seeded from `artifacts/publish-wolverine`. Legacy histories/sources/deliveries survived migration, new work succeeded, and another startup did not duplicate or revert delivery records. Tampered bytes and nine unsafe paths were rejected for the expected integrity/path errors; traversal used an existing outside-data sentinel with matching hash/length. The forced-stop check separately proved a real pending generation in the stopped database, restored it, observed further attempts and exhaustion, then verified persisted dead-letter replay across another restart. All owned processes and temporary package/data copies were removed.

Workflow verification on 2026-11-12: passed using a standalone `publish-workflows` copy seeded from `publish-durable`. The upgrade preserved exact JSON hashes for existing packets, saved revisions and prediction artifacts, plus original-source and delivery-artifact hashes. Public history/export responses add workflow metadata; older delivery snapshots honestly retain null workflow metadata. Fresh processing/delivery and repeated startup passed. The workflow-aware forced-stop check also preserved the catalog/default and an archived v1 snapshot after publishing v2; pending retry recovery, dead-letter replay and linked reprocessing all retained the original settings. All owned verification processes and disposable copies were cleaned up.

Platform verification on 2026-11-13: passed against a relocated `publish-platform` copy seeded from `publish-workflows`. Historical JSON/source/artifact hashes, approval history and export provenance survived restore after deleting the original data; fresh processing/delivery and repeated startup passed. The forced-stop pending-retry check also passed catalog/default recovery, archived workflow preservation, failure exhaustion, dead-letter replay and linked reprocessing. Platform-specific backend tests separately cover approval continuation, reference restoration, extraction carry-forward and interrupted output-file recovery. Full results: `platform-verification.md`.
