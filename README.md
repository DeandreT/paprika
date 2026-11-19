# paprika

paprika turns a mixed packet of paperwork into reviewed documents. Upload PDFs or images, check the proposed document types and page groups, then export approved PDFs and a manifest that traces them back to their source pages.

## First run on Windows

Install the **.NET 10 SDK**, **Bun 1.4.5**, and **PowerShell 7**. From the repository root in PowerShell 7:

```powershell
./scripts/dev.ps1 -Install
```

Open **http://127.0.0.1:4200** after the first build. Choose **Try demo packet** to walk through review and export, or upload a PDF, PNG or JPEG in the default **Simulation** workflow.

Simulation runs locally without provider keys. It reads embedded PDF text; scans and images without text need manual review. The demo uses a separate synthetic packet.

On later runs, omit `-Install`. Press **Ctrl+C** to stop both the API and the web app.

## Other ways to run

- [Run the API and web app in separate terminals](docs/development.md#run-in-separate-terminals), including on Linux.
- [Build or run the Windows package](docs/packaging.md) to serve the UI and API from one .NET process.

## Documentation

[Browse the task guides](docs/README.md) to configure a workflow, connect live providers, use the API or operate an installation.
