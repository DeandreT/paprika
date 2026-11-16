# Local release package

This packages the approved local PoC: one .NET process serves the Angular UI, API, and OpenAPI on `http://127.0.0.1:5080`. It is a framework-dependent release, not an enterprise deployment or standalone runtime installer.

## Build

On Windows, install PowerShell 7, the .NET 10 SDK, and the Node/npm version required by `client/package.json`. From `C:/dev/paprika`, in a shell without provider credentials:

```powershell
pwsh -NoProfile -File ./scripts/publish.ps1
```

The script restores the pinned CSharpier tool, checks backend formatting, installs the frontend from its lockfile (`npm ci`), builds Angular production assets, and publishes the API in Release configuration. It copies static assets, including the PDF worker, to `wwwroot`. Build outputs stay outside OneDrive. `artifacts/` is ignored by source control.

The default output is `artifacts/publish-platform`. Existing output is never deleted or overwritten. For another build, choose a new destination:

```powershell
pwsh -NoProfile -File ./scripts/publish.ps1 -OutputDirectory ./artifacts/release-2
```

The script deletes only its own uniquely named temporary staging directory on failure. Keep the lockfile with the source; no provider keys are needed during publishing.

## Run

The target Windows machine needs the .NET 10 **ASP.NET Core Runtime** and PowerShell 7. Copy the entire release folder to local storage outside OneDrive. No Node/npm or source tree is required to run it.

```powershell
pwsh -NoProfile -File ./artifacts/publish-platform/run-published.ps1
```

Alternatively, from the repository:

```powershell
pwsh -NoProfile -File ./scripts/run-published.ps1 -PackageDirectory ./artifacts/publish-platform
```

Open `http://127.0.0.1:5080`. Health is `/api/health`; the OpenAPI document is `/openapi/v1.json`. Stop with Ctrl+C. Port 5080 must be free; stop your own development API before starting the package. The launcher sets the content root to the package so static assets work regardless of the caller's directory.

Simulation mode is the default and needs no credentials. It accepts PDF/PNG/JPEG uploads, extracts embedded PDF text locally, and proposes labels/boundaries using deterministic rules. Scanned PDFs and images without text remain unknown for manual review; no OCR or AI result is invented. The separate demo button generates a synthetic example. Packet data defaults to `%LOCALAPPDATA%/paprika`, separate from the release folder, with [legacy database fallback](renaming.md#existing-data). Set `paprika__DataDirectory` to a different local directory for isolated demonstrations. Replacing the package does not reset packet data. Do not run two instances against the same data directory.

## Live runtime configuration

Configure these environment variables only for the runtime process:

- `paprika__Mode=Live`
- `paprika__AzureEndpoint`: Azure Document Intelligence HTTPS endpoint
- `paprika__AzureKey`: Azure key
- `paprika__JevApiKey`: TypeSafe Jev API key
- `paprika__JevModel`: optional model, default `jev-latest`
- `paprika__DataDirectory`: optional local data folder


```powershell
$env:paprika__Mode = 'Live'
$env:paprika__AzureEndpoint = 'https://YOUR-RESOURCE.cognitiveservices.azure.com'
```

Never wrap `publish.ps1`, dependency installation, or build commands in credential injection. Do not save keys in the package, source, browser configuration, or documentation. The packaged launcher runs the already-built DLL without restoration or compilation.

Workflow settings determine the mode for new workflow-aware runs. The environment seeds a new catalog and supplies provider configuration; changing it does not rewrite published workflows or accepted packets. To use live processing with an existing catalog, publish/select a Live workflow using the approved server-side default profile and configured model. Draft sample previews run in Simulation mode. See the packaged `workflows.md` for library, release and workflow management.

## Hosting behavior

WolverineFx and its SQLite persistence are included in the package. Durable local processing/delivery queues, retry storage and dead letters run without an external broker. Application checkpoints, approved revisions and delivery artifacts remain separate business records. Packaging checks exercise the durable handlers from the published application.

The platform release extends versioned workflows with quality routing, learned text classification, reference lookup, folder/HTTP connectors, shared review, extraction, visual templates, cases, flows and enhanced outputs. Each module guide is included in the package. Simulation delivery remains available separately from configured real connectors. See `docs/recovery.md` for stopped-application backup, integrity validation, and restore into a fresh data directory.

Upgrade using a stopped-application backup first. Legacy delivery state is migrated without deleting the old database or artifacts. After migration, use the new release for that data directory; roll back by restoring the pre-upgrade backup to a separate directory for the earlier binary. Never run two releases against the same data simultaneously.

Loopback/host validation and the `X-paprika: 1` mutation-header protection run before static files. The legacy marker remains accepted; see [rename notes](renaming.md). Same-origin requests on port 5080 and the existing development proxy on port 4200 remain supported. The SPA fallback does not convert missing `/api` or `/openapi` endpoints into HTML successes. The development command `scripts/dev.ps1` still runs Angular separately.
