import { Component, DestroyRef, effect, inject, input, output, signal } from "@angular/core";
import { HttpClient, HttpErrorResponse, HttpHeaders } from "@angular/common/http";
import { FormsModule } from "@angular/forms";
import { firstValueFrom } from "rxjs";

export type FlowBlockKind = "Reference" | "Extraction" | "Quality" | "Case" | "Output" | "Transform" | "Validate";
export type FlowConditionKind = "DocumentType" | "FieldEquals" | "ReferenceVerified";
export interface FlowOperation { kind: "Trim" | "Uppercase" | "Lowercase" | "RequireValue"; source: string; target: string; }
export interface FlowCondition { kind: FlowConditionKind; key: string; value: string; negate: boolean; }
export interface FlowBlock { id: string; kind: FlowBlockKind; dependsOn: string[]; when: FlowCondition | null; templateId: string | null; operations: FlowOperation[] | null; }
export interface FlowTemplate { id: string; kind: FlowBlockKind; operations: FlowOperation[] | null; }
export interface FlowDefinition { name: string; blocks: FlowBlock[]; templates: FlowTemplate[]; }
interface FlowLibraryEntry { id: string; revision: number; definition: FlowDefinition; hash: string; actor: string; }
interface FlowExecution { blockId: string; status: string; attempt: number; idempotencyKey: string; error: string | null; updatedAt: string; }
interface FlowRun { packetId: string; snapshotHash: string; definitionHash: string; definition: FlowDefinition; executions: FlowExecution[]; }

@Component({
  selector: "app-flow-panel", standalone: true, imports: [FormsModule],
  templateUrl: "./flow-panel.html", styleUrl: "./flow-panel.css",
})
export class FlowPanelComponent {
  private http = inject(HttpClient);
  private headers = new HttpHeaders({ "X-paprika": "1" });
  private generation = 0;
  packetId = input(""); revision = input(0); refreshed = output<void>();
  run = signal<FlowRun | null>(null); library = signal<FlowLibraryEntry[]>([]);
  error = signal(""); notice = signal(""); busy = signal(false);
  editorOpen = signal(false);
  definition: FlowDefinition = { name: "New flow", blocks: [], templates: [] };
  flowId = ""; libraryRevision = 0; nextKind: FlowBlockKind = "Reference";
  kinds: FlowBlockKind[] = ["Reference", "Extraction", "Quality", "Case", "Output", "Transform", "Validate"];
  conditions: FlowConditionKind[] = ["DocumentType", "FieldEquals", "ReferenceVerified"];
  constructor() {
    inject(DestroyRef).onDestroy(() => { this.generation++; });
    effect(() => { const id = this.packetId(); this.revision(); void this.loadRun(id); });
  }
  private message(error: unknown) { return error instanceof HttpErrorResponse ? error.error?.error || error.error?.message || `Request failed (${error.status}).` : "Request failed."; }
  async loadRun(id = this.packetId()) {
    const generation = ++this.generation; this.run.set(null); this.error.set("");
    if (!id) return;
    try { const run = await firstValueFrom(this.http.get<FlowRun | null>(`/api/flows/${encodeURIComponent(id)}`)); if (generation === this.generation) this.run.set(run); }
    catch (error) { if (generation === this.generation) this.error.set(this.message(error)); }
  }
  async loadLibrary() {
    this.editorOpen.set(true); this.error.set("");
    try { this.library.set(await firstValueFrom(this.http.get<FlowLibraryEntry[]>("/api/flows/library"))); }
    catch (error) { this.error.set(this.message(error)); }
  }
  load(entry: FlowLibraryEntry) { this.flowId = entry.id; this.libraryRevision = entry.revision; this.definition = structuredClone(entry.definition); this.definition.templates ??= []; this.notice.set("Editing a reusable definition. Accepted packets keep their original flow."); }
  newDefinition() { this.flowId = ""; this.libraryRevision = 0; this.definition = { name: "New flow", blocks: [], templates: [] }; }
  addBlock() {
    const id = `block-${this.definition.blocks.length + 1}-${Date.now().toString(36)}`;
    this.definition.blocks.push({ id, kind: this.nextKind, dependsOn: [], when: null, templateId: null, operations: this.nextKind === "Transform" || this.nextKind === "Validate" ? [{ kind: this.nextKind === "Validate" ? "RequireValue" : "Trim", source: "value", target: "value" }] : null });
  }
  removeBlock(block: FlowBlock) { this.definition.blocks = this.definition.blocks.filter(b => b !== block); for (const other of this.definition.blocks) other.dependsOn = other.dependsOn.filter(id => id !== block.id); }
  earlier(index: number) { return this.definition.blocks.slice(0, index); }
  rename(block: FlowBlock, value: string) { const old = block.id; block.id = value; for (const other of this.definition.blocks) other.dependsOn = other.dependsOn.map(id => id === old ? value : id); }
  dependency(block: FlowBlock, id: string, checked: boolean) { block.dependsOn = checked ? [...block.dependsOn, id] : block.dependsOn.filter(d => d !== id); }
  condition(block: FlowBlock, kind: string) { block.when = kind ? { kind: kind as FlowConditionKind, key: "", value: "", negate: false } : null; }
  reusable(block: FlowBlock) { const id = `${block.id}-template`; if (this.definition.templates.some(t => t.id === id)) return; this.definition.templates.push({ id, kind: block.kind, operations: structuredClone(block.operations) }); block.templateId = id; block.operations = null; }
  useTemplate(block: FlowBlock, id: string) { const template = this.definition.templates.find(t => t.id === id); block.templateId = template?.id ?? null; if (template) { block.kind = template.kind; block.operations = null; } else if (block.kind === "Transform" || block.kind === "Validate") block.operations = [{ kind: block.kind === "Validate" ? "RequireValue" : "Trim", source: "value", target: "value" }]; }
  async save() {
    if (this.busy()) return; this.busy.set(true); this.error.set(""); this.notice.set("");
    try {
      const entry = await firstValueFrom(this.http.put<FlowLibraryEntry>(`/api/flows/library/${encodeURIComponent(this.flowId)}`, { revision: this.libraryRevision, definition: this.definition }, { headers: this.headers }));
      this.load(entry); this.notice.set("Reusable flow saved. Select it in a draft workflow before accepting new packets."); await this.loadLibrary(); this.refreshed.emit();
    } catch (error) { this.error.set(this.message(error)); } finally { this.busy.set(false); }
  }
  async retry(blockId: string) {
    if (this.busy()) return; const packetId = this.packetId(); const generation = this.generation; this.busy.set(true); this.error.set("");
    try { const run = await firstValueFrom(this.http.post<FlowRun>(`/api/flows/${encodeURIComponent(packetId)}/retry`, { revision: this.revision(), blockId }, { headers: this.headers })); if (generation === this.generation) { this.run.set(run); this.notice.set("Recovery queued. Completed blocks retain their results."); this.refreshed.emit(); } }
    catch (error) { if (generation === this.generation) this.error.set(this.message(error)); } finally { this.busy.set(false); }
  }
}
