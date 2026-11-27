import { Component, EventEmitter, Input, OnChanges, OnDestroy, Output, computed, inject, signal } from "@angular/core";
import { HttpClient, HttpErrorResponse } from "@angular/common/http";
import { FormsModule } from "@angular/forms";
import { JsonPipe, PercentPipe } from "@angular/common";
import { firstValueFrom, Subscription } from "rxjs";
import { apiError } from "./workflow-store";

export interface TrainingDataset { id: string; name: string }
interface ExampleSummary { id: string; label: string; synthetic: boolean }
interface Model { id: string; name: string; hash: string; approved: boolean; artifact: { dataset?: TrainingDataset | null; synthetic: boolean; trainingExamples: number; evaluation: { status: string; examples: number; accuracy: number | null; errorUpperBound: number | null; threshold: number } } }
interface Qa { id: string; packetRevision: number; revision: number; status: string; primaryActor: string; reviewer: string | null; verdict: string | null; note: string | null; snapshot: unknown }
interface Quality { decision: { autoApprove: boolean; sampled: boolean; revision: number; items: { scope: string; id: string; reason: string }[] } | null; tasks: Qa[] }
interface Draft { kind: string; id: string; version: number; revision: number; status: string; name: string; workflow?: { capabilities?: Record<string, unknown>; [key: string]: unknown }; [key: string]: unknown }
interface QualityPolicyForm { mode: string; autoApprove: boolean; threshold: number; sampleRate: number; sampleSeed: string; minimumHeldOut: number; maximumErrorUpperBound: number; syntheticEvaluation: boolean; requireIndependentReviewer: boolean; [key: string]: unknown }
const defaultQualityPolicy = (): QualityPolicyForm => ({ mode: "manual", autoApprove: false, threshold: .95, sampleRate: .1, sampleSeed: "quality-v1", minimumHeldOut: 50, maximumErrorUpperBound: .1, syntheticEvaluation: false, requireIndependentReviewer: true });
const legacyDatasetId = "default";
const datasetApi = (id: string) => `/api/training/datasets/${encodeURIComponent(id)}`;

function reviewDatasetFor(draft: Draft | undefined): string | null {
  const collection = draft?.workflow?.capabilities?.["reviewCollection"] as { datasetId?: unknown } | null | undefined;
  if (collection == null) return "";
  // Keep a malformed explicit target distinct from a deliberately unspecified target.
  return typeof collection.datasetId === "string" && collection.datasetId ? collection.datasetId : null;
}

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
    <h4>Classifier datasets</h4>
    <label>New dataset name <input [(ngModel)]="newDatasetName" maxlength="120" [disabled]="busy()"></label>
    <button type="button" (click)="createDataset()" [disabled]="busy() || !newDatasetName.trim()">Create dataset</button>
    <label>Training dataset
      <select [ngModel]="datasetId()" (ngModelChange)="selectDataset($event)" [disabled]="busy()">
        <option value="">Choose a dataset</option>
        @for (dataset of datasets(); track dataset.id) { <option [value]="dataset.id">{{dataset.name}}</option> }
      </select>
    </label>
    @if (selectedDataset(); as dataset) {
      <fieldset data-testid="training-dataset-tools" [disabled]="busy()"><legend>Examples and training: {{dataset.name}}</legend>
        <p>Dataset ID: <code>{{dataset.id}}</code></p>
        @if (dataset.id !== 'default') {
          <details><summary>Rename dataset</summary>
            <label>Updated dataset name <input [(ngModel)]="renameTo" maxlength="120"></label>
            <button type="button" (click)="renameDataset()" [disabled]="!renameTo.trim() || renameTo.trim() === dataset.name">Save dataset name</button>
          </details>
        }
        <h5>Examples in {{dataset.name}}</h5>
        <p>List example IDs, labels and whether each example is synthetic. Document text and provenance are kept out of this view.</p>
        <button type="button" (click)="listExamples()" [disabled]="examplesLoading()">List examples</button>
        @if (examplesLoading()) { <span role="status">Loading examples…</span> }
        @if (examplesError()) { <p role="alert">{{examplesError()}}</p> }
        @if (examples(); as rows) {
          <button type="button" (click)="hideExamples()">Hide examples</button>
          @if (rows.length) {
            <div class="examples-table"><table>
              <caption>{{rows.length}} examples in {{dataset.name}}</caption>
              <thead><tr><th scope="col">Example ID</th><th scope="col">Label</th><th scope="col">Source</th></tr></thead>
              <tbody>@for (row of rows.slice(0, visibleExamples); track row.id) {
                <tr><td><code>{{row.id}}</code></td><td>{{row.label}}</td><td>{{row.synthetic ? 'Synthetic' : 'Real'}}</td></tr>
              }</tbody>
            </table></div>
            @if (rows.length > visibleExamples) { <button type="button" (click)="visibleExamples = visibleExamples + 50">Show more examples</button> }
          } @else { <p>No examples in {{dataset.name}}.</p> }
        }
        <h5>Import labeled examples into {{dataset.name}}</h5>
        <p>Supply a JSON array with id, text, label, synthetic (defaults true), and provenance. Mark real only for authorized, independently labeled paperwork. Switching datasets clears this input.</p>
        <textarea [(ngModel)]="examplesJson" rows="5" aria-label="Labeled examples JSON" [placeholder]="examplePlaceholder"></textarea>
        <button type="button" (click)="importExamples()" [disabled]="!examplesJson.trim()">Import immutable examples</button>
        <h5>Train a local text classifier on {{dataset.name}}</h5>
        <label>Name <input [(ngModel)]="modelName" maxlength="120"></label>
        <label>Evaluation/routing score threshold <input type="number" min="0.5" max="1" step="0.01" [(ngModel)]="threshold"></label>
        <button type="button" (click)="train()" [disabled]="!modelName.trim()">Train and evaluate</button>
        <p>Deterministic text-group held-out evaluation; small datasets explicitly remain not evaluated. Scores are Naive Bayes scores, not individual calibrated probabilities.</p>
      </fieldset>
    } @else { <p>Choose a training dataset to list or import examples and train a model.</p> }
    <h4>Trained artifacts from all datasets</h4>
    @for (model of models(); track model.id) {
      <section><strong>{{model.name}}</strong> <code>{{model.id}}</code>
        @if (model.artifact.dataset; as source) {
          <p>Dataset at training: <strong>{{source.name}}</strong> · <code>{{source.id}}</code></p>
        } @else {
          <p>Dataset at training: <strong>Legacy</strong> · <code>default</code> (historical artifact)</p>
        }
        <p>{{model.artifact.synthetic ? 'Synthetic training' : 'Real-data training'}}; {{model.artifact.trainingExamples}} training examples; evaluation: {{model.artifact.evaluation.status}}, {{model.artifact.evaluation.examples}} threshold-qualified held-out examples.</p>
        @if (model.artifact.evaluation.accuracy !== null) { <p>Held-out accuracy: {{model.artifact.evaluation.accuracy | percent:'1.1-1'}}; 95% error upper bound: {{model.artifact.evaluation.errorUpperBound | percent:'1.1-1'}}.</p> }
        <p>{{model.approved ? 'Approved artifact' : 'Not approved'}}</p>
        <button type="button" (click)="approve(model)" [disabled]="busy() || model.approved || model.artifact.synthetic || model.artifact.evaluation.status !== 'held-out'">Approve real evaluated artifact</button>
      </section>
    }
    <h4>Configure a saved workflow draft</h4>
    <p>Saving changes the selected draft only. Publish it in the workflow manager to activate it. Existing packets keep frozen settings.</p>
    <label>Workflow draft <select [(ngModel)]="draftKey" (ngModelChange)="selectDraft()" [disabled]="busy()"><option value="">Choose draft</option>@for (draft of drafts(); track draft.id + ':' + draft.version) { <option [value]="draft.id + ':' + draft.version">{{draft.name}} v{{draft.version}}</option> }</select></label>
    <label>Review collection dataset
      <select [(ngModel)]="reviewDatasetId" [disabled]="busy() || !draftKey">
        <option value="">{{unspecifiedReviewLabel()}}</option>
        @if (!reviewTargetAvailable()) { <option [ngValue]="reviewDatasetId" disabled>Unavailable dataset: {{reviewDatasetId || '(invalid reference)'}}</option> }
        @for (target of datasets(); track target.id) { <option [value]="target.id">{{target.name}}</option> }
      </select>
    </label>
    <p>Choose where reviewed examples from future packets go, independently of the trained artifact. An unspecified target collects in Legacy only for workflows with no model or a Legacy model.</p>
    @if (draftKey && !reviewTargetAvailable()) { <p role="alert">This draft references an unavailable review collection. Choose a dataset before saving.</p> }
    <label>Trained artifact <select [(ngModel)]="modelId" [disabled]="busy() || !draftKey"><option value="">None</option>@if (modelId && !draftModel()) { <option [value]="modelId" disabled>Unavailable artifact: {{modelId}}</option> }@for (model of models(); track model.id) { <option [value]="model.id">{{model.name}} · {{model.artifact.dataset?.name ?? 'Legacy'}} {{model.approved ? '(approved)' : '(review only)'}}</option> }</select></label>
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
      <label>Quality policy JSON <textarea [(ngModel)]="policyJson" (ngModelChange)="advancedDirty = true" [disabled]="busy()" rows="6"></textarea></label>
      <button type="button" (click)="applyPolicyJson()" [disabled]="busy()">Apply advanced JSON to form</button>
      @if (advancedDirty) { <p>Apply the advanced JSON before saving, or change a structured field to use the form instead.</p> }
    </details>
    <button type="button" (click)="saveDraft()" [disabled]="busy() || !draftKey || advancedDirty || !reviewTargetAvailable()">Validate and save draft quality/training settings</button>
    } @else if (adminAvailable() === false) { <p>Dataset, model and policy administration requires administrator access. Authorized packet QA remains available above.</p> }
  </details>`,
  styles: [`:host{display:block}details{margin:1rem 0}section{padding:.6rem;background:var(--mat-sys-surface-container-low,#f3f4f6);margin:.5rem 0}label{display:block;margin:.5rem 0}textarea{display:block;width:100%;box-sizing:border-box}pre{max-height:24rem;overflow:auto;white-space:pre-wrap;overflow-wrap:anywhere}button{margin:.3rem}code,legend,td{overflow-wrap:anywhere}p{max-width:80ch}fieldset{min-inline-size:0}select{max-width:100%}.examples-table{overflow:auto;max-height:20rem}table{border-collapse:collapse;text-align:left}caption{text-align:left;padding:.5rem}td,th{padding:.35rem .7rem;border-bottom:1px solid var(--mat-sys-outline-variant,#bbb)}h5{font-size:1rem;margin:1em 0 .4em}`],
})
export class QualityPanelComponent implements OnChanges, OnDestroy {
  @Input() packetId = "";
  @Input() revision = 0;
  @Output() refreshed = new EventEmitter<void>();
  private http = inject(HttpClient);
  private headers = { "X-paprika": "1" };
  private expanded = false;
  private refreshQueued = false;
  private exampleRead?: Subscription;
  private editingDraft?: Draft;
  private initialPolicy = "";
  quality = signal<Quality | null>(null); models = signal<Model[]>([]); drafts = signal<Draft[]>([]); adminAvailable = signal<boolean | null>(null);
  busy = signal(false); error = signal(""); notice = signal(""); snapshot = signal<unknown>(null);
  qaNotes: Record<string, string> = {};
  datasets = signal<TrainingDataset[]>([]);
  datasetId = signal("");
  selectedDataset = computed<TrainingDataset | null>(() => this.datasets().find(dataset => dataset.id === this.datasetId()) ?? null);
  newDatasetName = ""; renameTo = "";
  examples = signal<ExampleSummary[] | null>(null);
  examplesLoading = signal(false); examplesError = signal("");
  visibleExamples = 50;
  examplesJson = "";
  readonly examplePlaceholder = '[{"id":"example-1","text":"Invoice sample text","label":"invoice","synthetic":true,"provenance":"synthetic demonstration"}]';
  modelName = "Local text model"; threshold = .95; draftKey = ""; modelId = "";
  reviewDatasetId: string | null = "";
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
  ngOnChanges() {
    this.snapshot.set(null);
    this.quality.set(null);
    if (this.expanded) void this.load();
  }
  opened(event: Event) {
    this.expanded = (event.target as HTMLDetailsElement).open;
    if (this.expanded) void this.load();
    else this.hideExamples();
  }
  ngOnDestroy() { this.expanded = false; this.hideExamples(); }

  private async run(action: () => Promise<void>, adminAction = true) {
    if (this.busy() || (adminAction && !this.adminAvailable())) return;
    if (this.examplesLoading()) this.hideExamples();
    this.busy.set(true); this.error.set(""); this.notice.set("");
    try {
      await action();
    } catch (e) {
      if (adminAction && this.accessDenied(e)) this.clearAdminData();
      this.error.set(e instanceof HttpErrorResponse ? apiError(e, "Operation failed.") : e instanceof Error ? e.message : "Operation failed.");
    } finally {
      this.busy.set(false);
      if (this.refreshQueued) {
        this.refreshQueued = false;
        if (this.expanded) void this.load();
      }
    }
  }
  async load() {
    if (this.busy()) { this.refreshQueued = true; return; }
    this.hideExamples();
    await this.run(() => this.refreshData(), false);
  }
  private accessDenied(error: unknown) {
    return error instanceof HttpErrorResponse && (error.status === 401 || error.status === 403);
  }
  private clearAdminData() {
    this.adminAvailable.set(false);
    this.notice.set("");
    this.models.set([]); this.datasets.set([]); this.drafts.set([]);
    this.newDatasetName = ""; this.setSelectedDataset("");
    this.draftKey = ""; this.selectDraft();
  }
  private async refreshData() {
    const packetId = this.packetId;
    const revision = this.revision;
    try {
      const quality = packetId ? await firstValueFrom(this.http.get<Quality>(`/api/quality/packets/${encodeURIComponent(packetId)}`)) : null;
      if (packetId === this.packetId && revision === this.revision) this.quality.set(quality);
    } catch (e) {
      if (packetId === this.packetId && revision === this.revision) {
        this.quality.set(null);
        this.error.set(apiError(e, "Could not load packet QA."));
      }
    }
    try {
      const [datasets, models, catalog] = await Promise.all([
        firstValueFrom(this.http.get<TrainingDataset[]>("/api/training/datasets")),
        firstValueFrom(this.http.get<Model[]>("/api/training/models")),
        firstValueFrom(this.http.get<{entries: Draft[]}>("/api/workflows")),
      ]);
      const previous = this.selectedDataset();
      this.datasets.set(datasets);
      const current = this.selectedDataset();
      if (this.datasetId() && !current) this.setSelectedDataset("");
      else if (current && previous && current.name !== previous.name) {
        if (this.renameTo === previous.name) this.renameTo = current.name;
        else if (this.renameTo !== current.name) this.notice.set(`The dataset is now named ${current.name}. Your proposed name was kept.`);
      }
      this.models.set(models);
      this.drafts.set(catalog.entries.filter(e => e.kind === "workflow" && e.status === "draft"));
      const refreshedDraft = this.findDraft();
      if (this.editingDraft?.revision !== refreshedDraft?.revision) {
        if (!refreshedDraft) this.draftKey = "";
        this.selectDraft();
      }
      this.adminAvailable.set(true);
    } catch (e) {
      if (this.accessDenied(e)) this.clearAdminData();
      else throw e;
    }
  }
  selectDataset(id: string) {
    if (this.busy()) return;
    this.setSelectedDataset(id);
    this.error.set(""); this.notice.set("");
  }
  private setSelectedDataset(id: string) {
    this.hideExamples();
    this.datasetId.set(id);
    this.examplesJson = "";
    this.renameTo = this.selectedDataset()?.name ?? "";
  }
  async createDataset() {
    await this.run(async () => {
      const name = this.newDatasetName.trim();
      if (!name) throw new Error("Enter a dataset name.");
      const dataset = await firstValueFrom(this.http.post<TrainingDataset>("/api/training/datasets", { name }, { headers: this.headers }));
      this.datasets.update(rows => [...rows, dataset]);
      this.setSelectedDataset(dataset.id);
      this.newDatasetName = "";
      this.notice.set(`Dataset created: ${dataset.name}.`);
    });
  }
  async renameDataset() {
    await this.run(async () => {
      const dataset = this.requireDataset();
      const name = this.renameTo.trim();
      if (!name) throw new Error("Enter a dataset name.");
      let updated: TrainingDataset;
      try {
        updated = await firstValueFrom(this.http.put<TrainingDataset>(datasetApi(dataset.id), { name, expectedName: dataset.name }, { headers: this.headers }));
      } catch (e) {
        if (e instanceof HttpErrorResponse && e.status === 409) {
          throw new Error(apiError(e, "Rename conflict. Use Refresh quality and models to check the current names."));
        }
        throw e;
      }
      this.datasets.update(rows => rows.map(row => row.id === updated.id ? updated : row));
      this.renameTo = updated.name;
      this.notice.set(`Dataset renamed to ${updated.name}.`);
    });
  }
  hideExamples() {
    this.exampleRead?.unsubscribe();
    this.exampleRead = undefined;
    this.examples.set(null); this.examplesError.set(""); this.examplesLoading.set(false);
    this.visibleExamples = 50;
  }
  listExamples() {
    const dataset = this.selectedDataset();
    if (!dataset || !this.adminAvailable() || this.busy()) return;
    this.hideExamples();
    this.examplesLoading.set(true);
    this.exampleRead = this.http.get<ExampleSummary[]>(`${datasetApi(dataset.id)}/examples/summary`).subscribe({
      next: rows => {
        this.examples.set(rows);
        this.examplesLoading.set(false);
      },
      error: e => {
        this.examplesLoading.set(false);
        if (this.accessDenied(e)) this.clearAdminData();
        else this.examplesError.set(apiError(e, `Could not list examples in ${dataset.name}.`));
      },
    });
  }
  private requireDataset() {
    const dataset = this.selectedDataset();
    if (!dataset) throw new Error("Choose a training dataset.");
    return dataset;
  }
  async importExamples() {
    await this.run(async () => {
      const dataset = this.requireDataset();
      let examples: unknown;
      try { examples = JSON.parse(this.examplesJson); }
      catch { throw new Error("Supply a valid JSON array of labeled examples."); }
      if (!Array.isArray(examples)) throw new Error("Supply a JSON array of labeled examples.");
      await firstValueFrom(this.http.post(`${datasetApi(dataset.id)}/examples`, { examples }, { headers: this.headers }));
      this.examplesJson = "";
      this.hideExamples();
      this.notice.set(`Examples imported into ${dataset.name}. List examples to see the updated dataset.`);
    });
  }
  async train() {
    await this.run(async () => {
      const dataset = this.requireDataset();
      const model = await firstValueFrom(this.http.post<Model>(`${datasetApi(dataset.id)}/models`, { name: this.modelName, threshold: this.threshold }, { headers: this.headers }));
      this.models.update(rows => [model, ...rows]);
      this.notice.set(`Model trained on ${dataset.name}. Review held-out results before approving.`);
    });
  }
  async approve(model: Model) { await this.run(async () => { await firstValueFrom(this.http.post(`/api/training/models/${encodeURIComponent(model.id)}/approve`, { hash: model.hash }, { headers: this.headers })); await this.refreshData(); this.notice.set("Artifact approved; workflow automation still requires evaluation criteria and explicit policy."); }); }
  async complete(task: Qa, verdict: string) { await this.run(async () => { await firstValueFrom(this.http.post(`/api/quality/qa/${encodeURIComponent(task.id)}/complete`, { revision: task.revision, verdict, note: this.qaNotes[task.id] ?? "" }, { headers: this.headers })); await this.refreshData(); this.refreshed.emit(); }, false); }
  private findDraft() { return this.drafts().find(d => d.id + ":" + d.version === this.draftKey); }
  draftModel() { return this.models().find(model => model.id === this.modelId); }
  unspecifiedReviewLabel() {
    if (this.modelId) {
      const model = this.draftModel();
      if (!model) return "Unspecified — model origin unavailable";
      if (model.artifact.dataset && model.artifact.dataset.id !== legacyDatasetId) return "Unspecified — do not collect examples";
    }
    return "Unspecified — collect in Legacy";
  }
  reviewTargetAvailable() {
    return this.reviewDatasetId === "" || this.datasets().some(dataset => dataset.id === this.reviewDatasetId);
  }
  selectDraft() {
    const draft = this.findDraft();
    this.editingDraft = draft;
    const caps = draft?.workflow?.capabilities;
    this.policy = { ...defaultQualityPolicy(), ...(caps?.["quality"] as Partial<QualityPolicyForm> | undefined) }; this.syncPolicyJson();
    this.initialPolicy = this.policyJson;
    this.modelId = (caps?.["training"] as {id?:string} | undefined)?.id ?? "";
    this.reviewDatasetId = reviewDatasetFor(draft);
  }
  async saveDraft() { await this.run(async () => {
    const draft = this.editingDraft;
    if (!draft?.workflow) throw new Error("Choose a workflow draft.");
    if (this.advancedDirty) throw new Error("Apply advanced JSON before saving the policy.");
    if (!this.reviewTargetAvailable()) throw new Error("Choose an available review collection.");
    const capabilities = { ...draft.workflow.capabilities };
    const originalModelId = (capabilities["training"] as {id?: string} | undefined)?.id ?? "";
    const policyOrModelChanged = this.initialPolicy !== this.policyJson || originalModelId !== this.modelId;
    const targetChanged = reviewDatasetFor(draft) !== this.reviewDatasetId;
    if (!targetChanged && !policyOrModelChanged) {
      this.notice.set("Workflow draft settings are unchanged.");
      return;
    }
    if (targetChanged) {
      if (this.reviewDatasetId) capabilities["reviewCollection"] = { datasetId: this.reviewDatasetId };
      else delete capabilities["reviewCollection"];
    }
    if (policyOrModelChanged) {
      const validated = await firstValueFrom(this.http.post<{quality: unknown; training: unknown}>("/api/quality/validate", { quality: this.policy, training: this.modelId ? {id: this.modelId} : null }, { headers: this.headers }));
      capabilities["quality"] = validated.quality;
      capabilities["training"] = validated.training;
    }
    const updated = { ...draft, workflow: { ...draft.workflow, capabilities } };
    await firstValueFrom(this.http.put(`/api/workflows/catalog/workflow/${encodeURIComponent(draft.id)}/${draft.version}`, updated, { headers: this.headers }));
    await this.refreshData(); this.refreshed.emit(); this.notice.set("Workflow draft saved. Publish from the workflow manager when ready.");
  }); }
}
