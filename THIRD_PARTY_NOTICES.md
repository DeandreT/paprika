# Third-party notices

Keep this file, `COPYING`, `notices/`, and the notice files under `wwwroot/` with the distributed application. Each dependency retains its own terms. This file describes the evidence included in a package; it does not grant rights on behalf of a dependency author.

## Where the texts are

| Material | In the package |
| --- | --- |
| Compiled Angular application | `wwwroot/3rdpartylicenses.txt` is Angular's production-build output. `wwwroot/notices/browser-packages.txt` identifies the corresponding installed versions. These files also accompany a separately copied browser directory. |
| PDF.js worker and library | `wwwroot/notices/pdfjs-dist/` contains the package's Apache License 2.0, notices recovered from the library and worker source maps, and the package's other license files. The copied worker retains its own Mozilla Foundation header. |
| API restore, including transitive packages | `notices/nuget.json` records ID/version, content hash, whether the package is in the published dependency manifest, license declaration, author/copyright fields and repository revision when supplied. `notices/nuget/<ID>/<version>/` contains the nuspec and package-supplied license/notice files; `notices/terms/` supplies common MIT and Apache-2.0 terms for expression-only packages. Author fields are not evidence of copyright ownership. |
| Native PDFium, Skia and SQLite | `notices/native-assets.json` lists the paths, byte counts and SHA-256 hashes of the native libraries present in this publish, including runtime-specific copies. |
| Ubuntu image and fonts | In the image, `/app/notices/os/packages.tsv` lists installed binary package, binary version, source package and source version. `os/copyright/` contains available per-package texts, `os/common-licenses/` the shared license texts they reference, and `os/missing-copyright.txt` identifies missing records. `/app/notices/dotnet/` contains the base runtime license, third-party notices and installed runtime versions. |

### Browser assets

The inspected production build credits Angular core/common/forms/platform-browser 22.2.6 and CDK/Material 22.2.7 (MIT), RxJS 7.8.2 (Apache-2.0), tslib 2.8.1 (0BSD) and pdfjs-dist 5.4.624 (Apache-2.0). The separate `assets/pdf.worker.min.mjs` copy is checked against the installed package byte for byte. Its worker source map includes qcms, JBIG2 and OpenJPEG JavaScript and credits for code from Glyph & Cog, Mozilla and Opera. The standalone wasm binaries, CMaps, ICC profiles and standard font assets in `pdfjs-dist` are not copied by `client/angular.json`; their license files are retained conservatively. Build-only Bun, Angular CLI and the .NET SDK are not copied from the builder stages into the final image.

### Managed packages and native libraries

- **Docnet.Core 2.6.0** declares MIT for its wrapper. The NuGet package includes PDFium itself, with a combined license for each native build. Windows, Linux and macOS native assets can be present in a runtime-independent publish even if it is launched only on Windows. The Linux container publishes `linux-x64`; the ordinary `linux/native/pdfium.so` is the Docnet x64 Linux asset. Keep **all seven** `Docnet.Core/2.6.0/runtimes/*/native/LICENSE` files: the two macOS texts differ from the Windows/Linux texts. The bundles cover PDFium and components such as FreeType, libjpeg-turbo/IJG, libpng, LibTIFF, lcms, OpenJPEG and ICU.
- **SkiaSharp 3.119.1** and its Win32, Linux and transitive macOS NativeAssets packages each supply `LICENSE.txt`. The NativeAssets packages also supply `THIRD-PARTY-NOTICES.txt`, which includes Skia's BSD terms and the licenses of individual components. Do not replace it with the wrapper's MIT license.
- This software is based in part on the work of the Independent JPEG Group and on the work of the [FreeType Team](https://freetype.org/). The SkiaSharp notice includes this acknowledgment: “This product includes DNG technology under license by Adobe Systems Incorporated.”
- **PdfPig 0.1.16** is Apache-2.0 and credits Apache PDFBox. Its DLLs also embed Adobe CMaps, glyph lists, Core 14 font metrics and an ICC profile (Copyright International Color Consortium, 2015). `notices/nuget/PdfPig/0.1.16/embedded/` reproduces the text-resource copyright headers and the original `MustRead.html` that accompanies the AFM files. **PDFsharp 6.2.4** is a separate MIT package.
- **Microsoft.Data.Sqlite 10.0.15** and its Core package declare MIT. **SQLitePCLRaw 3.0.5** packages declare Apache-2.0. The native **SQLite 3.53.4** package has its own `LICENSE.txt` identifying SQLite as public domain. Keep the terms for both the managed provider and the native library.
- **WolverineFx 6.64.0** (including SQLite, RDBMS and runtime compilation), JasperFx, Weasel and Roslyn are included in the API restore. Their package-level licenses and bundled Roslyn third-party notices are recorded separately. Other restored components include Polly.Core 8.6.5 (BSD-3-Clause) and NewId 4.0.1 (Apache-2.0).
- **Azure.Storage.Blobs 12.30.1** and **Azure.Identity 1.21.0** declare MIT. Their resolved SDK dependencies include Azure.Core, Azure.Storage.Common, System.ClientModel, MSAL and identity abstractions. Use `nuget.json` for exact transitive versions in a particular build. Providers reached over HTTP are not thereby redistributed as SDK binaries.

### System packages and fonts

The framework-dependent Windows package expects a separately installed ASP.NET Core runtime; it does not include Ubuntu or DejaVu fonts. The Linux image redistributes its base layers, .NET runtime, `libfontconfig1`, `fonts-dejavu-core` and their installed dependencies. The DejaVu/Bitstream Vera font permissions, naming conditions and other notices are in the font package's copyright file; the application license does not relicense the fonts. Inspect the **finished image**, since apt-installed patch versions are not fixed by the base image digest alone.

## Reproducing and reviewing a distribution

Run `scripts/publish.ps1` or build the Dockerfile. Both run `scripts/licensing/collect-notices.ps1` against the build's own restores and published DLLs without fetching notice text from an external service. A local check can also use `pwsh -File scripts/licensing/collect-notices.ps1 -PackageDirectory <fresh-publish-folder>` after copying the matching Angular `browser/` contents to that folder's `wwwroot/`. The Dockerfile additionally runs `scripts/licensing/collect-system.sh` inside the final stage.

**Before external distribution:**

1. Reconcile the inventories with the exact binaries and browser assets being delivered. Some NuGets provide only an expression or URL, and some provide no copyright line (the JasperFx/Wolverine/Weasel family, for example), or only “MIT” in that field (Docnet). Obtain missing copyright and NOTICE files from the exact upstream revisions recorded in the nuspecs; review PdfPig's PDFBox-derived code, ICC profile and embedded resources. Standard license wording and author metadata alone do not resolve missing upstream attribution.
2. Confirm applicable native components against the shipped builds. Skia's combined notice lists, among others, libmicrohttpd (conditional LGPL/eCos terms), FreeType (alternative terms), and the Adobe DNG SDK; an entry is not proof that a particular feature was linked. Establish any source, relinking, naming or other distribution conditions for the actual builds, or change the dependency/build. Review the PDFium bundle for the selected platforms as well.
3. For a container, review `os/missing-copyright.txt`, the package-specific notices and the runtime notices. Retain exact matching upstream source, packaging revisions, modifications and build material where required and arrange the corresponding-source delivery applicable to the distribution. A list of packages or upstream URLs, an application-only source archive, and a base-image digest alone do not supply that source. The releases of Docker/Bun/SDK tooling used only during build are a separate scope.

Resolve these issues for the exact artifact before treating it as cleared for redistribution.
