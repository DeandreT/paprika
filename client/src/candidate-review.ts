import { DatePipe } from "@angular/common";
import { HttpClient, HttpErrorResponse } from "@angular/common/http";
import { Component, Input, OnChanges, OnDestroy, SimpleChanges, computed, inject, signal } from "@angular/core";
import { FormsModule } from "@angular/forms";
import { firstValueFrom } from "rxjs";
import { apiError } from "./workflow-store";
import type { TrainingDataset } from "./quality-panel";

type Comparison = "disagreement" | "agreement" | "single";
type Decision = "accepted" | "corrected" | "rejected";
type Status = "pending" | Decision;
interface Prediction {
  packetId: string;
  pageId: string;
  sourceId: string;
  revision: number | null;
  sourceName: string;
  pageNumber: number;
  proposedLabel: string;
  provider: string | null;
  model: string | null;
  requestedModel?: string | null;
  provenance: string;
  synthetic: boolean;
}
interface Candidate {
  id: string;
  version: string;
  comparison: Comparison;
  status: Status;
  label: string | null;
  reviewer: string | null;
  reviewedAt: string | null;
  text: string;
  labels: string[];
  predictions: Prediction[];
}
interface Review {
  id: string;
  version: string;
  decision: Decision;
  label?: string;
}
interface CandidateRow extends Candidate {
  proposals: string[];
  acceptable: string[];
  corrections: string[];
}

@Component({
  selector: "app-candidate-review", standalone: true, imports: [DatePipe, FormsModule],
  templateUrl: "./candidate-review.html", styleUrl: "./candidate-review.css",
})
export class CandidateReviewComponent implements OnChanges, OnDestroy {
  @Input({ required: true }) dataset!: TrainingDataset;
  @Input() packetId = "";
  private http = inject(HttpClient);
  private headers = { "X-paprika": "1" };
  private scope = 0;
  private expanded = false;
  readonly comparisonNames = { disagreement: "Disagreement", agreement: "Agreement", single: "Single prediction" };
  readonly maxBatch = 100;
  readonly pageSize = 20;
  candidates = signal<CandidateRow[]>([]);
  staged = signal(new Map<string, Review>());
  ready = signal(false);
  busy = signal(false);
  notice = signal("");
  error = signal("");
  comparison = signal<Comparison | "all">("all");
  status = signal<Status | "all">("pending");
  page = signal(0);
  sampleId = signal("");
  statusMatches = computed(() => this.candidates().filter(row => this.status() === "all" || row.status === this.status()));
  agreements = computed(() => this.statusMatches().filter(row => row.comparison === "agreement"));
  filtered = computed(() => this.statusMatches().filter(row =>
    (this.comparison() === "all" || row.comparison === this.comparison()) && (!this.sampleId() || row.id === this.sampleId())));
  visible = computed(() => this.filtered().slice(this.page() * this.pageSize, (this.page() + 1) * this.pageSize));
  needsLabel = computed(() => [...this.staged().values()].some(review => review.decision !== "rejected" && !review.label?.trim()));

  ngOnChanges(changes: SimpleChanges) {
    const datasetChange = changes["dataset"];
    if (!datasetChange || datasetChange.previousValue?.id === this.dataset.id) return;
    this.scope++;
    this.candidates.set([]); this.staged.set(new Map()); this.ready.set(false);
    this.notice.set(""); this.error.set(""); this.busy.set(false);
    this.comparison.set("all"); this.status.set("pending"); this.resetPage();
    if (this.expanded) void this.reload();
  }
  ngOnDestroy() { this.scope++; }
  opened(event: Event) {
    this.expanded = (event.target as HTMLDetailsElement).open;
    if (this.expanded) void this.reload();
    else {
      this.ready.set(false); this.candidates.set([]);
      this.notice.set(""); this.error.set("");
    }
  }
  resetPage() { this.page.set(0); this.sampleId.set(""); }
  sourcePage(prediction: Prediction) {
    return `/api/packets/${encodeURIComponent(prediction.packetId)}/sources/${encodeURIComponent(prediction.sourceId)}#page=${prediction.pageNumber}`;
  }
  sample() {
    const pool = this.agreements();
    if (!pool.length) return;
    const alternatives = pool.length > 1 ? pool.filter(row => row.id !== this.sampleId()) : pool;
    this.comparison.set("agreement"); this.page.set(0);
    this.sampleId.set(alternatives[Math.floor(Math.random() * alternatives.length)].id);
  }

  stage(row: CandidateRow, decision: Decision | "") {
    const next = new Map(this.staged());
    if (!decision) next.delete(row.id);
    else {
      if (!next.has(row.id) && next.size >= this.maxBatch) return;
      const review: Review = { id: row.id, version: row.version, decision };
      if (decision === "accepted" && row.proposals.length === 1 && row.acceptable.length === 1) review.label = row.acceptable[0];
      if (decision === "corrected" && row.label && row.corrections.includes(row.label)) review.label = row.label;
      next.set(row.id, review);
    }
    this.staged.set(next);
  }
  setLabel(row: Candidate, label: string) {
    const review = this.staged().get(row.id);
    if (review) this.staged.set(new Map(this.staged()).set(row.id, { ...review, label }));
  }

  private async run(action: (base: string, scope: number) => Promise<void>, reviewing = false) {
    if (this.busy()) return;
    const scope = this.scope;
    this.busy.set(true); this.error.set(""); this.notice.set("");
    try { await action(`/api/training/datasets/${encodeURIComponent(this.dataset.id)}`, scope); }
    catch (e) {
      if (scope !== this.scope) return;
      if (reviewing && e instanceof HttpErrorResponse && e.status === 409) {
        this.ready.set(false);
        this.error.set("A candidate changed. Reload candidates before saving. Check any decision whose source or review changed.");
      } else if (e instanceof HttpErrorResponse && [401, 403, 404].includes(e.status)) {
        this.ready.set(false); this.candidates.set([]); this.staged.set(new Map());
        this.error.set("The selected data is unavailable. Check your access and reload candidates.");
      } else {
        this.error.set(apiError(e, "Request failed."));
      }
    } finally {
      if (scope === this.scope) {
        this.busy.set(false);
        if (!this.expanded) { this.ready.set(false); this.candidates.set([]); }
      }
    }
  }
  private async readCandidates(base: string, scope: number) {
    this.ready.set(false);
    const candidates = await firstValueFrom(this.http.get<Candidate[]>(`${base}/candidates`));
    if (scope !== this.scope) return;
    const rows = candidates.map(row => {
      const proposals = [...new Set(row.predictions.map(p => p.proposedLabel))];
      return { ...row, proposals, acceptable: row.labels.filter(label => proposals.includes(label)),
        corrections: row.labels.filter(label => !proposals.includes(label)) };
    });
    const retained = new Map<string, Review>();
    for (const row of rows) {
      const draft = this.staged().get(row.id);
      if (draft && draft.version === row.version) retained.set(row.id, draft);
    }
    const changed = this.staged().size - retained.size;
    if (changed) this.notice.update(message => `${message} Staged decisions removed because the evidence or review changed: ${changed}.`.trim());
    this.staged.set(retained); this.candidates.set(rows); this.resetPage(); this.ready.set(true);
  }
  reload() { return this.run((base, scope) => this.readCandidates(base, scope)); }
  importPacket() {
    const packetId = this.packetId;
    if (!packetId || !this.ready()) return;
    return this.run(async (base, scope) => {
      const result = await firstValueFrom(this.http.post<{ imported: number }>(
        `${base}/candidates/import`, { packetIds: [packetId] }, { headers: this.headers }));
      if (scope !== this.scope) return;
      this.notice.set(`New predictions imported: ${result.imported}.`);
      await this.readCandidates(base, scope);
    });
  }
  save() {
    const decisions = [...this.staged().values()].map(review =>
      review.label ? { ...review, label: review.label.trim() } : review);
    if (!this.ready() || !decisions.length || this.needsLabel()) return;
    return this.run(async (base, scope) => {
      const result = await firstValueFrom(this.http.post<{ reviewed: number }>(
        `${base}/candidates/review`, { decisions }, { headers: this.headers }));
      if (scope !== this.scope) return;
      this.staged.set(new Map()); this.notice.set(`Review decisions saved: ${result.reviewed}.`);
      await this.readCandidates(base, scope);
    }, true);
  }
  exportReviewed() {
    if (!this.ready()) return;
    return this.run(async (base, scope) => {
      const file = await firstValueFrom(this.http.get(`${base}/export`, { responseType: "blob" }));
      if (scope !== this.scope) return;
      const url = URL.createObjectURL(file);
      const link = document.createElement("a");
      link.href = url; link.download = `paprika-${this.dataset.id}-reviewed.json`;
      link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
      this.notice.set("Downloaded reviewed data with training text.");
    });
  }
}
