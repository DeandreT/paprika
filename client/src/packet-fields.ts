import { Component, OnChanges, OnDestroy, SimpleChanges, computed, inject, input, output, signal } from "@angular/core";
import { HttpClient, HttpErrorResponse } from "@angular/common/http";
import { DatePipe, JsonPipe } from "@angular/common";
import { FormsModule } from "@angular/forms";
import { MatButtonModule } from "@angular/material/button";
import { firstValueFrom } from "rxjs";
import { Packet, Page } from "./models";
import { apiError } from "./workflow-store";
import { Category, Evidence, ExtractionHistory, ExtractionRun, ReviewedField, correctionLabels, normalizedValue, pageHash, reviewDocuments, selectedSpan } from "./field-review";

interface Draft {
  packetId: string;
  revision: number;
  runId: string;
  documentId: string;
  field: ReviewedField;
}

@Component({
  selector: "app-packet-fields",
  standalone: true,
  imports: [DatePipe, JsonPipe, FormsModule, MatButtonModule],
  templateUrl: "./packet-fields.html",
  styleUrl: "./packet-fields.css",
})
export class PacketFieldsComponent implements OnChanges, OnDestroy {
  packet = input.required<Packet>();
  documentId = input("");
  includeAdHoc = input(false);
  refreshVersion = input(0);
  disabled = input(false);
  editingChanged = output<boolean>();
  saved = output<Packet>();
  reloadPacket = output<void>();

  readonly labels = correctionLabels;
  private http = inject(HttpClient);
  private generation = 0;
  history = signal<ExtractionHistory | null>(null);
  selectedRunId = signal("");
  loading = signal(false);
  saving = signal(false);
  error = signal("");
  conflict = signal(false);
  draft = signal<Draft | null>(null);
  category = signal<Category | "">("");
  note = signal("");
  value = signal("");
  pageId = signal("");
  span = signal<Omit<Evidence, "textSha256"> | null>(null);

  run = computed(() => {
    const history = this.history();
    if (!history) return undefined;
    const p = this.packet();
    const id = this.includeAdHoc() ? this.selectedRunId() : history.currentConfiguredRunId;
    return history.runs.find(run => run.id === id && run.packetId === p.id && (this.includeAdHoc() || run.packetRevision === p.revision));
  });
  documents = computed(() => {
    const run = this.run(), history = this.history();
    return history && run ? reviewDocuments(this.packet(), run, history, this.documentId()) : [];
  });
  currentRun = computed(() => {
    const run = this.run(), packet = this.packet();
    if (!run || run.packetRevision !== packet.revision || !["review", "approved"].includes(packet.status)) return false;
    if (!this.includeAdHoc()) return true;
    // An earlier extraction with the same definition and revision is historical.
    const matching = this.history()?.runs.filter(item => item.packetId === packet.id && item.packetRevision === packet.revision
      && item.definition.id === run.definition.id && item.definition.version === run.definition.version);
    return matching?.at(-1)?.id === run.id;
  });
  pages = computed(() => {
    const packet = this.packet();
    const ids = packet.documents.find(doc => doc.id === this.draft()?.documentId)?.pageIds ?? [];
    const pagesById = new Map(packet.pages.map(page => [page.id, page]));
    return ids.map(id => pagesById.get(id)).filter((page): page is Page => !!page);
  });
  page = computed(() => this.pages().find(page => page.id === this.pageId()));
  normalized = computed(() => {
    const draft = this.draft();
    const value = this.category() === "wrong-selection" ? this.span()?.text ?? "" : this.value();
    return draft && this.category() !== "omit" ? normalizedValue(value, draft.field.definition.kind) : null;
  });
  sameSelection = computed(() => {
    const prior = this.draft()?.field.final.evidence;
    const span = this.span();
    return span && prior?.length === 1 && prior[0].pageId === span.pageId
      && prior[0].offset === span.offset && prior[0].length === span.length;
  });
  ready = computed(() => {
    const category = this.category();
    if (!this.currentRun() || this.disabled() || this.saving() || this.conflict() || !this.draft()
        || !this.note().trim() || this.note().length > 1000 || !category) return false;
    if (category === "omit") return true;
    const value = this.normalized();
    return value !== null && value.length <= 10000 && (category !== "wrong-selection" || !this.sameSelection());
  });

  ngOnChanges(changes: SimpleChanges) {
    if (changes["packet"] || changes["refreshVersion"] || changes["includeAdHoc"]) {
      this.resetDraft();
      void this.load();
    } else if (changes["documentId"]) {
      this.resetDraft();
      this.error.set("");
    }
  }
  ngOnDestroy() {
    ++this.generation;
    this.resetDraft();
  }
  async load() {
    if (this.draft()) return;
    const token = ++this.generation;
    const packet = this.packet();
    this.history.set(null);
    this.loading.set(true);
    this.error.set("");
    try {
      const history = await firstValueFrom(this.http.get<ExtractionHistory>(`/api/extraction/${encodeURIComponent(packet.id)}`));
      if (token !== this.generation) return;
      this.history.set(history);
      this.selectedRunId.set(history.runs.at(-1)?.id ?? "");
    } catch (e) {
      if (token === this.generation) this.error.set(apiError(e, "Could not load extracted fields."));
    } finally {
      if (token === this.generation) this.loading.set(false);
    }
  }
  edit(documentId: string, field: ReviewedField) {
    const run = this.run();
    if (!run || !this.currentRun() || this.disabled() || this.draft()) return;
    this.error.set("");
    this.draft.set({ packetId: run.packetId, revision: run.packetRevision, runId: run.id, documentId, field });
    this.category.set("");
    this.note.set("");
    this.selectPage(field.final.evidence[0]?.pageId ?? this.pages()[0]?.id ?? "");
    this.editingChanged.emit(true);
  }
  setCategory(category: Category | "") {
    this.category.set(category);
    this.span.set(null);
    this.value.set(this.draft()?.field.final.value ?? "");
  }
  selectPage(id: string) {
    this.pageId.set(id);
    this.span.set(null);
  }
  selectText(page: Page, element: HTMLTextAreaElement) {
    if (!this.saving() && page.id === this.pageId())
      this.span.set(selectedSpan(page, element.selectionStart, element.selectionEnd));
  }
  location(evidence: Evidence) {
    const page = this.packet().pages.find(page => page.id === evidence.pageId);
    return `${page?.sourceName ?? evidence.pageId} · p. ${page?.pageNumber ?? "?"} · offset ${evidence.offset}, length ${evidence.length}`;
  }
  resetDraft() {
    if (this.draft()) {
      this.draft.set(null);
      this.editingChanged.emit(false);
    }
    this.saving.set(false);
    this.conflict.set(false);
    this.span.set(null);
  }
  cancel() {
    if (this.saving()) return;
    this.resetDraft();
    this.error.set("");
  }
  refreshPacket() {
    this.cancel();
    this.reloadPacket.emit();
  }
  private current(draft: Draft) {
    return this.draft() === draft && this.packet().id === draft.packetId && this.packet().revision === draft.revision
      && this.run()?.id === draft.runId && !this.disabled();
  }
  async save() {
    const draft = this.draft();
    if (!draft || !this.ready() || !this.current(draft)) return;
    const category = this.category();
    const value = this.normalized();
    const reason = this.note().trim();
    this.saving.set(true);
    this.error.set("");
    try {
      const evidence: Evidence[] = [];
      if (category === "wrong-selection") {
        const page = this.page(), span = this.span();
        if (!page || !span || page.id !== span.pageId) return;
        evidence.push({ ...span, textSha256: await pageHash(page.text) });
        if (!this.current(draft)) return;
      }
      const response = await firstValueFrom(this.http.post<{ packet: Packet }>(
        `/api/extraction/${encodeURIComponent(draft.packetId)}/corrections`,
        { runId: draft.runId, revision: draft.revision, documentId: draft.documentId, field: draft.field.definition.name,
          category, reason, value, evidence },
        { headers: { "X-paprika": "1" } },
      ));
      if (!this.current(draft) || response.packet.id !== draft.packetId || response.packet.revision !== draft.revision + 1) return;
      this.resetDraft();
      this.saved.emit(response.packet);
    } catch (e) {
      if (!this.current(draft)) return;
      this.conflict.set(e instanceof HttpErrorResponse && (e.status === 409 || e.status === 0));
      this.error.set(apiError(e, "Could not confirm the field correction."));
    } finally {
      if (this.draft() === draft) this.saving.set(false);
    }
  }
}
