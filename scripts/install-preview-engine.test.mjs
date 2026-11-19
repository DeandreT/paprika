import { afterEach, expect, test } from "bun:test";
import {
  mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, symlinkSync, writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { gzipSync } from "node:zlib";
import { install } from "./install-preview-engine.mjs";

let tmp;
afterEach(() => { if (tmp) rmSync(tmp, { recursive: true, force: true }); });

function layout() {
  tmp = mkdtempSync(join(tmpdir(), "paprika-install-test-"));
  const delivery = join(tmp, "delivery"), wwwroot = join(tmp, "wwwroot");
  const raw = Buffer.from([0, 97, 115, 109, 1, 0, 0, 0]);
  for (const dir of ["pkg/resources", "notices", "resources"]) mkdirSync(join(delivery, dir), { recursive: true });
  for (let i=0;i<12;i++) writeFileSync(join(delivery, `pkg/resources/${i}.bin`), `fixture-${i}`);
  for (const [name, data] of Object.entries({
    "wasm-engine.js": "export {};", "wasm-worker.js": "onmessage = () => {};",
    "COPYING": "fixture", "THIRD_PARTY_NOTICES.md": "fixture", "pkg/THIRD_PARTY_NOTICES.txt": "fixture",
    "pkg/RUST-STDLIB-NOTICES.html": "fixture", "notices/FONT.txt": "fixture",
    "pkg/folio_wasm.wasm": raw, "pkg/folio_wasm.wasm.gz": gzipSync(raw), "resources/font.bin": "old",
  })) writeFileSync(join(delivery, name), data);
  mkdirSync(join(wwwroot, "assets"), { recursive: true });
  writeFileSync(join(wwwroot, "assets/pdf.worker.min.mjs"), "");
  const index = join(wwwroot, "index.html"), versions = join(wwwroot, "assets/preview-engine");
  const html = '<!doctype html><html><head><script>let x = "</head>";</script></head><body></body></html>';
  writeFileSync(index, html);
  return { delivery, wwwroot, index, versions, html };
}

test("versions the whole delivery, switches atomically, retains prior editions, restores the index", () => {
  const { delivery, wwwroot, index, versions, html } = layout();
  const options = { delivery, wwwroot };
  const a = install(options);
  const configured = readFileSync(index, "utf8");
  expect(configured).toContain(`<meta name="paprika-preview-engine" content="/assets/preview-engine/${a}/wasm-engine.js"></head>`);
  expect(readFileSync(join(versions, a, "notices/FONT.txt"), "utf8")).toBe("fixture");
  expect(install(options)).toBe(a);
  expect(readFileSync(index, "utf8")).toBe(configured);
  expect(readdirSync(versions)).toEqual([a]);
  writeFileSync(join(delivery, "resources/font.bin"), "new");
  const b = install(options);
  expect(b).not.toBe(a);
  expect(readFileSync(join(versions, b, "resources/font.bin"), "utf8")).toBe("new");
  install({ wwwroot, rollback: a });
  expect(readFileSync(index, "utf8")).toBe(configured);
  writeFileSync(join(versions, b, "resources/font.bin"), "damaged");
  expect(() => install({ wwwroot, rollback: b })).toThrow("damaged");
  expect(() => install(options)).toThrow("damaged");
  expect(readFileSync(index, "utf8")).toBe(configured);
  install({ wwwroot, remove: true });
  expect(readFileSync(index, "utf8")).toBe(html);
});

test("rejects junctions, overlap, traversal and bad sidecars before modifying the release", () => {
  const { delivery, wwwroot, index, versions, html } = layout();
  const link = join(delivery, "external");
  symlinkSync(wwwroot, link, process.platform === "win32" ? "junction" : "dir");
  expect(() => install({ wwwroot, delivery })).toThrow(/link/);
  rmSync(link);
  symlinkSync(delivery, versions, process.platform === "win32" ? "junction" : "dir");
  expect(() => install({ wwwroot, delivery })).toThrow(/link/);
  expect(() => install({ wwwroot, delivery: tmp })).toThrow(/disjoint/);
  expect(() => install({ wwwroot, rollback: "../index.html" })).toThrow(/SHA-256/);
  writeFileSync(join(delivery, "pkg/folio_wasm.wasm.gz"), gzipSync("stale"));
  expect(() => install({ wwwroot, delivery })).toThrow(/sidecar/);
  expect(readFileSync(index, "utf8")).toBe(html);
});

test("rejects a delivery missing a rarely requested font before activation", () => {
  const { delivery, wwwroot, index, html } = layout();
  rmSync(join(delivery, "pkg/resources/11.bin"));
  expect(() => install({ wwwroot, delivery })).toThrow("pkg/resources/11.bin");
  expect(readFileSync(index, "utf8")).toBe(html);
});
