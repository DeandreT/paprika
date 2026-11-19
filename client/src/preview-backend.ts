import type { RenderTask } from "pdfjs-dist";

export interface PreviewDocument {
  render(page: number, width: number, canvas: HTMLCanvasElement): {
    promise: Promise<void>; cancel(): void;
  };
  destroy(): Promise<void>;
}
interface CanvasDocument {
  render(page: number, width: number, canvas: HTMLCanvasElement, signal: AbortSignal): Promise<void>;
  destroy(): void | Promise<void>;
}
interface CanvasModule {
  preload?(): Promise<unknown>;
  openDocument(url: string): Promise<CanvasDocument>;
}

// A trusted deployment may install a versioned, same-origin renderer. PDFs and
// packet metadata cannot select either the engine or its URL.
const configured = document.querySelector<HTMLMetaElement>('meta[name="paprika-preview-engine"]')?.content ?? "";
const entry = /^\/assets\/preview-engine\/[a-zA-Z0-9_-]+\/wasm-engine\.js$/.test(configured)
  ? configured : null;
let extension: Promise<CanvasModule> | undefined;
function moduleForPreview() {
  return extension ??= import(/* @vite-ignore */ entry!) as Promise<CanvasModule>;
}
let intent: ReturnType<typeof setTimeout> | undefined;
export function cancelPreviewIntent() { clearTimeout(intent); }
export function signalPreviewIntent() {
  cancelPreviewIntent();
  if (!entry || (navigator as Navigator & { connection?: {saveData?: boolean} }).connection?.saveData) return;
  intent = setTimeout(() => void moduleForPreview().then(m => m.preload?.()).catch(() => {}), 150);
}

async function openBuiltin(url: string): Promise<PreviewDocument> {
  const { getDocument, GlobalWorkerOptions } = await import("pdfjs-dist");
  GlobalWorkerOptions.workerSrc = "/assets/pdf.worker.min.mjs";
  const task = getDocument(url);
  const pdf = await task.promise.catch(async e => { await task.destroy(); throw e; });
  return {
    render(number, width, canvas) {
      let cancelled = false;
      let render: RenderTask | undefined;
      const promise = (async () => {
        const page = await pdf.getPage(number);
        if (cancelled) return;
        const viewport = page.getViewport({ scale: width / page.getViewport({scale: 1}).width });
        canvas.width = viewport.width;
        canvas.height = viewport.height;
        render = page.render({canvas, viewport});
        await render.promise;
      })();
      return { promise, cancel() { cancelled = true; render?.cancel(); } };
    },
    destroy: () => task.destroy(),
  };
}

export async function openPreviewDocument(url: string): Promise<PreviewDocument> {
  if (!entry) return openBuiltin(url);
  let primary: CanvasDocument;
  try { primary = await (await moduleForPreview()).openDocument(url); }
  catch { return openBuiltin(url); }
  let backup: Promise<PreviewDocument> | undefined;
  let closed = false;
  let retired: Promise<void> | undefined;
  const retire = () => retired ??= Promise.resolve(primary.destroy());
  return {
    render(page, width, canvas) {
      const controller = new AbortController();
      let fallback: ReturnType<PreviewDocument["render"]> | undefined;
      const promise = (async () => {
        if (!backup) {
          try { await primary.render(page, width, canvas, controller.signal); return; }
          catch { if (closed || controller.signal.aborted) return; }
        }
        backup ??= openBuiltin(url);
        void retire().catch(() => {});
        const doc = await backup;
        if (closed || controller.signal.aborted) return;
        fallback = doc.render(page, width, canvas);
        await fallback.promise;
      })();
      return {promise, cancel() { controller.abort(); fallback?.cancel(); }};
    },
    async destroy() {
      closed = true;
      await Promise.allSettled([retire(), backup?.then(doc => doc.destroy())]);
    },
  };
}
