# Upgrading to paprika

PaperworkClassifier is now **paprika**. The API project and published assembly are `server/paprika.Api.csproj` and `paprika.Api.dll`. Update service commands, launchers and package references; use the launcher shipped with the matching package.

## Configuration

Rename the `Paperwork` configuration section to `paprika`: for example, `Paperwork:Mode` becomes `paprika:Mode`, and environment variable `Paperwork__Mode` becomes `paprika__Mode`. This applies to provider keys, data directories, connectors, hosting and operations. Old configuration keys are not aliases and must be migrated before starting the new release. `OPENAI_API_KEY` is unchanged.

Update secret-profile environment mappings to the new names without putting secret values in files or command arguments. Suggested profile names are `paprika-dev` and `paprika-openai`; profile and secret-alias labels are local tool settings, not automatically migrated application data. Test/service harness variables now use `PAPRIKA_*` instead of `PAPERWORK_*`.

## Existing data

An explicitly configured `paprika__DataDirectory` (or `paprika:DataDirectory` through another provider) takes precedence. Migrate an old explicit `Paperwork__DataDirectory` setting; it is not read by paprika.

Without an explicit directory, the application uses the local application-data directory's `paprika` folder (`%LOCALAPPDATA%/paprika` on Windows). If that folder has no `jobs.sqlite` and the legacy `PaperworkClassifier` folder does, it reuses the legacy folder in place. If both contain databases, the new `paprika` database wins. No folder is automatically renamed, merged or copied.

Stop all instances and make a complete [offline backup](recovery.md) before upgrading. To move data deliberately, restore the backup into a fresh directory and configure `paprika__DataDirectory` to that directory. Never run old and new releases against the same data simultaneously or open upgraded data with an old binary; rollback uses a complete pre-upgrade backup restored into a fresh directory.

## Requests and sessions

New clients send `X-paprika: 1` for browser/API mutations. The backend also accepts `X-PaperworkClassifier: 1` for existing clients. These headers are request markers, not authentication; shared-mode identity requirements are unchanged.

The renamed session cookie resets shared-mode browser sessions. Users must sign in again after upgrading; this does not delete their stored packet or review data.

## Retained storage contracts

Backup manifests deliberately retain the format identifier `paperwork-backup-1`, preserving existing backup readability. Persisted prompt-version identifiers and delivery idempotency prefixes are also unchanged; do not rewrite stored history or downstream deduplication keys for the rename.

Durable commands retain their `PaperworkClassifier.*` message identities and fault-finalization header so existing pending retries and dead letters remain readable despite the namespace change.

`scripts/recovery-check.ps1` can seed an upgrade check with a previous package through `-SeedPackageDirectory`. It uses that package's original DLL, configuration prefix and request header, then switches to the paprika contract for the new package. Other current launch/check scripts target paprika packages.
