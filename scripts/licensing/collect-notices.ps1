# Run after the production Angular build and API publish, with the matching restore assets.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [string]$Root = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent)
)
$ErrorActionPreference = 'Stop'
# Package metadata has optional elements; absent XML properties return null.
Set-StrictMode -Version 1.0
$stage = (Resolve-Path -LiteralPath $PackageDirectory).Path
$root = (Resolve-Path -LiteralPath $Root).Path
$notices = Join-Path $stage 'notices'
if (Test-Path -LiteralPath $notices) { throw "Use a fresh package directory: $notices" }
function Copy-Text($from, $to) {
    if ((Get-Item -LiteralPath $from).Length -le 0) { throw "Empty notice: $from" }
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($to))
    [IO.File]::Copy($from, $to, $false)
}
function Save-Text($to, [string]$text) {
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($to))
    [IO.File]::WriteAllText($to, $text)
}

Copy-Text "$root/THIRD_PARTY_NOTICES.md" "$stage/THIRD_PARTY_NOTICES.md"
Copy-Text "$root/COPYING" "$stage/COPYING"
$modules = "$root/client/node_modules"
$pdf = "$modules/pdfjs-dist"
$web = "$stage/wwwroot"
$credits = "$root/client/dist/client/3rdpartylicenses.txt"
Copy-Text $credits "$web/3rdpartylicenses.txt"
$ui = foreach ($match in (Select-String -LiteralPath $credits -Pattern '^Package: (.+)$')) {
    $name = $match.Matches[0].Groups[1].Value.Trim()
    $manifest = Get-Content -LiteralPath "$modules/$name/package.json" -Raw | ConvertFrom-Json
    "$($manifest.name) $($manifest.version)"
}
if (!($ui -match '^@angular/core ')) { throw 'Missing Angular package credits.' }
Save-Text "$web/notices/browser-packages.txt" ($ui -join "`n")

$worker = 'assets/pdf.worker.min.mjs'
if ((Get-FileHash -LiteralPath "$web/$worker").Hash -ne (Get-FileHash -LiteralPath "$pdf/build/pdf.worker.min.mjs").Hash) {
    throw 'PDF.js worker differs from restored pdfjs-dist; review its licensing before packaging.'
}
foreach ($file in Get-ChildItem -LiteralPath $pdf -Recurse -File -Include 'LICENSE*','NOTICE*') {
    if ($file.Name -notmatch '^(LICENSE|NOTICE)') { continue }
    $rel = [IO.Path]::GetRelativePath($pdf, $file.FullName)
    Copy-Text $file.FullName "$web/notices/pdfjs-dist/$rel"
}
# Minification may omit headers (including Glyph & Cog's permission). Recover
# distinct notices from both corresponding source maps; do not ship the maps.
$headers = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$pdfHeaders = foreach ($map in 'pdf.mjs.map','pdf.worker.mjs.map') {
    $sources = Get-Content -LiteralPath "$pdf/build/$map" -Raw | ConvertFrom-Json -AsHashtable
    for ($i = 0; $i -lt $sources.sources.Count; $i++) {
        foreach ($m in [regex]::Matches($sources.sourcesContent[$i], '(?s)/\*.*?\*/')) {
            if ($m.Value -match 'copyright|licensed under|permission is hereby granted|redistribution and use|this code was forked' -and $headers.Add($m.Value)) {
                "$($sources.sources[$i])`n$($m.Value)`n"
            }
        }
    }
}
Save-Text "$web/notices/pdfjs-dist/source-notices.txt" ($pdfHeaders -join "`n")
Copy-Text "$pdf/LICENSE" "$notices/terms/Apache-2.0.txt"
Copy-Text "$PSScriptRoot/MIT.txt" "$notices/terms/MIT.txt"

$assets = Get-Content -LiteralPath "$root/server/obj/project.assets.json" -Raw | ConvertFrom-Json -AsHashtable
$deps = Get-Content -LiteralPath "$stage/paprika.Api.deps.json" -Raw | ConvertFrom-Json -AsHashtable
foreach ($key in $deps.libraries.Keys) {
    $pkg = $deps.libraries[$key]
    if ($pkg.type -eq 'package' -and (!$assets.libraries.Contains($key) -or
        $pkg.sha512 -cne "sha512-$($assets.libraries[$key].sha512)")) {
        throw "Publish and restore evidence differ for $key"
    }
}
$rows = foreach ($key in ($assets.libraries.Keys | Sort-Object)) {
    $lib = $assets.libraries[$key]
    if ($lib.type -ne 'package') { continue }
    $path = @($assets.packageFolders.Keys | ForEach-Object { Join-Path $_ $lib.path } | Where-Object { Test-Path -LiteralPath $_ })[0]
    if (!$path) { throw "Restored package missing: $key" }
    $spec = @(Get-ChildItem -LiteralPath $path -File -Filter '*.nuspec')
    if ($spec.Count -ne 1) { throw "Expected one nuspec for $key" }
    $meta = ([xml]([IO.File]::ReadAllText($spec[0].FullName))).package.metadata
    $dir = "$notices/nuget/$key"
    Copy-Text $spec[0].FullName "$dir/$($spec[0].Name)"
    $copied = @(foreach ($rel in $lib.files) {
        $leaf = [IO.Path]::GetFileName($rel)
        if ($leaf -match '^(licen[cs]e|notices?|copying|copyright|third[-_. ]?party(?:[-_. ]?notices?)?|patents?)([-_. ].*)?$' -or
            ($meta.license.type -eq 'file' -and $rel -eq $meta.license.'#text')) {
            Copy-Text "$path/$rel" "$dir/$rel"
            $rel
        }
    })
    $license = [string]$meta.license.'#text'
    if ((!$license -or ($meta.license.type -eq 'expression' -and $license -notin @('MIT','Apache-2.0'))) -and
        !$copied.Where({ [IO.Path]::GetFileName($_) -match '^licen[cs]e' })) {
        throw "No collected license terms for $key ($license). Review before packaging."
    }
    [ordered]@{
        package = $key; sha512 = $lib.sha512; inPublishedDeps = $deps.libraries.Contains($key)
        licenseType = $meta.license.type; license = $license; licenseUrl = [string]$meta.licenseUrl
        authors = [string]$meta.authors; copyright = [string]$meta.copyright
        project = [string]$meta.projectUrl; repository = [string]$meta.repository.url; commit = [string]$meta.repository.commit
        packagedNotices = $copied
    }
}
Save-Text "$notices/nuget.json" (ConvertTo-Json -InputObject @($rows) -Depth 4)
$native = foreach ($file in (Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName)) {
    if ($file.Name -match '^(pdfium|libSkiaSharp|(?:lib)?e_sqlite3)\.(so|dylib|dll|a)$') {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($stage, $file.FullName).Replace('\','/')
            bytes = $file.Length; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
    }
}
if (!$native) { throw 'No published native PDF/graphics/database binaries found' }
Save-Text "$notices/native-assets.json" (ConvertTo-Json -InputObject @($native))
# PdfPig embeds licensed data in the managed DLLs, not as loose NuGet files.
$pig = @($rows | Where-Object package -like 'PdfPig/*')[0].package
$embedded = "$notices/nuget/$pig/embedded"
foreach ($dll in 'UglyToad.PdfPig.dll','UglyToad.PdfPig.Fonts.dll') {
    # Load from bytes so the local Windows publisher can move/delete its staging directory.
    $asm = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes("$stage/$dll"))
    $parts = foreach ($name in ($asm.GetManifestResourceNames() | Sort-Object)) {
        if ($name -notmatch '\.(CMap|AdobeFontMetrics|GlyphList)\.') { continue }
        $stream = $asm.GetManifestResourceStream($name)
        try {
            if ($name.EndsWith('.MustRead.html')) {
                $null = [IO.Directory]::CreateDirectory($embedded)
                $target = [IO.File]::Create("$embedded/MustRead.html")
                try { $stream.CopyTo($target) } finally { $target.Dispose() }
            } else {
                $reader = [IO.StreamReader]::new($stream)
                try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
                $lines = @($text -split '\r?\n' | Where-Object { $_ -match '^(%%Copyright|Notice |Comment Copyright|#)' })
                if ($lines) { "$name`n$($lines -join "`n")`n" }
            }
        } finally { $stream.Dispose() }
    }
    Save-Text "$embedded/$dll.txt" ($parts -join "`n")
}
foreach ($required in @(
    "$embedded/MustRead.html", "$web/notices/pdfjs-dist/LICENSE", "$web/notices/pdfjs-dist/wasm/LICENSE_QCMS",
    "$notices/nuget/Docnet.Core/*/runtimes/linux/native/LICENSE", "$notices/nuget/Docnet.Core/*/runtimes/osx-x64/native/LICENSE",
    "$notices/nuget/SkiaSharp.NativeAssets.Linux/*/THIRD-PARTY-NOTICES.txt", "$notices/nuget/SQLite/*/LICENSE.txt"
)) {
    if (!(Get-Item -Path $required -ErrorAction SilentlyContinue | Where-Object Length -gt 0)) { throw "Required notice missing: $required" }
}
