import {
  Component, Directive, ElementRef, Injectable, OnDestroy, effect, inject, input, signal,
} from "@angular/core";
import { openPreviewDocument, type PreviewDocument } from "./preview-backend";

interface CacheEntry {
  document: Promise<PreviewDocument>;
  users: number;
}

@Injectable({ providedIn: "root" })
export class PdfCache implements OnDestroy {
  private readonly limit = 2;
  // Insertion order is least to most recently acquired.
  private readonly documents = new Map<string, CacheEntry>();
  private readonly waiters = new Set<() => void>();
  private retiring = 0;
  private closed = false;

  async withDocument(
    url: string, signal: AbortSignal, draw: (doc: PreviewDocument) => Promise<void>,
  ): Promise<void> {
    while (!this.closed && !signal.aborted) {
      let entry = this.documents.get(url);
      if (entry) {
        this.documents.delete(url);
      } else if (this.documents.size + this.retiring < this.limit) {
        entry = { document: openPreviewDocument(url), users: 0 };
      } else {
        // Even an opening or asynchronously shutting-down worker occupies a slot.
        if (!this.retiring) {
          const idle = [...this.documents].find(([, candidate]) => !candidate.users);
          if (idle) this.retire(...idle);
        }
        await this.waitForSlot(signal);
        continue;
      }
      this.documents.set(url, entry);
      entry.users++;
      try {
        let document: PreviewDocument;
        try {
          document = await entry.document;
        } catch (error) {
          // Share an in-flight open, but don't retain failed opens.
          if (this.documents.get(url) === entry) this.documents.delete(url);
          throw error;
        }
        if (!signal.aborted && !this.closed) await draw(document);
      } finally {
        entry.users--;
        if (!entry.users && this.closed && this.documents.get(url) === entry)
          this.retire(url, entry);
        this.wakeWaiters();
      }
      return;
    }
  }

  private waitForSlot(signal: AbortSignal) {
    return new Promise<void>((resolve) => {
      const wake = () => {
        this.waiters.delete(wake);
        signal.removeEventListener("abort", wake);
        resolve();
      };
      this.waiters.add(wake);
      signal.addEventListener("abort", wake, { once: true });
    });
  }

  private wakeWaiters() {
    for (const wake of this.waiters) wake();
  }

  private retire(url: string, entry: CacheEntry) {
    this.documents.delete(url);
    this.retiring++;
    void entry.document.then(doc => doc.destroy()).catch(error => {
      console.warn("Could not close preview document", error);
    }).finally(() => {
      this.retiring--;
      this.wakeWaiters();
    });
  }

  ngOnDestroy() {
    this.closed = true;
    this.wakeWaiters();
    for (const [url, entry] of this.documents)
      if (!entry.users) this.retire(url, entry);
  }
}

// One observer across a packet, including thumbnails inside clipped scroll containers.
@Injectable({ providedIn: "root" })
class PreviewSchedule implements OnDestroy {
  private observer?: IntersectionObserver;
  private readonly watched = new Map<Element, (visible: boolean) => void>();
  private readonly waiting = new Set<() => void>();
  private inspections = 0;
  private thumbnails = 0;
  private readonly thumbnailLimit = 2;
  private frame?: number;

  observe(host: Element, visible: (value: boolean) => void) {
    if (typeof IntersectionObserver === "undefined") {
      visible(true);
      return () => {};
    }
    this.observer ??= new IntersectionObserver(entries => {
      for (const entry of entries)
        this.watched.get(entry.target)?.(entry.isIntersecting);
    });
    this.watched.set(host, visible);
    this.observer.observe(host);
    return () => {
      this.observer?.unobserve(host);
      this.watched.delete(host);
    };
  }

  inspectionStarted() {
    this.inspections++;
    let pending = true;
    return () => {
      if (!pending) return;
      pending = false;
      this.inspections--;
      this.schedule();
    };
  }

  thumbnailReady(start: () => void) {
    let running = false;
    const launch = () => {
      this.thumbnails++;
      running = true;
      start();
    };
    this.waiting.add(launch);
    this.schedule();
    return () => {
      this.waiting.delete(launch);
      if (running) this.thumbnails--;
      running = false;
      this.schedule();
    };
  }

  private schedule() {
    if (this.inspections || this.thumbnails >= this.thumbnailLimit ||
        !this.waiting.size || this.frame !== undefined) return;
    this.frame = requestAnimationFrame(() => {
      this.frame = undefined;
      // A new inspection may have started since the frame was requested.
      if (this.inspections) return;
      for (const start of this.waiting) {
        this.waiting.delete(start);
        start();
        if (this.thumbnails >= this.thumbnailLimit) break;
      }
    });
  }

  ngOnDestroy() {
    if (this.frame !== undefined) cancelAnimationFrame(this.frame);
    this.observer?.disconnect();
    this.watched.clear();
    this.waiting.clear();
  }
}

interface Page {
  url: string;
  number: number;
  width: number;
  pdf: boolean;
  label: string;
  settled: (failed?: boolean) => void;
}

@Directive({ selector: "canvas[previewPage]", standalone: true })
class PreviewCanvas {
  previewPage = input.required<Page>();
  private readonly cache = inject(PdfCache);
  private readonly canvas = inject<ElementRef<HTMLCanvasElement>>(ElementRef).nativeElement;

  constructor() {
    effect(onCleanup => {
      const page = this.previewPage();
      const { url, number, width } = page;
      const canvas = this.canvas;
      const abort = new AbortController();
      let render: ReturnType<PreviewDocument["render"]> | undefined;
      onCleanup(() => {
        abort.abort();
        render?.cancel();
      });
      void this.cache.withDocument(url, abort.signal, async doc => {
        render = doc.render(number, width, canvas);
        try {
          await render.promise;
          if (!abort.signal.aborted) page.settled();
        } finally {
          render = undefined;
        }
      }).catch(() => {
        if (!abort.signal.aborted) page.settled(true);
      });
    });
  }
}

@Component({
  selector: "app-page-preview",
  standalone: true,
  imports: [PreviewCanvas],
  host: { "[class.thumb-preview]": "thumbnail()" },
  template: `
    @for (page of mounted(); track page) {
      @if (page.pdf) {
        <canvas [previewPage]="page" [attr.aria-label]="page.label" role="img"></canvas>
      } @else {
        <img [src]="page.url" [alt]="page.label"
             (load)="page.settled()" (error)="page.settled(true)" />
      }
    }
    @if (error()) {
      <span class="preview-error">Preview unavailable. Open the source file.</span>
    }
  `,
  styles: [`
    :host { display: block; position: relative; }
    :host(.thumb-preview) { height: 100%; }
    canvas, img { display: block; width: 100%; height: auto; object-fit: contain; }
    .preview-error { display: block; padding: 12px; font-size: .75rem; color: var(--app-error, #9b3030); }
  `],
})
export class PagePreviewComponent {
  url = input.required<string>();
  name = input.required<string>();
  pageNumber = input(1);
  width = input(900);
  thumbnail = input(false);
  readonly mounted = signal<Page[]>([]);
  readonly error = signal(false);
  private readonly visible = signal(false);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;
  private readonly schedule = inject(PreviewSchedule);

  constructor() {
    effect(onCleanup => {
      if (this.thumbnail())
        onCleanup(this.schedule.observe(this.host, value => this.visible.set(value)));
    });
    effect(onCleanup => {
      const url = this.url(), name = this.name(), number = this.pageNumber(), thumb = this.thumbnail();
      this.error.set(false);
      if (thumb && !this.visible()) return;

      let finish: () => void;
      let active = true;
      const pdf = /\.pdf(?:$|[?#])/i.test(name) || /\.pdf(?:$|[?#])/i.test(url);
      const page: Page = {
        url, number, pdf, width: thumb ? 160 : pdf ? this.width() : 0,
        label: `${name}, page ${number}`,
        settled: (failed = false) => {
          if (!active) return;
          this.error.set(failed);
          finish();
        },
      };
      // Key by request, not page number: a late or cancelled draw can only
      // touch its detached canvas, even across rapid selection or packet changes.
      const mount = () => this.mounted.set([page]);
      if (thumb) finish = this.schedule.thumbnailReady(mount);
      else {
        finish = this.schedule.inspectionStarted();
        mount();
      }
      onCleanup(() => {
        active = false;
        finish();
        this.mounted.set([]);
      });
    });
  }
}
