# Install the optional PDF preview engine

Paprika uses its bundled PDF.js renderer by default. To enable Leafview, start with a [published paprika package](packaging.md) and a **trusted local Leafview delivery directory** approved for distribution. The renderer runs as same-origin application code. Verify the delivery's provenance and licensing before installing it.

## Install

Run this from the paprika checkout with Bun on the packaging machine. Substitute the path to the unpacked delivery and the **published** `wwwroot`. Keep the delivery and package in separate local directories. Use an offline release folder with a single writer.

```sh
bun scripts/install-preview-engine.mjs --wwwroot "./artifacts/publish-platform/wwwroot" --delivery "../delivery"
```

The command prints the full SHA-256 version; record it with the release. It stages the complete delivery under `wwwroot/assets/preview-engine/<version>/`, then switches `index.html` by inserting the following tag at the end of its head:

```html
<meta name="paprika-preview-engine" content="/assets/preview-engine/<version>/wasm-engine.js">
```

The delivery root contains `wasm-engine.js`, `wasm-worker.js`, `pkg/folio_wasm.wasm`, `COPYING`, and `THIRD_PARTY_NOTICES.md`. Supply the whole export, including the engine's rare font and character-map resources, upstream notices and any compressed sidecars. Every file and relative path is part of the version, including notices. The installer checks the required files, validates the WASM binary, checks each `.br`/`.gz` against its identity file, and rejects links, special files, hidden files and ambiguous names. It copies the tree as supplied; a digest identifies those exact bytes but does not establish their origin. Keep any required corresponding source available alongside your distribution process.

Installing identical content reuses the same directory and verifies its contents. Installing changed content creates a new directory; previous versions stay available to open tabs and for rollback. Deploy the whole release, or publish the new asset directory **before** the updated HTML. Clients must revalidate `index.html`; versioned engine URLs may be cached as immutable. If your hosting pipeline precompresses `index.html`, remove its sidecars before switching and regenerate them afterward. The `.br`/`.gz` engine files are optional hosting representations: serve the unsuffixed URL with the correct `Content-Type` (`application/wasm` for WASM), `Content-Encoding` and `Vary: Accept-Encoding` if negotiating compression; identity files also work.

## Delivery layout

The current delivery includes `pkg/THIRD_PARTY_NOTICES.txt` and `pkg/resources/0.bin` through `11.bin`. `0.bin` supplies predefined character maps; `1.bin`–`11.bin` supply additional fonts. Keep all twelve under their original paths, even if a common invoice only requests the engine and WASM. Zoomed guided previews at 2× and above request a 1600-pixel render; ordinary previews and thumbnails use 900 and 160 pixels respectively.

## Roll back or disable the renderer

To select a previously installed version, substitute the full 64-character lowercase hash printed at installation:

```sh
bun scripts/install-preview-engine.mjs --wwwroot "./artifacts/publish-platform/wwwroot" --rollback "<previous-version>"
```

Rollback verifies the stored tree against its directory name before changing the HTML. To select the bundled PDF.js renderer again:

```sh
bun scripts/install-preview-engine.mjs --wwwroot "./artifacts/publish-platform/wwwroot" --remove
```

Both commands retain installed assets. Reload paprika after switching. To verify activation, open a PDF in a fresh browser session and check that the engine module, worker and WASM return successfully from the selected version URL. A visible preview alone is not proof: paprika can fall back to PDF.js if the optional renderer fails. After removal, a fresh session should use the bundled PDF.js worker.
