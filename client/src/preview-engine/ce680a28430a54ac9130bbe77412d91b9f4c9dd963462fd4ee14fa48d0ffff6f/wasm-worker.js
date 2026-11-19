// One dedicated worker and one unshared linear memory per open document.
const MAX_BYTES = 64 * 1024 * 1024;
let wasm;
const fonts = new Map();
let crcTable;

// Dedicated worker only. Hayro resolves nested resources synchronously. Never
// substitute an empty font/map on failure: unwinding across the import leaves
// this WASM instance unusable; the fatal response tells the host to reopen.
function copy_asset(id, ptr, size, expectedCRC) {
  let bytes = fonts.get(id);
  if (!bytes) {
    const xhr = new XMLHttpRequest();
    xhr.open("GET", new URL(`./pkg/resources/${id}.bin`, import.meta.url), false);
    xhr.responseType = "arraybuffer";
    xhr.send();
    if (xhr.status !== 200 || xhr.response?.byteLength !== size)
      throw Error(`Invalid renderer asset ${id} (HTTP ${xhr.status}, expected ${size} bytes)`);
    bytes = new Uint8Array(xhr.response); // HTTP decoding is already complete.
    crcTable ??= Uint32Array.from({ length: 256 }, (_, x) => {
      for (let i = 0; i < 8; i++) x = (x >>> 1) ^ (0xedb88320 & -(x & 1));
      return x;
    });
    let crc = -1;
    for (let i = 0; i < bytes.length; i++) crc = (crc >>> 8) ^ crcTable[(crc ^ bytes[i]) & 255];
    if (~crc !== expectedCRC) throw Error(`Invalid renderer asset ${id} (checksum)`);
    if (id !== 0) fonts.set(id, bytes); // Hayro retains the whole CMap bundle itself.
  }
  new Uint8Array(wasm.memory.buffer, ptr, size).set(bytes);
}

let bitmapTarget, bitmapCtx, bitmapDisabled = false;
function sendFrame(id, width, height, wantBitmap) {
  try {
    const view = new Uint8ClampedArray(wasm.memory.buffer, wasm.pixels(), width * height * 4);
    if (wantBitmap === true && width >= 1600 && !bitmapDisabled &&
        typeof OffscreenCanvas !== "undefined" && typeof ImageData !== "undefined" &&
        typeof OffscreenCanvas.prototype.transferToImageBitmap === "function") {
      let bitmap;
      try {
        bitmapTarget ??= new OffscreenCanvas(width, height);
        bitmapCtx ??= bitmapTarget.getContext("2d");
        if (!bitmapCtx) throw Error("Worker canvas 2D unavailable");
        if (bitmapTarget.width !== width) bitmapTarget.width = width;
        if (bitmapTarget.height !== height) bitmapTarget.height = height;
        bitmapCtx.putImageData(new ImageData(view, width, height), 0, 0);
        bitmap = bitmapTarget.transferToImageBitmap();
        self.postMessage({ id, width, height, bitmap }, [bitmap]);
        return;
      } catch {
        bitmap?.close();
        bitmapTarget = bitmapCtx = undefined;
        bitmapDisabled = true;
      }
    }
    const pixels = view.slice().buffer;
    self.postMessage({ id, width, height, pixels }, [pixels]);
  } finally {
    wasm.release_pixels();
  }
}

let receiveCode;
const code = new Promise(resolve => { receiveCode = resolve; });

async function open(url) {
  const [instance, response] = await Promise.all([
    code.then(module => WebAssembly.instantiate(module, { leafview: { copy_asset } })), fetch(url),
  ]);
  if (!response.ok) throw new Error(`PDF download failed (HTTP ${response.status})`);
  if (Number(response.headers.get("content-length")) > MAX_BYTES) {
    throw new Error("PDF exceeds 64 MiB");
  }
  if (!response.body) throw new Error("PDF download has no body");
  wasm = instance.exports;
  // Stream into WASM. Enforce the limit on decoded bytes, even when
  // Content-Length is absent or describes a compressed response.
  const reader = response.body.getReader();
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      if (!value.byteLength) continue;
      const ptr = wasm.input_chunk(value.byteLength);
      if (!ptr) throw new Error("PDF exceeds 64 MiB");
      new Uint8Array(wasm.memory.buffer, ptr, value.byteLength).set(value);
    }
  } finally {
    reader.releaseLock();
  }
  const pageCount = wasm.open_pdf();
  if (pageCount === -2) throw new Error("PDF must contain 1–250 pages");
  if (pageCount <= 0) throw new Error("Hayro could not open this PDF (invalid or password protected)");
  return pageCount;
}

self.onmessage = async ({ data: { id, type, url, page, width, module, bitmap } }) => {
  try {
    if (type === "code") { receiveCode(module); return; }
    if (type === "open") {
      self.postMessage({ id, pageCount: await open(url) });
      return;
    }
    if (type !== "render" || !wasm) throw new Error("Unexpected worker request");
    const height = wasm.render_page(page, width);
    if (height <= 0) {
      self.postMessage({ id, error: height === -3 ? "Invalid page" : "Invalid page dimensions or render exceeds 8192 px per edge / 4 MP" });
      return;
    }
    sendFrame(id, width, height, bitmap);
  } catch (e) {
    // Discard this instance after a trap, fetch failure or unexpected exception.
    self.postMessage({ id, fatal: String(e?.message ?? e) });
    self.close();
  }
};
