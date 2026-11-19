// Run with Bun; uses its built-in HTMLRewriter and no package dependencies.
import { createHash, randomUUID } from "node:crypto";
import {
  chmodSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, realpathSync,
  renameSync, rmSync, writeFileSync,
} from "node:fs";
import { dirname, isAbsolute, join, relative, resolve, sep } from "node:path";
import { parseArgs } from "node:util";
import { brotliDecompressSync, gunzipSync } from "node:zlib";

const usage = `bun scripts/install-preview-engine.mjs --wwwroot PATH (--delivery PATH | --rollback VERSION | --remove)`;
const prefix = "/assets/preview-engine";
const metaName = "paprika-preview-engine";
const entry = "wasm-engine.js";
const sha256 = (bytes) => createHash("sha256").update(bytes).digest("hex");

function directory(path) {
  if (!lstatSync(path).isDirectory()) throw Error(`Expected a directory (not a link): ${path}`);
  return realpathSync(path);
}

function regularFile(path) {
  const stat = lstatSync(path);
  if (!stat.isFile()) throw Error(`Expected a regular file (not a link): ${path}`);
  return stat;
}

function contains(parent, child) {
  const path = relative(parent, child);
  return !isAbsolute(path) && path !== ".." && !path.startsWith(`..${sep}`);
}

function snapshot(root) {
  // A delivery is a trusted static export, never a source checkout. Keep empty
  // directories too. Reading each file once binds the bytes we copy to the hash.
  const tree = new Map();
  function walk(dir) {
    const seen = new Set();
    for (const name of readdirSync(join(root, dir)).sort()) {
      const key = name.toLowerCase();
      if (!/^[a-z0-9_-][a-z0-9_ .()+-]*$/i.test(name) || /[. ]$/.test(name) ||
          /^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)/i.test(name) ||
          key === "web.config" || key === "node_modules" || seen.has(key)) {
        throw Error(`Unsafe or non-portable delivery name: ${dir}${name}`);
      }
      seen.add(key);
      const path = `${dir}${name}`;
      const absolute = join(root, path);
      const stat = lstatSync(absolute);
      if (stat.isFile()) tree.set(path, readFileSync(absolute));
      else if (stat.isDirectory()) {
        tree.set(path, null);
        walk(`${path}/`);
      } else throw Error(`Delivery contains a link or special file: ${path}`);
    }
  }
  walk("");
  return tree;
}

function validate(tree) {
  for (const path of [entry, "wasm-worker.js", "pkg/folio_wasm.wasm", "COPYING", "THIRD_PARTY_NOTICES.md",
    "pkg/THIRD_PARTY_NOTICES.txt", "pkg/RUST-STDLIB-NOTICES.html",
    ...Array.from({length: 12}, (_, i) => `pkg/resources/${i}.bin`)]) {
    if (!tree.get(path)?.length) throw Error(`Missing or empty delivery file: ${path}`);
  }
  if (!WebAssembly.validate(tree.get("pkg/folio_wasm.wasm"))) throw Error("Invalid renderer WASM binary");
  for (const [path, bytes] of tree) {
    if (bytes === null) continue;
    const decode = path.endsWith(".br") ? brotliDecompressSync : path.endsWith(".gz") ? gunzipSync : null;
    if (!decode) continue;
    const raw = tree.get(path.slice(0, -3));
    if (!raw || !decode(bytes, { maxOutputLength: Math.max(raw.length, 1) }).equals(raw)) {
      throw Error(`Compressed sidecar does not match its identity file: ${path}`);
    }
  }
  // Hash a deterministic, unambiguous list of relative paths and content hashes.
  return sha256(JSON.stringify(Array.from(tree, ([path, bytes]) => [path, bytes === null ? null : sha256(bytes)])));
}

function publishTree(parent, version, tree) {
  const dest = join(parent, version);
  if (lstatSync(dest, { throwIfNoEntry: false })) {
    if (validate(snapshot(directory(dest))) !== version) throw Error(`Installed version is damaged: ${version}`);
    return;
  }
  const stage = mkdtempSync(join(parent, ".install-"));
  try {
    for (const [path, bytes] of tree) {
      if (bytes === null) mkdirSync(join(stage, path));
      else writeFileSync(join(stage, path), bytes, { flag: "wx" });
    }
    // mkdtemp defaults to owner-only on POSIX; the server may run as a different user.
    if (process.platform !== "win32") chmodSync(stage, lstatSync(parent).mode & 0o777);
    renameSync(stage, dest);
  } finally {
    rmSync(stage, { recursive: true, force: true });
  }
}

function configure(html, version) {
  let heads = 0, ends = 0;
  const output = new HTMLRewriter()
    .on("head", { element(el) {
      heads++;
      el.onEndTag((end) => {
        ends++;
        if (version) end.before(`<meta name="${metaName}" content="${prefix}/${version}/${entry}">`, { html: true });
      });
    } })
    .on("meta", { element(el) {
      if (el.getAttribute("name")?.toLowerCase() === metaName) el.remove();
    } })
    .transform(html);
  if (heads !== 1 || ends !== 1) throw Error("Expected one explicitly closed <head> in published index.html");
  return output;
}

export function install({ wwwroot, delivery, rollback, remove }) {
  if (!wwwroot || [delivery, rollback, remove].filter(Boolean).length !== 1) throw Error(usage);
  if (rollback && !/^[0-9a-f]{64}$/.test(rollback)) throw Error("Expected a full lowercase SHA-256 version");
  const root = directory(resolve(wwwroot));
  const assets = directory(join(root, "assets"));
  regularFile(join(assets, "pdf.worker.min.mjs"));
  const index = join(root, "index.html");
  const mode = regularFile(index).mode;
  const html = readFileSync(index, "utf8");
  const parent = join(assets, "preview-engine");
  let tree, version = rollback;
  if (delivery) {
    const source = directory(resolve(delivery));
    if (contains(source, root) || contains(root, source)) throw Error("Delivery and wwwroot must be disjoint");
    tree = snapshot(source);
    version = validate(tree);
  }
  const next = configure(html, version);
  if (next !== html && [".br", ".gz"].some(ext => lstatSync(index + ext, { throwIfNoEntry: false }))) {
    throw Error("Remove precompressed index.html sidecars before changing the HTML");
  }
  if (tree) {
    if (!lstatSync(parent, { throwIfNoEntry: false })) mkdirSync(parent);
    publishTree(directory(parent), version, tree);
  } else if (rollback) {
    const installed = snapshot(directory(join(directory(parent), version)));
    if (validate(installed) !== version) throw Error(`Installed version is damaged: ${version}`);
  }
  if (next !== html) {
    const tmp = join(dirname(index), `.index-${randomUUID()}.tmp`);
    try {
      writeFileSync(tmp, next, { mode, flag: "wx" });
      renameSync(tmp, index);
    } finally {
      rmSync(tmp, { force: true });
    }
  }
  return version;
}

if (import.meta.main) {
  try {
    const { values } = parseArgs({ options: {
      wwwroot: { type: "string" }, delivery: { type: "string" },
      rollback: { type: "string" }, remove: { type: "boolean" }, help: { type: "boolean" },
    } });
    if (values.help) console.log(usage);
    else console.log(install(values) ?? "Preview engine meta removed.");
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
