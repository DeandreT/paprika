# Build and run the Windows package

One .NET process serves the Angular UI, PDF preview worker, API and OpenAPI on `http://127.0.0.1:5080`. If you already have a release folder, start at **Run the package**.

## Build from source

Install PowerShell 7, the .NET 10 SDK, and Bun 1.4.5. From the repository root, in a shell without provider credentials:

```powershell
./scripts/publish.ps1
```

The script checks backend formatting, installs frontend dependencies from the lockfile, builds Angular and publishes a framework-dependent Release build to `artifacts/publish-platform`. It won't overwrite existing output. For another build, choose a fresh local directory outside OneDrive:

```powershell
./scripts/publish.ps1 -OutputDirectory ./artifacts/release-2
```

## Run the package

Copy the **entire release folder** to the target Windows machine. Install PowerShell 7 and the **.NET 10 ASP.NET Core Runtime**; Bun and the SDK aren't needed. Stop any development API on port 5080, then run this from the release folder:

```powershell
./run-published.ps1
```

Open **http://127.0.0.1:5080** and choose **Try demo packet**, or upload a PDF, PNG or JPEG. Health is at `/api/health` and OpenAPI at `/openapi/v1.json`. Press Ctrl+C to stop. To launch the same package from a source checkout, use `./scripts/run-published.ps1 -PackageDirectory ./artifacts/publish-platform`.

The default Simulation workflow reads embedded text locally without credentials. For Live processing, inject provider secrets only into the runtime process, select a published Live workflow and use approved inputs. In the source documentation, `docs/providers.md` lists provider settings and data sent to each service.

## Preserve packet data on upgrade

Packet data is stored separately from the release, normally in `%LOCALAPPDATA%/paprika`. An existing legacy database may be reused. Set `paprika__DataDirectory` to choose a different local directory before starting. Don't run two instances against the same directory.

Stop the app and back up the entire data directory **before** upgrading. To roll back, restore the pre-upgrade backup into a fresh directory for the earlier binary. The source repository's `docs/recovery.md` includes backup, verification and restore commands; `docs/renaming.md` explains legacy directory selection.

Included with the release: [configure workflows](workflows.md), [set up shared review](operations-platform.md), [connect intake and delivery](connectors-platform.md), and [inspect durable queues](wolverine.md).
