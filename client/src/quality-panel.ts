import { Component, EventEmitter, Input, OnChanges, Output, inject, signal } from "@angular/core";
import { HttpClient, HttpErrorResponse } from "@angular/common/http";
import { FormsModule } from "@angular/forms";
import { JsonPipe, PercentPipe } from "@angular/common";
import { firstValueFrom } from "rxjs";

interface Model { id: string; name: string; hash: string; approved: boolean; artifact: { synthetic: boolean; trainingExamples: number; evaluation: { status: string; examples: number; accuracy: number | null; errorUpperBound: number | null; threshold: number } } }
interface Qa { id: string; packetRevision: number; revision: number; status: string; primaryActor: string; reviewer: string | null; verdict: string | null; note: string | null; snapshot: unknown }
interface Quality { decision: { autoApprove: boolean; sampled: boolean; revision: number; items: { scope: string; id: string; reason: string }[] } | null; tasks: Qa[] }
interface Draft { kind: string; id: string; version: number; revision: number; status: string; name: string; workflow?: { capabilities?: Record<string, unknown>; [key: string]: unknown }; [key: string]: unknown }
interface QualityPolicyForm { mode: string; autoApprove: boolean; threshold: number; sampleRate: number; sampleSeed: string; minimumHeldOut: number; maximumErrorUpperBound: number; syntheticEvaluation: boolean; requireIndependentReviewer: boolean; [key: string]: unknown }
const defaultQualityPolicy = (): QualityPolicyForm => ({ mode: "manual", autoApprove: false, threshold: .95, sampleRate: .1, sampleSeed: "quality-v1", minimumHeldOut: 50, maximumErrorUpperBound: .1, syntheticEvaluation: false, requireIndependentReviewer: true });

@Component({
  selector: "app-quality-panel", standalone: true, imports: [FormsModule, JsonPipe, PercentPipe],
  template: `
  <details (toggle)="opened($event)"><summary>Quality, independent QA and trained text models</summary>
    <p>Scores are not measured accuracy. Manual approval is the default. Synthetic models cannot approve operational packets.</p>
    @if (error()) { <p role="alert">{{error()}}</p> }
    @if (notice()) { <p role="status">{{notice()}}</p> }
    <button type="button" (click)="load()" [disabled]="busy()">Refresh quality and models</button>
    @if (quality(); as q) {
      @if (q.decision; as d) {
        <p>Routing revision {{d.revision}}: {{d.autoApprove ? 'eligible for configured automatic approval' : 'manual review'}}. Independent sample: {{d.sampled ? 'yes' : 'no'}}.</p>
        <ul>@for (item of d.items; track $index) { <li>{{item.scope}}: {{item.reason}}</li> }</ul>
      }
      <h4>Independent sampled QA</h4>
      @for (task of q.tasks; track task.id) {
        <section><p>Packet revision {{task.packetRevision}} — {{task.status}}. Primary reviewer: {{task.primaryActor}}. {{task.verdict}}</p>
          <button type="button" (click)="snapshot.set(task.snapshot)">Inspect frozen QA snapshot</button>
          @if (task.status === 'pending') {
            <label>QA note <input [(ngModel)]="qaNotes[task.id]" maxlength="2000"></label>
            <button type="button" (click)="complete(task, 'pass')" [disabled]="busy()">QA pass</button>
            <button type="button" (click)="complete(task, 'fail')" [disabled]="busy()">QA fail / reopen matching approval</button>
          } @else { <p>{{task.reviewer}}: {{task.note}}</p> }
        </section>
      } @empty { <p>No sampled QA tasks for this packet.</p> }
      @if (snapshot(); as s) { <details open><summary>Frozen snapshot (not current edits)</summary><pre>{{s | json}}</pre></details> }
    }
    @if (adminAvailable()) {
    <h4>Import labeled examples</h4>
    <p>JSON array: id, text, label, synthetic (defaults true), provenance. Mark real only for authorized, independently labeled paperwork. Reviewed live approvals are captured automatically.</p>
    <textarea [(ngModel)]="examplesJson" rows="5" aria-label="Labeled examples JSON"></textarea>
    <button type="button" (click)="importExamples()" [disabled]="busy()">Import immutable examples</button>
    <h4>Train a local text classifier</h4>
    <label>Name <input [(ngModel)]="modelName" maxlength="120"></label>
    <label>Evaluation/routing score threshold <input type="number" min="0.5" max="1" step="0.01" [(ngModel)]="threshold"></label>
    <button type="button" (click)="train()" [disabled]="busy()">Train and evaluate</button>
    <p>Deterministic text-group held-out evaluation; small datasets explicitly remain not evaluated. Scores are Naive Bayes scores, not individual calibrated probabilities.</p>
    @for (model of models(); track model.id) {
      <section><strong>{{model.name}}</strong> <code>{{model.id}}</code>
        <p>{{model.artifact.synthetic ? 'Synthetic training' : 'Real-data training'}}; {{model.artifact.trainingExamples}} training examples; evaluation: {{model.artifact.evaluation.status}}, {{model.artifact.evaluation.examples}} threshold-qualified held-out examples.</p>
        @if (model.artifact.evaluation.accuracy !== null) { <p>Held-out accuracy: {{model.artifact.evaluation.accuracy | percent:'1.1-1'}}; 95% error upper bound: {{model.artifact.evaluation.errorUpperBound | percent:'1.1-1'}}.</p> }
        <p>{{model.approved ? 'Approved artifact' : 'Not approved'}}</p>
        <button type="button" (click)="approve(model)" [disabled]="busy() || model.approved || model.artifact.synthetic || model.artifact.evaluation.status !== 'held-out'">Approve real evaluated artifact</button>
      </section>
    }
    <h4>Configure a saved workflow draft</h4>
    <p>Saving changes the selected draft only. Publish it in the workflow manager to activate it. Existing packets keep frozen settings.</p>
    <label>Workflow draft <select [(ngModel)]="draftKey" (ngModelChange)="selectDraft()"><option value="">Choose draft</option>@for (draft of drafts(); track draft.id + ':' + draft.version) { <option [value]="draft.id + ':' + draft.version">{{draft.name}} v{{draft.version}}</option> }</select></label>
    <label>Trained artifact <select [(ngModel)]="modelId"><option value="">None</option>@for (model of models(); track model.id) { <option [value]="model.id">{{model.name}} {{model.approved ? '(approved)' : '(review only)'}}</option> }</select></label>
    <fieldset [disabled]="busy()"><legend>Quality routing policy</legend>
      <label>Review mode <select [(ngModel)]="policy.mode" (ngModelChange)="syncPolicyJson()"><option value="manual">Manual approval</option><option value="selective">Selective review</option></select></label>
      <label><input type="checkbox" [(ngModel)]="policy.autoApprove" (ngModelChange)="syncPolicyJson()"> Enable automatic approval</label>
      <label>Routing score threshold <input type="number" min="0.5" max="1" step="0.01" [(ngModel)]="policy.threshold" (ngModelChange)="syncPolicyJson()"></label>
      <label>Independent QA sample rate (0–1) <input type="number" min="0" max="1" step="0.01" [(ngModel)]="policy.sampleRate" (ngModelChange)="syncPolicyJson()"></label>
      <label>QA sampling seed <input maxlength="200" [(ngModel)]="policy.sampleSeed" (ngModelChange)="syncPolicyJson()"></label>
      <label>Minimum held-out examples <input type="number" min="1" max="1000000" step="1" [(ngModel)]="policy.minimumHeldOut" (ngModelChange)="syncPolicyJson()"></label>
      <label>Maximum held-out error upper bound <input type="number" min="0.001" max="0.25" step="0.001" [(ngModel)]="policy.maximumErrorUpperBound" (ngModelChange)="syncPolicyJson()"></label>
      <label><input type="checkbox" [(ngModel)]="policy.syntheticEvaluation" (ngModelChange)="syncPolicyJson()"> Synthetic evaluation only</label>
      <label><input type="checkbox" [(ngModel)]="policy.requireIndependentReviewer" (ngModelChange)="syncPolicyJson()"> Require independent QA reviewer</label>
    </fieldset>
    <p>Automatic approval requires selective mode, an approved real-data artifact, the exact evaluated threshold, sufficient held-out evidence and independent sampling. Synthetic evaluation stays manual.</p>
    <details><summary>Advanced quality policy JSON</summary>
      <label>Quality policy JSON <textarea [(ngModel)]="policyJson" (ngModelChange)="advancedDirty = true" rows="6"></textarea></label>
      <button type="button" (click)="applyPolicyJson()" [disabled]="busy()">Apply advanced JSON to form</button>
      @if (advancedDirty) { <p>Apply the advanced JSON before saving, or change a structured field to use the form instead.</p> }
    </details>
    <button type="button" (click)="saveDraft()" [disabled]="busy() || !draftKey || advancedDirty">Validate and save draft quality/training settings</button>
    } @else { <p>Dataset, model and policy administration requires administrator access. Authorized packet QA remains available above.</p> }
  </details>`,
  styles: [`:host{display:block}details{margin:1rem 0}section{padding:.6rem;background:var(--mat-sys-surface-container-low,#f3f4f6);margin:.5rem 0}label{display:block;margin:.5rem 0}textarea{display:block;width:100%;box-sizing:border-box}pre{max-height:24rem;overflow:auto;white-space:pre-wrap;overflow-wrap:anywhere}button{margin:.3rem}code{overflow-wrap:anywhere}p{max-width:80ch}`],
})
export class QualityPanelComponent implements OnChanges {
  @Input() packetId = "";
  @Input() revision = 0;
  @Output() refreshed = new EventEmitter<void>();
  private http = inject(HttpClient);
  private headers = { "X-PaperworkClassifier": "1" };
  private expanded = false;
  quality = signal<Quality | null>(null); models = signal<Model[]>([]); drafts = signal<Draft[]>([]); adminAvailable = signal(true);
  busy = signal(false); error = signal(""); notice = signal(""); snapshot = signal<unknown>(null);
  qaNotes: Record<string, string> = {};
  examplesJson = '[{"id":"example-1","text":"Invoice sample text","label":"invoice","synthetic":true,"provenance":"synthetic demonstration"}]';
  modelName = "Local text model"; threshold = .95; draftKey = ""; modelId = "";
  policy = defaultQualityPolicy();
  policyJson = JSON.stringify(this.policy, null, 2);
  advancedDirty = false;
  syncPolicyJson() { this.policyJson = JSON.stringify(this.policy, null, 2); this.advancedDirty = false; }
  applyPolicyJson() {
    try {
      const value: unknown = JSON.parse(this.policyJson);
      if (!value || typeof value !== "object" || Array.isArray(value)) throw new Error("Quality policy JSON must be an object.");
      this.policy = { ...defaultQualityPolicy(), ...value };
      this.syncPolicyJson(); this.error.set("");
    } catch (e) { this.error.set(e instanceof Error ? e.message : "Invalid quality policy JSON."); }
  }
  ngOnChanges() { this.snapshot.set(null); if (this.expanded) void this.load(); }
  opened(event: Event) { this.expanded = (event.target as HTMLDetailsElement).open; if (this.expanded) void this.load(); }
  private async run(action: () => Promise<void>) {
    this.busy.set(true); this.error.set(""); this.notice.set("");
    try { await action(); } catch (e) { this.error.set(e instanceof HttpErrorResponse ? e.error?.error ?? e.message : e instanceof Error ? e.message : "Operation failed."); } finally { this.busy.set(false); }
  }
  async load() { await this.run(async () => { await this.refreshData(); }); }
  private async refreshData() {
    const packetId = this.packetId;
    const quality = packetId ? await firstValueFrom(this.http.get<Quality>(`/api/quality/packets/${encodeURIComponent(packetId)}`)) : null;
    if (packetId === this.packetId) this.quality.set(quality);
    try {
      const [models, catalog] = await Promise.all([
        firstValueFrom(this.http.get<Model[]>("/api/training/models")),
        firstValueFrom(this.http.get<{entries: Draft[]}>("/api/workflows")),
      ]);
      this.models.set(models); this.drafts.set(catalog.entries.filter(e => e.kind === "workflow" && e.status === "draft")); this.adminAvailable.set(true);
    } catch (e) {
      if (e instanceof HttpErrorResponse && e.status === 403) { this.adminAvailable.set(false); this.models.set([]); this.drafts.set([]); }
      else throw e;
    }
  }
  async importExamples() { await this.run(async () => { const examples = JSON.parse(this.examplesJson); if (!Array.isArray(examples)) throw new Error("Supply a JSON array."); await firstValueFrom(this.http.post("/api/training/examples", { examples }, { headers: this.headers })); this.notice.set("Examples imported. Existing immutable IDs cannot be changed."); }); }
  async train() { await this.run(async () => { await firstValueFrom(this.http.post("/api/training/models", { name: this.modelName, threshold: this.threshold }, { headers: this.headers })); await this.refreshData(); this.notice.set("Model trained. Review held-out results before approving."); }); }
  async approve(model: Model) { await this.run(async () => { await firstValueFrom(this.http.post(`/api/training/models/${encodeURIComponent(model.id)}/approve`, { hash: model.hash }, { headers: this.headers })); await this.refreshData(); this.notice.set("Artifact approved; workflow automation still requires evaluation criteria and explicit policy."); }); }
  async complete(task: Qa, verdict: string) { await this.run(async () => { await firstValueFrom(this.http.post(`/api/quality/qa/${encodeURIComponent(task.id)}/complete`, { revision: task.revision, verdict, note: this.qaNotes[task.id] ?? "" }, { headers: this.headers })); await this.refreshData(); this.refreshed.emit(); }); }
  selectDraft() {
    const draft = this.drafts().find(d => d.id + ":" + d.version === this.draftKey);
    const caps = draft?.workflow?.capabilities;
    this.policy = { ...defaultQualityPolicy(), ...(caps?.["quality"] as Partial<QualityPolicyForm> | undefined) }; this.syncPolicyJson();
    this.modelId = (caps?.["training"] as {id?:string} | undefined)?.id ?? "";
  }
  async saveDraft() { await this.run(async () => {
    const draft = this.drafts().find(d => d.id + ":" + d.version === this.draftKey);
    if (!draft?.workflow) throw new Error("Choose a workflow draft.");
    if (this.advancedDirty) throw new Error("Apply advanced JSON before saving the policy.");
    const validated = await firstValueFrom(this.http.post<{quality: unknown; training: unknown}>("/api/quality/validate", { quality: this.policy, training: this.modelId ? {id: this.modelId} : null }, { headers: this.headers }));
    const updated = { ...draft, workflow: { ...draft.workflow, capabilities: { ...draft.workflow.capabilities, quality: validated.quality, training: validated.training } } };
    await firstValueFrom(this.http.put(`/api/workflows/catalog/workflow/${encodeURIComponent(draft.id)}/${draft.version}`, updated, { headers: this.headers }));
    await this.refreshData(); this.refreshed.emit(); this.notice.set("Workflow draft saved. Publish from the workflow manager when ready.");
  }); }
}
