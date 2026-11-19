# Develop and test paprika

The Angular 22 app is in `client/`; the ASP.NET Core API, processing jobs and provider adapters are in `server/`. Backend regression tests live in `server.Tests/`; Playwright and offline evaluation tests live in `tests/`.

## Run in separate terminals

Use the .NET 10 SDK and Node.js 22.22.3 or later in the 22.x line with npm. The [Windows launcher](../README.md#first-run-on-windows) starts both processes. On Linux, or when the API needs a different environment from the frontend, use two terminals. On Arch Linux, install `aspnet-runtime` and `aspnet-targeting-pack` alongside `dotnet-sdk`.

Start the API from the repository root:

```sh
dotnet run --project server/paprika.Api.csproj --no-launch-profile --urls http://127.0.0.1:5080
```

In a second terminal, from the repository root:

```sh
cd client
npm ci
npm start -- --host 127.0.0.1
```

Open **http://127.0.0.1:4200**. Angular proxies `/api` to port 5080. Stop each process with Ctrl+C. For Live processing, configure [provider credentials](providers.md) only for the backend.

## Run checks

On Windows, use PowerShell 7 and Python 3. Install the client dependencies first (`npm ci` from `client/`). From the repository root, `./scripts/verify.ps1` checks CSharpier formatting, runs backend and offline Python tests, then builds Angular. To run individual checks:

```powershell
dotnet tool restore
dotnet csharpier check .   # Use dotnet csharpier format . to apply fixes.
dotnet test server.Tests
python -m unittest discover -s tests -p 'test_*.py'
npm --prefix client run build
```

For browser/API tests, install Chromium once from `tests/`:

```sh
npm ci
npx playwright install chromium
```

With both servers running, run `npm test` from `tests/`, or `./scripts/verify.ps1 -Browser` from the repository root. These tests create packets in the running API. For isolation, set `paprika__DataDirectory` to a fresh local directory **before** starting the API. See [data-directory selection](renaming.md#existing-data) for defaults.

## Check a release

- **Package acceptance:** [Publish the Windows package](packaging.md), install the test dependencies and Chromium as above, then free port 5080 and run `./scripts/acceptance.ps1` from the root. It starts and stops the package in Simulation using disposable data. Pass `-PackageDirectory` for a non-default build.
- **Upgrade and recovery:** `./scripts/recovery-check.ps1 -SeedPackageDirectory <previous-package>` checks an upgrade from a previous package; `./scripts/durability-check.ps1` tests forced-stop recovery. See [backup and recovery](recovery.md) for prerequisites and what each check verifies.
- **Classification regression:** `./scripts/benchmark.ps1` runs the [10-case synthetic benchmark](benchmark.md). Synthetic and mocked-provider results don't establish live accuracy; use the [evaluation procedure](evaluation.md) for held-out real paperwork. For past release results, see [platform verification](platform-verification.md).
