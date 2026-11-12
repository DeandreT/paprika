import { Component, DestroyRef, ElementRef, computed, inject, input, output, signal, viewChild } from "@angular/core";
import { HttpClient, HttpErrorResponse, HttpHeaders } from "@angular/common/http";
import { FormsModule } from "@angular/forms";
import { MatButtonModule } from "@angular/material/button";
import { firstValueFrom } from "rxjs";
import { CatalogEntry, CatalogKind, Config, Packet, VersionRef } from "./models";
import { WorkflowStore, apiError, normalizeEntry, refKey, splitPolicy } from "./workflow-store";
import { SplitEditorComponent } from "./split-editor";
import { CapabilityEditorComponent } from "./capability-editor";

@Component({
  selector: "app-workflow-manager",
  standalone: true,
  imports: [FormsModule, MatButtonModule, SplitEditorComponent, CapabilityEditorComponent],
  templateUrl: "./workflow-manager.html",
})
export class WorkflowManagerComponent {
  store = inject(WorkflowStore);
  private http = inject(HttpClient);
  private headers = new HttpHeaders({ "X-PaperworkClassifier": "1" });
  config = input<Config | null>(null);
  reviewBusy = input(false);
  reviewDirty = input(false);
  packetCreated = output<Packet>();
  dirtyChanged = output<boolean>();
  tab = signal<CatalogKind>("workflow");
  editor = signal<CatalogEntry | null>(null);
  isNew = signal(false);
  dirty = signal(false);
  busy = signal(false);
  error = signal("");
  notice = signal("");
  advanced = signal(false);
  step = signal(0);
  readonly steps = ["Basics", "Documents", "Review rules", "Finish"];
  private stepHeading = viewChild<ElementRef<HTMLElement>>("stepHeading");
  guided = computed(() => !!this.editor()?.workflow && !this.advanced());
  atFinish = computed(() => !this.guided() || this.step() === this.steps.length - 1);
  stepIssues = computed(() => this.issuesForStep(this.step()));
  configuredTools = computed(() => {
    const capabilities = this.editor()?.workflow?.capabilities;
    const labels: Record<string, string> = { quality: "Quality checks", training: "Trained classifier", reference: "Reference verification", operations: "Review tasks", extraction: "Field extraction", recognition: "Visual matching", preprocessing: "Image preparation", case: "Cases", flow: "Processing flow", output: "Additional output" };
    return Object.entries(capabilities ?? {}).filter(([, value]) => !!value).map(([key]) => labels[key] ?? key);
  });
  cloneName = "";
  keywords = "";
  files = signal<File[]>([]);
  pendingPreview = signal<{ files: File[]; entry: CatalogEntry; key: string } | null>(null);
  identifiers = ["load", "shipment", "order", "bol", "receipt", "mc", "dot", "trailer", "container", "chassis"];
  refKey = refKey;
  entries = computed(() => this.store.catalog().entries.filter(e => e.kind === this.tab()));
  typeIds = computed(() => [...new Set(this.store.catalog().entries.filter(e => e.kind === "type").map(e => e.id))]);
  releases = computed(() => this.store.catalog().entries.filter(e => e.kind === "release"));
  locked = computed(() => this.busy() || !!this.pendingPreview() || this.editor()?.status !== "draft");
  selectedRelease = computed(() => this.releases().find(e => refKey(e) === refKey(this.editor()?.workflow?.release)));
  selectedTypes = computed(() => (this.selectedRelease()?.release?.types ?? []).map(ref => this.store.catalog().entries.find(e => e.kind === "type" && refKey(e) === refKey(ref))).filter((e): e is CatalogEntry => !!e));
  publicationIssues = computed(() => {
    const e = this.editor();
    if (!e) return [];
    const issues: string[] = [];
    if (!e.name.trim()) issues.push("Enter a name.");
    if (e.release) {
      if (!e.release.types.some(t => t.id === "unknown")) issues.push("Include an exact version of Unknown.");
      for (const ref of e.release.types) {
        if (!this.store.catalog().entries.some(t => t.kind === "type" && refKey(t) === refKey(ref) && t.status === "published")) issues.push(`Publish document type ${refKey(ref)} before publishing this release.`);
      }
    }
    if (e.workflow) {
      if (!e.workflow.model.trim()) issues.push("Enter the approved model for the selected mode.");
      if (!this.selectedRelease()) issues.push("Choose a document-type release.");
      else if (this.selectedRelease()?.status !== "published") issues.push("Publish the selected release before publishing this workflow.");
      if (this.selectedTypes().some(t => t.status !== "published")) issues.push("Publish all referenced document type versions before publishing this workflow.");
    }
    return issues;
  });
  stats = computed(() => this.store.catalog().stats.find(s => refKey(s) === refKey(this.editor())));
  constructor() {
    const beforeUnload = (event: BeforeUnloadEvent) => { if (this.dirty() || this.pendingPreview()) event.preventDefault(); };
    window.addEventListener("beforeunload", beforeUnload);
    inject(DestroyRef).onDestroy(() => window.removeEventListener("beforeunload", beforeUnload));
  }
  markDirty() { this.editor.update(entry => entry ? { ...entry } : null); this.dirty.set(true); this.dirtyChanged.emit(true); this.notice.set(""); }
  private issuesForStep(step: number): string[] {
    const entry = this.editor();
    if (!entry?.workflow) return [];
    if (step === 0) {
      const issues = entry.name.trim() ? [] : ["Enter a workflow name."];
      if (entry.id && !/^[a-z0-9][a-z0-9_-]{0,79}$/.test(entry.id)) issues.push("Use lowercase letters, numbers, hyphens or underscores for the ID.");
      return issues;
    }
    if (step === 1) {
      const issues = this.selectedRelease() ? [] : ["Choose a document type release."];
      if (!entry.workflow.model.trim()) issues.push("An approved model is required for this mode.");
      return issues;
    }
    if (step === 2) {
      const split = entry.workflow.defaultSplit;
      if (split.mode === "fixed" && (!Number.isInteger(split.pageCount) || split.pageCount < 1 || split.pageCount > 10000)) return ["Enter a page count from 1 to 10,000."];
      if (["first", "last", "identifier"].includes(split.mode) && !split.pattern.trim()) return ["Enter a marker pattern."];
    }
    return [];
  }
  goToStep(target: number) {
    if (this.busy() || this.pendingPreview() || target < 0 || target >= this.steps.length) return;
    if (target > this.step()) {
      for (let i = 0; i < target; i++) {
        if (this.issuesForStep(i).length) { this.step.set(i); this.stepHeading()?.nativeElement.focus(); return; }
      }
    }
    this.step.set(target);
    this.stepHeading()?.nativeElement.focus();
  }
  setAdvanced(value: boolean) {
    if (this.busy() || this.pendingPreview()) return;
    this.advanced.set(value);
  }
  backToCatalog() {
    if (!this.canLeave()) return;
    this.editor.set(null); this.resetDirty(); this.error.set(""); this.notice.set("");
  }
  private resetDirty() { this.dirty.set(false); this.dirtyChanged.emit(false); }
  private canLeave() {
    if (this.busy() || this.pendingPreview()) return false;
    return !this.dirty() || window.confirm("Discard unsaved catalog changes?");
  }
  selectTab(kind: CatalogKind) {
    if (!this.canLeave()) return;
    this.tab.set(kind); this.editor.set(null); this.resetDirty(); this.error.set(""); this.notice.set("");
  }
  private accept(entry: CatalogEntry, resetStep = true) {
    this.editor.set(normalizeEntry(entry)); this.isNew.set(false); this.resetDirty();
    if (resetStep) this.step.set(entry.status === "draft" ? 0 : this.steps.length - 1);
    this.keywords = (entry.documentType?.keywords ?? []).join("\n");
    this.cloneName = `${entry.name} copy`;
    this.files.set([]);
  }
  select(entry: CatalogEntry) { if (this.canLeave()) { this.accept(entry); this.error.set(""); this.notice.set(""); } }
  create() {
    if (!this.canLeave()) return;
    const kind = this.tab();
    const entry: CatalogEntry = { kind, id: "", version: 1, revision: 0, status: "draft", name: "", description: "" };
    if (kind === "type") entry.documentType = { keywords: [], instructions: "", split: splitPolicy("inherit") };
    if (kind === "release") {
      const unknown = this.store.catalog().entries.filter(e => e.kind === "type" && e.id === "unknown" && e.status === "published").sort((a, b) => b.version - a.version)[0];
      entry.release = { types: unknown ? [{ id: unknown.id, version: unknown.version }] : [] };
    }
    if (kind === "workflow") entry.workflow = { release: this.store.defaultEntry()?.workflow?.release ?? { id: "", version: 1 }, mode: "simulation", providerProfile: "default", model: "local-simulation-1", instructions: "", defaultSplit: splitPolicy(), identifierKinds: [...this.identifiers], requiredIdentifierKinds: [] };
    this.accept(entry); this.isNew.set(true); this.markDirty(); this.error.set("");
  }
  typeVersions(id: string) { return this.store.catalog().entries.filter(e => e.kind === "type" && e.id === id); }
  typeSelection(id: string) { return refKey(this.editor()?.release?.types.find(t => t.id === id)); }
  chooseType(id: string, key: string) {
    const entry = this.editor(); if (!entry?.release) return;
    const type = this.typeVersions(id).find(t => refKey(t) === key);
    entry.release.types = entry.release.types.filter(t => t.id !== id);
    if (type) entry.release.types.push({ id: type.id, version: type.version });
    this.editor.set({ ...entry }); this.markDirty();
  }
  chooseRelease(key: string) {
    const entry = this.editor(); const release = this.releases().find(r => refKey(r) === key);
    if (!entry?.workflow) return;
    entry.workflow.release = release ? { id: release.id, version: release.version } : { id: "", version: 1 };
    this.editor.set({ ...entry }); this.markDirty();
  }
  chooseMode(mode: "simulation" | "live") {
    const workflow = this.editor()?.workflow;
    if (!workflow) return;
    workflow.mode = mode;
    workflow.model = mode === "simulation" ? "local-simulation-1" : this.store.catalog().liveModel;
    this.markDirty();
  }
  toggleIdentifier(kind: string, required: boolean, checked: boolean) {
    const workflow = this.editor()?.workflow; if (!workflow) return;
    const field = required ? "requiredIdentifierKinds" : "identifierKinds";
    workflow[field] = checked ? [...new Set([...workflow[field], kind])] : workflow[field].filter(k => k !== kind);
    if (required && checked) workflow.identifierKinds = [...new Set([...workflow.identifierKinds, kind])];
    if (!required && !checked) workflow.requiredIdentifierKinds = workflow.requiredIdentifierKinds.filter(k => k !== kind);
    this.markDirty();
  }
  private url(entry: CatalogEntry) { return `/api/workflows/catalog/${entry.kind}/${encodeURIComponent(entry.id)}/${entry.version}`; }
  async save() {
    const entry = this.editor(); if (!entry || this.locked()) return;
    const body = structuredClone(entry);
    if (!body.name.trim()) { this.error.set("Enter a name before saving."); return; }
    if (body.documentType) body.documentType.keywords = this.keywords.split(/\r?\n/).map(k => k.trim()).filter(Boolean);
    if (this.isNew() && !body.id.trim()) delete (body as Partial<CatalogEntry>).id;
    this.busy.set(true); this.error.set("");
    try {
      const saved = await firstValueFrom(this.isNew()
        ? this.http.post<CatalogEntry>("/api/workflows/catalog", body, { headers: this.headers })
        : this.http.put<CatalogEntry>(this.url(entry), body, { headers: this.headers }));
      this.accept(saved, false); this.notice.set("Draft saved."); await this.store.load();
    } catch (e) { this.error.set(apiError(e, "Could not save. Your edits are preserved. If this version changed, reload it before saving again.")); }
    finally { this.busy.set(false); }
  }
  async action(action: "publish" | "archive" | "versions" | "clone") {
    const entry = this.editor(); if (!entry || this.busy() || this.isNew() || this.dirty() || this.pendingPreview()) return;
    if (action === "clone" && !this.cloneName.trim()) { this.error.set("Enter a clone name."); return; }
    this.busy.set(true); this.error.set("");
    try {
      const saved = await firstValueFrom(this.http.post<CatalogEntry>(`${this.url(entry)}/${action}`, { revision: entry.revision, ...(action === "clone" ? { name: this.cloneName.trim() } : {}) }, { headers: this.headers }));
      this.accept(saved); this.notice.set(action === "versions" ? "New draft version created." : action === "clone" ? "Draft cloned." : action === "publish" ? "Version published." : "Version archived.");
      await this.store.load();
    } catch (e) { this.error.set(apiError(e, "The catalog action failed. Reload the version if it changed in another session.")); }
    finally { this.busy.set(false); }
  }
  async makeDefault() {
    const entry = this.editor(); if (!entry || this.busy() || entry.status !== "published") return;
    this.busy.set(true); this.error.set("");
    try { await firstValueFrom(this.http.put<VersionRef>("/api/workflows/default", { id: entry.id, version: entry.version }, { headers: this.headers })); await this.store.load(); this.notice.set("Default workflow updated."); }
    catch (e) { this.error.set(apiError(e, "Could not change the default workflow.")); }
    finally { this.busy.set(false); }
  }
  async reload() {
    if (!this.canLeave()) return;
    await this.store.load();
    const current = this.editor();
    if (current && !this.store.error()) {
      const entry = this.store.catalog().entries.find(e => e.kind === current.kind && refKey(e) === refKey(current));
      if (entry) this.accept(entry);
    }
  }
  chooseFiles(event: Event) { const input = event.target as HTMLInputElement; this.files.set(Array.from(input.files ?? [])); input.value = ""; }
  async preview() {
    const entry = this.editor();
    if (!entry?.workflow || entry.status !== "draft" || entry.workflow.mode !== "simulation" || this.dirty() || this.isNew() || this.reviewDirty() || this.reviewBusy() || this.busy()) return;
    if (!this.pendingPreview()) {
      const files = this.files();
      if (!files.length || files.some(f => !/\.(pdf|png|jpe?g)$/i.test(f.name))) { this.error.set("Choose PDF, PNG, or JPG sample files."); return; }
      if (files.reduce((n, f) => n + f.size, 0) > Math.min(this.config()?.maxUploadMb ?? 25, 25) * 1024 * 1024) { this.error.set("The sample exceeds the upload limit. Choose smaller files."); return; }
      this.pendingPreview.set({ files: [...files], entry: structuredClone(entry), key: crypto.randomUUID() });
    }
    const submission = this.pendingPreview()!;
    const data = new FormData(); submission.files.forEach(f => data.append("files", f));
    data.append("workflowId", submission.entry.id); data.append("workflowVersion", String(submission.entry.version));
    data.append("workflowRevision", String(submission.entry.revision)); data.append("testRun", "true");
    this.busy.set(true); this.error.set("");
    try {
      const packet = await firstValueFrom(this.http.post<Packet>("/api/packets", data, { headers: this.headers.set("Idempotency-Key", submission.key) }));
      this.pendingPreview.set(null); this.files.set([]); this.notice.set("Sample preview created. Opened in packet review."); this.packetCreated.emit(packet); await this.store.load();
    } catch (e) {
      const uncertain = e instanceof HttpErrorResponse && (e.status === 0 || e.status >= 500);
      if (!uncertain) this.pendingPreview.set(null);
      this.error.set(apiError(e, uncertain ? "Preview result is uncertain. Retry sample preview uses the original saved revision, files, and submission key." : "Could not preview this saved workflow."));
    } finally { this.busy.set(false); }
  }
}
