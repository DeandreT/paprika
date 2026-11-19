# Licensing

Original source: copyright Deandre Thomas, licensed under GPL-3.0-only; see `COPYING`. The generated corpus uses synthetic content. The generator and canvas marker checks are adapted from the paprika client preview benchmark under the same license.

This source repository does not vendor the following packages. Preserve their upstream notices when packaging a browser build:

- [Hayro](https://github.com/LaurenzV/hayro) 0.9.0 and its Rust dependencies supply PDF parsing, interpretation and rasterization. Hayro is MIT OR Apache-2.0. Leafview modifies the byte loading in `hayro-font` 0.5.0 and `hayro-cmap` 0.1.1. The default delivery includes all 14 PDFium/Foxit substitute fonts (three in WASM, eleven separate files) and Adobe's character maps. The generated `pkg/THIRD_PARTY_NOTICES.txt` reproduces the locked crates' notices, including those for the fonts, CMaps and color profiles. The Rust toolchain's library notices are in `pkg/RUST-STDLIB-NOTICES.html`.
- [PDF.js](https://github.com/mozilla/pdf.js) 5.4.624 (Apache-2.0) supplies the comparator. Font resources, character maps and optional decoders carry additional licenses in `pdfjs-dist/standard_fonts`, `cmaps`, and `wasm`.
- The browser tests use [Playwright](https://github.com/microsoft/playwright) (Apache-2.0); the synthetic fixture generator uses ReportLab and Pillow. These are development dependencies.

`scripts/precompress.mjs` includes these notice files, this index and `COPYING` in the static renderer export. Distributors must also make the GPL corresponding source for their actual version (including any changes and build scripts) available in accordance with `COPYING`.
