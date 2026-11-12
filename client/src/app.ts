import { Component, DestroyRef, ElementRef, computed, effect, inject, signal, untracked, viewChild } from "@angular/core";
import {
  HttpClient,
  HttpErrorResponse,
  HttpHeaders,
} from "@angular/common/http";
import { FormsModule } from "@angular/forms";
import { DatePipe, PercentPipe } from "@angular/common";
import { MatButtonModule } from "@angular/material/button";
import { MatProgressBarModule } from "@angular/material/progress-bar";
import { MatMenuModule } from "@angular/material/menu";
import { CdkDragDrop, DragDropModule } from "@angular/cdk/drag-drop";
import { firstValueFrom } from "rxjs";
import { Config, Packet, Document, PacketRevision, VersionRef } from "./models";
import { PagePreviewComponent } from "./pdf-preview";
import { DeliveryComponent } from "./delivery";
import { OperationsComponent } from "./operations";
import { WorkflowStore, refKey, splitPolicy } from "./workflow-store";
import { WorkflowManagerComponent } from "./workflow-manager";
import { QualityPanelComponent } from "./quality-panel";
import { ReferencePanelComponent } from "./reference-panel";
import { OperationsPanelComponent } from "./operations-panel";
import { ExtractionPanelComponent } from "./extraction-panel";
import { CasePanelComponent } from "./case-panel";
import { FlowPanelComponent } from "./flow-panel";
import { ThemeService } from "./theme";
import { ClassificationWizardComponent } from "./classification-wizard";

export interface ShipmentCheck { documentId: string; revision: number; reference: string; status: string; matches: { recordId: string; label: string }[]; candidateReferences: string[] | null; }

@Component({
  selector: "app-root",
  standalone: true,
  imports: [
    FormsModule,
    DatePipe,
    PercentPipe,
    MatButtonModule,
    MatProgressBarModule,
    MatMenuModule,
    DragDropModule,
    PagePreviewComponent,
    DeliveryComponent,
    OperationsComponent,
    WorkflowManagerComponent,
    QualityPanelComponent,
    ReferencePanelComponent,
    OperationsPanelComponent,
    ExtractionPanelComponent,
    CasePanelComponent,
    FlowPanelComponent,
    ClassificationWizardComponent,
  ],
  templateUrl: "./app.html",
})
export class AppComponent {
  theme = inject(ThemeService);
  classificationAdvanced = signal(false);
  classificationStage = signal<"upload" | "review" | "approve" | "export">("upload");
  private uploadInput = viewChild<ElementRef<HTMLInputElement>>("filePicker");
  chooseUpload() { this.uploadInput()?.nativeElement.click(); }
  showClassification(advanced: boolean) {
    if (this.busy()) return;
    this.managerOpen.set(false);
    this.setClassificationAdvanced(advanced);
  }
  togglePlatform() {
    if (this.busy()) return;
    this.showClassification(true);
    this.platformOpen.set(!this.platformOpen());
  }
  setClassificationAdvanced(value: boolean) {
    if (this.busy()) return;
    this.classificationAdvanced.set(value);
    if (!value) {
      this.platformOpen.set(false);
      if (!this.packet()) this.classificationStage.set("upload");
      else if (this.packet()?.status === "approved" && !this.dirty()) this.classificationStage.set("export");
      else if (this.classificationStage() === "upload" || this.classificationStage() === "export") this.classificationStage.set("review");
    }
  }
  newClassification() {
    if (this.busy() || this.pendingUpload() || !this.mayDiscard()) return;
    ++this.selectionToken;
    clearTimeout(this.timer);
    this.packet.set(null); this.documents.set([]); this.selectedId.set("");
    this.dirty.set(false); this.stale.set(false); this.error.set(""); this.notice.set("");
    this.historyOpen.set(false); this.history.set([]);
    this.classificationStage.set("upload");
  }
  selectDocument(index: number) {
    const doc = this.documents()[index];
    if (!doc || this.busy()) return;
    this.selectedId.set(doc.pageIds[0]);
    this.classificationStage.set("review");
  }
  workflows = inject(WorkflowStore);
  managerOpen = signal(false);
  platformOpen = signal(false);
  platformTab = signal("team");
  platformTabs = [
    { id: "team", name: "Team access and tasks" },
    { id: "quality", name: "Quality and training" },
    { id: "references", name: "References and connections" },
    { id: "extraction", name: "Extraction and images" },
    { id: "cases", name: "Cases" },
    { id: "flows", name: "Processing flows" },
  ];
  async platformRefreshed() {
    if (this.dirty() || this.busy()) return;
    const id = this.packet()?.id;
    await this.initialize();
    if (id) await this.open(id);
  }
  catalogDirty = signal(false);
  workflowSelection = signal("");
  reprocessSelection = signal("");
  workflowFilter = signal("all");
  refKey = refKey;
  selectedWorkflow = computed(() => this.workflowSelection()
    ? this.workflows.published().find(w => refKey(w) === this.workflowSelection())
    : this.workflows.defaultEntry());
  shipmentRequired = computed(() => !!this.packet()?.workflow?.requiredIdentifierKinds.includes("shipment"));
  referenceLabel = computed(() => this.shipmentRequired() ? "Shipment ID" : "Business reference");
  referenceHint = computed(() => this.shipmentRequired() ? "Required – shipment number" : "Optional reference");
  referenceResults = signal<ShipmentCheck[]>([]);
  checkingShipment = signal(false);
  private referenceKey = computed(() => { const p = this.packet(); return p?.workflow?.capabilities?.reference ? p.id + "@" + p.revision : ""; });
  selectedReference = computed(() => {
    const p = this.packet(), d = this.selectedDoc();
    return this.referenceResults().find(r => r.revision === p?.revision && r.documentId === d?.id && r.reference === (d?.businessReference ?? "").trim());
  });
  missingShipments = computed(() => this.shipmentRequired() ? this.documents().filter(d => d.type !== "unknown" && !d.businessReference.trim()) : []);
  reviewTypes = computed(() => {
    const types = this.packet()?.workflow?.types;
    return Array.isArray(types) ? types.map(t => ({ id: t.id, label: t.name })) : this.config()?.documentTypes ?? [];
  });
  inboxWorkflows = computed(() => [...new Map(this.packets().filter(p => p.workflow).map(p => [refKey(p.workflow), p.workflow!])).values()]);
  private http = inject(HttpClient);
  private destroy = inject(DestroyRef);
  private headers = new HttpHeaders({ "X-PaperworkClassifier": "1" });
  private timer?: ReturnType<typeof setTimeout>;
  private disposed = false;
  private selectionToken = 0;
  config = signal<Config | null>(null);
  packets = signal<Packet[]>([]);
  packet = signal<Packet | null>(null);
  documents = signal<Document[]>([]);
  selectedId = signal("");
  dirty = signal(false);
  busy = signal(false);
  error = signal("");
  notice = signal("");
  stale = signal(false);
  loading = signal(true);
  inboxSearch = signal("");
  statusFilter = signal("all");
  modeFilter = signal("all");
  statuses = [
    "queued",
    "processing",
    "review",
    "approved",
    "failed",
    "cancelled",
  ];
  pendingUpload = signal<{ files: File[]; key: string; workflow: VersionRef | null; workflowLabel: string } | null>(null);
  uploadUncertain = signal(false);
  identifierCandidates = computed(() => {
    const pages = this.selectedDoc()?.pageIds ?? [];
    return (this.packet()?.identifierCandidates ?? []).filter((candidate) =>
      candidate.pageIds.some((id) => pages.includes(id)),
    );
  });
  derivedBusinessGroups = computed(() => {
    const groups = new Map<string, Document[]>();
    for (const document of this.documents()) {
      const reference = document.businessReference.trim();
      groups.set(reference, [...(groups.get(reference) ?? []), document]);
    }
    return [...groups].map(([reference, documents]) => ({
      reference,
      documents,
    }));
  });
  modes = ["simulation", "demo", "live"];
  visiblePackets = computed(() => {
    const search = this.inboxSearch().trim().toLowerCase();
    return this.packets().filter(
      (p) =>
        (this.statusFilter() === "all" || p.status === this.statusFilter()) &&
        (this.modeFilter() === "all" || p.mode === this.modeFilter()) &&
        (this.workflowFilter() === "all" || refKey(p.workflow) === this.workflowFilter()) &&
        (!search ||
          p.name.toLowerCase().includes(search) ||
          p.documents.some((d) =>
            d.businessReference.toLowerCase().includes(search),
          )),
    );
  });
  historyOpen = signal(false);
  historyLoading = signal(false);
  historyError = signal("");
  history = signal<PacketRevision[]>([]);
  private historyToken = 0;
  selected = computed(() =>
    this.packet()?.pages.find((p) => p.id === this.selectedId()),
  );
  selectedDoc = computed(() =>
    this.documents().find((d) => d.pageIds.includes(this.selectedId())),
  );
  uploadReady = computed(
    () =>
      !!this.selectedWorkflow() && (this.selectedWorkflow()?.workflow?.mode === "simulation" ||
      (!!this.config()?.ocrConfigured &&
        !!this.config()?.classifierConfigured &&
        this.selectedWorkflow()?.workflow?.mode === "live")),
  );
  editable = computed(
    () =>
      !!this.packet() &&
      ["review", "approved"].includes(this.packet()!.status) &&
      !this.busy() &&
      !this.stale(),
  );
  constructor() {
    void this.initialize();
    effect(() => { const key = this.referenceKey(); untracked(() => void this.loadReferences(key)); });
    const beforeUnload = (e: BeforeUnloadEvent) => {
      if (this.dirty() || this.catalogDirty()) {
        e.preventDefault();
      }
    };
    window.addEventListener("beforeunload", beforeUnload);
    this.destroy.onDestroy(() => {
      this.disposed = true;
      clearTimeout(this.timer);
      window.removeEventListener("beforeunload", beforeUnload);
    });
  }
  async initialize() {
    this.loading.set(true);
    this.error.set("");
    try {
      const [config, packets] = await Promise.all([
        firstValueFrom(this.http.get<Config>("/api/config")),
        firstValueFrom(this.http.get<Packet[]>("/api/packets")),
      ]);
      this.config.set(config);
      this.packets.set(Array.isArray(packets) ? packets.map(p => this.normalizePacket(p)) : []);
      await this.workflows.load();
    } catch (e) {
      if (e instanceof HttpErrorResponse && (e.status === 401 || e.status === 403)) {
        this.config.set(null);
        this.packets.set([]);
        this.packet.set(null);
        this.documents.set([]);
        this.selectedId.set("");
        this.history.set([]);
        this.platformOpen.set(true);
        this.classificationAdvanced.set(true);
        this.platformTab.set("team");
      }
      this.fail(
        e,
        e instanceof HttpErrorResponse && e.status === 401
          ? "Sign in through Platform tools to access shared review."
          : "Could not connect to the API. Check the backend and your access, then reconnect.",
      );
    } finally {
      this.loading.set(false);
    }
  }
  label(type: string) {
    return (
      this.reviewTypes().find((t) => t.id === type)?.label ?? type
    );
  }
  page(id: string) {
    return this.packet()?.pages.find((p) => p.id === id);
  }
  private accept(p: Packet) {
    p = this.normalizePacket(p);
    if (this.packet()?.id !== p.id) {
      this.classificationStage.set(p.status === "approved" ? "export" : "review");
      this.reprocessSelection.set("");
      ++this.historyToken;
      this.historyOpen.set(false);
      this.historyLoading.set(false);
      this.historyError.set("");
      this.history.set([]);
    }
    this.packet.set(p);
    if (p.status === "approved") this.classificationStage.set("export");
    else if (this.classificationStage() === "export") this.classificationStage.set("review");
    this.documents.set(structuredClone(p.documents));
    this.dirty.set(false);
    this.stale.set(false);
    if (!p.pages.some((x) => x.id === this.selectedId()))
      this.selectedId.set(p.pages[0]?.id ?? "");
    this.upsert(p);
    this.poll();
  }
  private normalizePacket(p: Packet): Packet {
    return {
      ...p,
      workflow: p.workflow ? {
        ...p.workflow,
        identifierKinds: Array.isArray(p.workflow.identifierKinds) ? p.workflow.identifierKinds : [],
        requiredIdentifierKinds: Array.isArray(p.workflow.requiredIdentifierKinds) ? p.workflow.requiredIdentifierKinds : [],
        defaultSplit: { ...splitPolicy(), ...p.workflow.defaultSplit },
        types: (Array.isArray(p.workflow.types) ? p.workflow.types : []).filter(Boolean).map(type => ({ ...type, split: { ...splitPolicy("inherit"), ...type.split }, keywords: Array.isArray(type.keywords) ? type.keywords : [] })),
      } : undefined,
      pages: (Array.isArray(p.pages) ? p.pages : []).map(page => ({ ...page, reviewReasons: Array.isArray(page.reviewReasons) ? page.reviewReasons : [] })),
      documents: (Array.isArray(p.documents) ? p.documents : []).map(doc => ({ ...doc, pageIds: Array.isArray(doc.pageIds) ? doc.pageIds : [], reviewReasons: Array.isArray(doc.reviewReasons) ? doc.reviewReasons : [], businessReference: doc.businessReference ?? "" })),
      identifierCandidates: (Array.isArray(p.identifierCandidates) ? p.identifierCandidates : []).map(candidate => ({ ...candidate, pageIds: Array.isArray(candidate.pageIds) ? candidate.pageIds : [] })),
    };
  }
  previewCreated(packet: Packet) {
    this.accept(packet);
    this.managerOpen.set(false);
    this.notice.set("Sample preview opened. Test runs can be reviewed and exported, but cannot be delivered.");
  }
  private upsert(p: Packet) {
    this.packets.update((all) => [p, ...all.filter((x) => x.id !== p.id)]);
  }
  private mayDiscard() {
    return (
      !this.dirty() || window.confirm("Discard your unsaved review changes?")
    );
  }
  async open(id: string) {
    if (this.busy() || !this.mayDiscard()) return;
    const token = ++this.selectionToken;
    clearTimeout(this.timer);
    this.busy.set(true);
    this.error.set("");
    this.notice.set("");
    try {
      const p = await firstValueFrom(
        this.http.get<Packet>("/api/packets/" + encodeURIComponent(id)),
      );
      if (token === this.selectionToken) this.accept(p);
    } catch (e) {
      this.fail(e, "Could not load this packet. Try again.");
    } finally {
      this.busy.set(false);
    }
  }
  async demo() {
    if (this.busy() || !this.mayDiscard()) return;
    this.busy.set(true);
    this.error.set("");
    this.notice.set("");
    clearTimeout(this.timer);
    try {
      this.accept(
        await firstValueFrom(
          this.http.post<Packet>(
            "/api/packets/demo",
            {},
            { headers: this.headers },
          ),
        ),
      );
    } catch (e) {
      this.fail(
        e,
        "Could not create a sample packet. Check the backend and try again.",
      );
    } finally {
      this.busy.set(false);
    }
  }
  async upload(event: Event) {
    const input = event.target as HTMLInputElement;
    const files = Array.from(input.files ?? []);
    input.value = "";
    if (!files.length || !this.uploadReady() || this.busy()) return;
    if (this.pendingUpload()) {
      this.error.set("Retry the pending upload before submitting another packet.");
      return;
    }
    if (files.some((f) => !/\.(pdf|png|jpe?g)$/i.test(f.name))) {
      this.error.set("Choose PDF, PNG, or JPG files.");
      return;
    }
    if (
      files.reduce((n, f) => n + f.size, 0) >
      Math.min(this.config()?.maxUploadMb ?? 25, 25) * 1024 * 1024
    ) {
      this.error.set(
        "The selected files exceed the " +
          this.config()?.maxUploadMb +
          " MB upload limit. Choose a smaller packet.",
      );
      return;
    }
    if (!this.mayDiscard()) return;
    const selected = this.selectedWorkflow();
    this.pendingUpload.set({ files, key: crypto.randomUUID(), workflow: this.workflowSelection() && selected ? { id: selected.id, version: selected.version } : null, workflowLabel: selected ? `${selected.name} v${selected.version}` : "Server default" });
    await this.submitUpload();
  }
  async retryUpload() {
    if (!this.uploadUncertain() || !this.mayDiscard()) return;
    await this.submitUpload();
  }
  private async submitUpload() {
    const submission = this.pendingUpload();
    if (!submission || this.busy()) return;
    const data = new FormData();
    submission.files.forEach((f) => data.append("files", f));
    if (submission.workflow) {
      data.append("workflowId", submission.workflow.id);
      data.append("workflowVersion", String(submission.workflow.version));
    }
    this.busy.set(true);
    this.error.set("");
    this.notice.set("");
    clearTimeout(this.timer);
    try {
      this.accept(
        await firstValueFrom(
          this.http.post<Packet>("/api/packets", data, {
            headers: this.headers.set("Idempotency-Key", submission.key),
          }),
        ),
      );
      this.pendingUpload.set(null);
      this.uploadUncertain.set(false);
    } catch (e) {
      const uncertain =
        e instanceof HttpErrorResponse && (e.status === 0 || e.status >= 500);
      this.uploadUncertain.set(uncertain);
      if (!uncertain) this.pendingUpload.set(null);
      this.fail(
        e,
        uncertain
          ? "The upload result is uncertain. Retry upload to safely check or submit the same files without duplicate processing."
          : "Upload failed. Check the service configuration and file sizes, then choose files again.",
      );
    } finally {
      this.busy.set(false);
      this.poll();
    }
  }
  private poll() {
    clearTimeout(this.timer);
    const p = this.packet();
    if (this.disposed || !p || !["queued", "processing"].includes(p.status))
      return;
    this.timer = setTimeout(async () => {
      const selectionToken = this.selectionToken;
      try {
        const next = await firstValueFrom(
          this.http.get<Packet>("/api/packets/" + encodeURIComponent(p.id)),
        );
        if (this.disposed || this.packet()?.id !== p.id) return;
        if (
          selectionToken !== this.selectionToken ||
          this.busy() ||
          this.packet()?.revision !== p.revision
        ) {
          this.poll();
          return;
        }
        this.upsert(next);
        if (!this.dirty()) this.accept(next);
      } catch (e) {
        this.fail(
          e,
          "Processing status could not be refreshed. Retrying automatically.",
        );
        this.poll();
      }
    }, 2000);
  }
  change(id: string, field: "type" | "businessReference", value: string) {
    if (!this.editable()) return;
    this.documents.update((ds) =>
      ds.map((d) => (d.id === id ? { ...d, [field]: value } : d)),
    );
    this.markDirty();
  }
  private markDirty() {
    this.dirty.set(true);
    if (this.classificationStage() === "export") this.classificationStage.set("review");
    this.notice.set("");
  }
  move(targetId: string) {
    if (!targetId || targetId === this.selectedDoc()?.id) return;
    this.relocatePage(this.selectedId(), targetId === 'new' ? null : targetId,
      this.documents().find(d => d.id === targetId)?.pageIds.length ?? 0);
  }
  dropPage(event: CdkDragDrop<string, string, { pageId: string; packetId: string }>) {
    if (!event.isPointerOverContainer || event.item.data.packetId !== this.packet()?.id) return;
    const source = this.documents().find(d => d.id === event.previousContainer.data);
    if (!source?.pageIds.includes(event.item.data.pageId)) return;
    this.relocatePage(event.item.data.pageId, event.container.data === 'new' ? null : event.container.data, event.currentIndex);
  }
  private relocatePage(pageId: string, targetId: string | null, position: number) {
    if (!this.editable() || !Number.isInteger(position) || position < 0) return;
    const docs = structuredClone(this.documents());
    const source = docs.find(d => d.pageIds.includes(pageId));
    if (!source) return;
    let target = docs.find(d => d.id === targetId);
    if (targetId === null) {
      if (source.pageIds.length === 1) return;
      target = { id: crypto.randomUUID(), type: source.type, pageIds: [], businessReference: '', reviewReasons: [] };
      docs.splice(docs.indexOf(source) + 1, 0, target);
    }
    if (!target || position > target.pageIds.length) return;
    const previous = source.pageIds.indexOf(pageId);
    if (source === target && previous === position) return;
    source.pageIds.splice(previous, 1);
    const insertion = Math.min(position, target.pageIds.length);
    target.pageIds.splice(insertion, 0, pageId);
    const remaining = docs.filter(d => d.pageIds.length);
    this.documents.set(remaining);
    this.selectedId.set(pageId);
    this.markDirty();
    this.notice.set(`Page moved to document ${remaining.indexOf(target) + 1}, position ${insertion + 1}.`);
  }
  async save(approve: boolean) {
    const packet = this.packet();
    if (!packet || !this.editable()) return;
    const ids = this.documents().flatMap((d) => d.pageIds);
    if (
      ids.length !== packet.pages.length ||
      new Set(ids).size !== ids.length ||
      packet.pages.some((p) => !ids.includes(p.id))
    ) {
      this.error.set(
        "Every source page must belong to exactly one document. Refresh the packet to recover.",
      );
      return;
    }
    this.busy.set(true);
    this.error.set("");
    try {
      this.accept(
        await firstValueFrom(
          this.http.put<Packet>(
            "/api/packets/" + encodeURIComponent(packet.id) + "/review",
            { revision: packet.revision, documents: this.documents(), approve },
            { headers: this.headers },
          ),
        ),
      );
      this.notice.set(
        this.packet()?.status === "approved"
          ? "Packet approved. The export is ready."
          : packet.status === "approved"
            ? "Changes saved. Previous approval was cleared; approve this revised packet to export."
            : "Draft saved.",
      );
      if (this.historyOpen()) await this.loadHistory();
    } catch (e) {
      if (e instanceof HttpErrorResponse && e.status === 409) {
        this.stale.set(true);
        this.error.set(
          "This packet changed in another session. Your edits are still here. Review or copy them before refreshing; refreshing will discard unsaved changes.",
        );
      } else
        this.fail(e, "Could not save. Your edits are preserved. Try again.");
    } finally {
      this.busy.set(false);
    }
  }
  async retry() {
    const p = this.packet();
    if (!p || this.busy()) return;
    this.busy.set(true);
    this.error.set("");
    try {
      this.accept(
        await firstValueFrom(
          this.http.post<Packet>(
            "/api/packets/" + encodeURIComponent(p.id) + "/retry",
            {},
            { headers: this.headers },
          ),
        ),
      );
    } catch (e) {
      this.fail(e, "Retry failed. Check backend configuration and try again.");
    } finally {
      this.busy.set(false);
    }
  }
  async lifecycle(action: "reprocess" | "cancel") {
    const p = this.packet();
    if (!p || this.busy() || this.stale()) return;
    const active = ["queued", "processing"].includes(p.status);
    if ((action === "cancel" && !active) || (action === "reprocess" && active))
      return;
    if (action === "reprocess" && this.reprocessSelection() && !this.workflows.published().some(w => refKey(w) === this.reprocessSelection())) {
      this.error.set("The selected reprocessing workflow is no longer published. Choose a published version or keep the parent snapshot.");
      return;
    }
    if (!this.mayDiscard()) return;
    this.busy.set(true);
    this.error.set("");
    clearTimeout(this.timer);
    try {
      const next = await firstValueFrom(
        this.http.post<Packet>(
          `/api/packets/${encodeURIComponent(p.id)}/${action}`,
          { revision: p.revision, ...(action === "reprocess" ? this.reprocessWorkflow() : {}) },
          { headers: this.headers },
        ),
      );
      this.accept(next);
      this.notice.set(
        action === "reprocess"
          ? "New processing run queued. The original run is preserved."
          : "Processing cancelled. You can start a new run when ready.",
      );
    } catch (e) {
      if (e instanceof HttpErrorResponse && e.status === 409)
        this.stale.set(true);
      this.fail(
        e,
        "The packet could not be updated. Refresh the packet and try again.",
      );
    } finally {
      this.busy.set(false);
      this.poll();
    }
  }
  private reprocessWorkflow() {
    const selected = this.workflows.published().find(w => refKey(w) === this.reprocessSelection());
    return selected ? { workflowId: selected.id, workflowVersion: selected.version } : {};
  }
  private async loadReferences(key: string) {
    if (!key) { this.referenceResults.set([]); return; }
    const id = key.split("@")[0];
    const rows = await firstValueFrom(this.http.get<ShipmentCheck[]>("/api/references/packets/" + encodeURIComponent(id))).catch(() => [] as ShipmentCheck[]);
    if (this.referenceKey() === key) this.referenceResults.set(rows);
  }
  async checkShipment() {
    const doc = this.selectedDoc();
    if (!doc || !this.editable()) return;
    this.checkingShipment.set(true);
    try {
      if (this.dirty()) await this.save(false);
      const p = this.packet();
      if (!p || this.dirty()) return;
      await firstValueFrom(this.http.post("/api/references/packets/" + encodeURIComponent(p.id) + "/lookup", { packetId: p.id, revision: p.revision, documentId: doc.id }, { headers: this.headers })).catch(() => undefined);
      await this.loadReferences(this.referenceKey());
    } finally { this.checkingShipment.set(false); }
  }
  // Lowest page score in the document; scores are uncalibrated, so this only directs reviewer attention.
  classification(doc: Document) {
    const pages = (this.packet()?.pages ?? []).filter((p) => doc.pageIds.includes(p.id));
    if (!pages.length) return null;
    const changed = pages.some((p) => p.predictedType !== doc.type);
    const score = Math.min(...pages.map((p) => p.confidence));
    const boundary = pages.some((p) => p.startProbability > 0.25 && p.startProbability < 0.75);
    const mode = this.packet()?.mode;
    return {
      changed,
      percent: Math.round(score * 100),
      boundary,
      uncertain: !changed && (doc.type === "unknown" || score < 0.8 || boundary),
      kind: mode === "simulation" ? "rule score" : mode === "demo" ? "sample score" : "model confidence",
    };
  }
  useReference(value: string) {
    const doc = this.selectedDoc();
    if (doc) this.change(doc.id, "businessReference", value);
  }
  exportUrl() {
    return "/api/packets/" + encodeURIComponent(this.packet()!.id) + "/export";
  }
  documentDownloadUrl(id: string) {
    return (
      "/api/packets/" +
      encodeURIComponent(this.packet()!.id) +
      "/documents/" +
      encodeURIComponent(id) +
      "/download"
    );
  }
  clearFilters() {
    this.inboxSearch.set("");
    this.statusFilter.set("all");
    this.modeFilter.set("all");
    this.workflowFilter.set("all");
  }
  async toggleHistory() {
    this.historyOpen.update((open) => !open);
    if (this.historyOpen()) await this.loadHistory();
  }
  async loadHistory() {
    const id = this.packet()?.id;
    if (!id) return;
    const token = ++this.historyToken;
    this.historyLoading.set(true);
    this.historyError.set("");
    try {
      const history = await firstValueFrom(
        this.http.get<PacketRevision[]>(
          "/api/packets/" + encodeURIComponent(id) + "/history",
        ),
      );
      if (token === this.historyToken && this.packet()?.id === id)
        this.history.set((Array.isArray(history) ? history : []).map(h => ({ ...h, documents: Array.isArray(h.documents) ? h.documents : [] })).sort((a, b) => a.revision - b.revision));
    } catch {
      if (token === this.historyToken)
        this.historyError.set("Could not load revision history. Try again.");
    } finally {
      if (token === this.historyToken) this.historyLoading.set(false);
    }
  }
  private fail(e: unknown, fallback: string) {
    // Only expose the API's explicit user-facing error field, never raw responses.
    const body: unknown = e instanceof HttpErrorResponse ? e.error : null;
    const message =
      body &&
      typeof body === "object" &&
      "error" in body &&
      typeof body.error === "string"
        ? body.error.trim()
        : "";
    this.error.set(
      message ? `${fallback} ${message.slice(0, 1000)}` : fallback,
    );
  }
}
