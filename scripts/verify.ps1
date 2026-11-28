param([switch]$Browser)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    & dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Local tool restore failed.' }
    & dotnet csharpier check .
    if ($LASTEXITCODE -ne 0) { throw 'CSharpier check failed. Run: dotnet csharpier format .' }
    & dotnet test server.Tests
    if ($LASTEXITCODE -ne 0) { throw 'Backend tests failed.' }
    & python -m unittest discover -s tests -p 'test_*.py'
    if ($LASTEXITCODE -ne 0) { throw 'Evaluation or recovery-evidence tests failed.' }
    & bun test tests/field-review.test.ts
    if ($LASTEXITCODE -ne 0) { throw 'Field review tests failed.' }
    Push-Location client
    try {
        & bun --bun run build
        if ($LASTEXITCODE -ne 0) { throw 'Angular build failed.' }
    } finally { Pop-Location }
    if ($Browser) {
        Push-Location tests
        try {
            & bun --bun run test
            if ($LASTEXITCODE -ne 0) { throw 'Browser/API tests failed.' }
        } finally { Pop-Location }
    }
} finally { Pop-Location }
