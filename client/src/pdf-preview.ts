import {
  Component,
  ElementRef,
  Injectable,
  input,
  effect,
  inject,
  signal,
  OnDestroy,
  viewChild,
} from "@angular/core";
import {
  getDocument,
  GlobalWorkerOptions,
  PDFDocumentProxy,
  PDFDocumentLoadingTask,
  RenderTask,
} from "pdfjs-dist";
GlobalWorkerOptions.workerSrc = "/assets/pdf.worker.min.mjs";

@Injectable({ providedIn: "root" })
export class PdfCache implements OnDestroy {
  private documents = new Map<string, PDFDocumentLoadingTask>();
  get(url: string): Promise<PDFDocumentProxy> {
    let task = this.documents.get(url);
    if (!task) {
      task = getDocument(url);
      this.documents.set(url, task);
    }
    return task.promise.catch((error) => {
      this.documents.delete(url);
      throw error;
    });
  }
  ngOnDestroy() {
    for (const task of this.documents.values()) void task.destroy();
  }
}

@Component({
  selector: "app-page-preview",
  standalone: true,
  template: `
    @if (isPdf()) {
      <canvas
        #canvas
        [attr.aria-label]="'Page ' + pageNumber() + ' of ' + name()"
        role="img"
      ></canvas>
    } @else {
      <img
        [src]="url()"
        [alt]="name() + ', page ' + pageNumber()"
        (error)="error.set('Preview unavailable. Open the source file.')"
        loading="lazy"
      />
    }
    @if (error()) {
      <span class="preview-error">{{ error() }}</span>
    }
  `,
  styles: [
    `
      :host {
        display: block;
        position: relative;
      }
      canvas,
      img {
        display: block;
        width: 100%;
        height: auto;
        object-fit: contain;
      }
      .preview-error {
        display: block;
        padding: 12px;
        font-size: 0.75rem;
        color: var(--app-error, #9b3030);
      }
    `,
  ],
})
export class PagePreviewComponent {
  url = input.required<string>();
  name = input.required<string>();
  pageNumber = input(1);
  thumbnail = input(false);
  canvas = viewChild<ElementRef<HTMLCanvasElement>>("canvas");
  error = signal("");
  private cache = inject(PdfCache);
  isPdf = () =>
    /\.pdf(?:$|\?)/i.test(this.name()) || /\.pdf(?:$|\?)/i.test(this.url());
  constructor() {
    effect((onCleanup) => {
      const url = this.url(),
        number = this.pageNumber(),
        canvas = this.canvas()?.nativeElement,
        thumb = this.thumbnail();
      this.error.set("");
      if (!this.isPdf() || !canvas) return;
      let cancelled = false;
      let render: RenderTask | undefined;
      onCleanup(() => {
        cancelled = true;
        render?.cancel();
      });
      void (async () => {
        try {
          const pdf = await this.cache.get(url);
          if (cancelled) return;
          const page = await pdf.getPage(number);
          if (cancelled) return;
          const initial = page.getViewport({ scale: 1 });
          const viewport = page.getViewport({
            scale: (thumb ? 160 : 900) / initial.width,
          });
          canvas.width = viewport.width;
          canvas.height = viewport.height;
          render = page.render({ canvas, viewport });
          await render.promise;
        } catch (e) {
          if (!cancelled)
            this.error.set("Preview unavailable. Open the source file.");
        }
      })();
    });
  }
}
