import { Component, inject, input, output, signal } from "@angular/core";
import { HttpClient } from "@angular/common/http";
import { FormsModule } from "@angular/forms";
import { firstValueFrom } from "rxjs";
import { WorkflowCapabilities } from "./models";
import { apiError } from "./workflow-store";

type Capability = keyof WorkflowCapabilities;
interface DefinitionChoice { definition: { id: string; version: number }; sha256: string }
interface TemplateChoice { template: { id: string; version: number; label: string }; sha256: string }
interface DatasetChoice { id: string; version: number; hash: string; name: string; synthetic: boolean }
interface FlowChoice { id: string; revision: number; definition: Record<string, unknown> }

@Component({
  selector: "app-capability-editor",
  standalone: true,
  imports: [FormsModule],
  styles: [`.capabilities{display:grid;gap:1rem;margin:1rem 0}.group{border-left:3px solid var(--mat-sys-primary,#396984);padding:.5rem 1rem}.fields{display:flex;flex-wrap:wrap;gap:1rem}label{display:grid;gap:.3rem;max-width:100%}input,select{max-width:100%}.toggle{display:flex;align-items:center;gap:.5rem}h4{margin:.2rem 0 .7rem}button{padding:.5rem}.muted{font-size:.85em}`],
  template: `
  <div class="capabilities" data-testid="capability-editor">
    <h3>Processing capabilities</h3>
    <p class="muted">Enable settings on this draft. Publication freezes the exact definitions for future packets. Quality and model training are configured in Platform tools.</p>
    <button type="button" (click)="load()" [disabled]="loading()">{{ loading() ? 'Loading definitions…' : 'Load capability definitions' }}</button>
    @if (error()) { <p role="alert">{{ error() }}</p> }
    <fieldset [disabled]="disabled()">
      <div class="group">
        <label class="toggle"><input type="checkbox" [ngModel]="enabled('reference')" (ngModelChange)="toggle('reference',$event)" />Verify business references</label>
        @if (enabled('reference')) {
          <div class="fields">
            <label>Reference source<select [ngModel]="get('reference','mode','local')" (ngModelChange)="referenceMode($event)"><option value="local">Versioned local dataset</option><option value="http">Configured HTTP lookup</option></select></label>
            @if (get('reference','mode','local') === 'local') {
              <label>Reference dataset<select data-testid="capability-reference-dataset" [ngModel]="referenceKey()" (ngModelChange)="selectDataset($event)"><option value="">Choose exact version</option>@for (d of datasets(); track d.id + '@' + d.version) { <option [value]="d.id + '@' + d.version">{{ d.name }} · v{{ d.version }}{{ d.synthetic ? ' · synthetic' : '' }}</option> }</select></label>
            } @else { <label>Configured lookup ID<input [ngModel]="get('reference','connectorId','')" (ngModelChange)="set('reference','connectorId',$event)" /></label> }
            <label class="toggle"><input type="checkbox" [ngModel]="get('reference','requireVerified',false)" (ngModelChange)="set('reference','requireVerified',$event)" />Require verification before export</label>
            <label class="toggle"><input type="checkbox" [ngModel]="get('reference','autoVerify',false)" (ngModelChange)="set('reference','autoVerify',$event)" />Automatically verify unique real-data matches</label>
          </div>
        }
      </div>
      <div class="group">
        <label class="toggle"><input type="checkbox" [ngModel]="enabled('operations')" (ngModelChange)="toggle('operations',$event)" />Reviewer task settings</label>
        @if (enabled('operations')) { <div class="fields">
          <label>Review deadline (hours)<input type="number" min="1" [ngModel]="get('operations','reviewSlaHours',24)" (ngModelChange)="set('operations','reviewSlaHours',$event)" /></label>
          <label>Task priority<input type="number" [ngModel]="get('operations','defaultPriority',0)" (ngModelChange)="set('operations','defaultPriority',$event)" /></label>
          <label>Claim lease (seconds)<input type="number" min="30" [ngModel]="get('operations','leaseSeconds',900)" (ngModelChange)="set('operations','leaseSeconds',$event)" /></label>
        </div> }
      </div>
      <div class="group">
        <label class="toggle"><input type="checkbox" [ngModel]="enabled('extraction')" (ngModelChange)="toggle('extraction',$event)" />Extract fields and tables</label>
        @if (enabled('extraction')) {
          <label>Extraction definition<select data-testid="capability-extraction" [ngModel]="extractionKey()" (ngModelChange)="selectDefinition($event)"><option value="">Choose exact version</option>@for (d of definitions(); track d.definition.id + '@' + d.definition.version) { <option [value]="d.definition.id + '@' + d.definition.version">{{ d.definition.id }} · v{{ d.definition.version }}</option> }</select></label>
        }
      </div>
      <div class="group">
        <label class="toggle"><input type="checkbox" [ngModel]="enabled('recognition')" (ngModelChange)="toggle('recognition',$event)" />Visual layout matching</label>
        @if (enabled('recognition')) {
          @for (t of templates(); track t.template.id + '@' + t.template.version) { <label class="toggle"><input type="checkbox" [ngModel]="templateSelected(t)" (ngModelChange)="toggleTemplate(t,$event)" />{{ t.template.id }} v{{ t.template.version }} → {{ t.template.label }}</label> }
          <div class="fields">
            <label>Minimum visual score<input type="number" min="0" max="1" step=".01" [ngModel]="get('recognition','minimumScore',.85)" (ngModelChange)="set('recognition','minimumScore',$event)" /></label>
            <label>Minimum score margin<input type="number" min="0" max="1" step=".01" [ngModel]="get('recognition','minimumMargin',.05)" (ngModelChange)="set('recognition','minimumMargin',$event)" /></label>
            <label class="toggle"><input type="checkbox" [ngModel]="get('recognition','overridePrediction',false)" (ngModelChange)="set('recognition','overridePrediction',$event)" />Allow visual match to replace text prediction</label>
          </div>
        }
      </div>
      <div class="group">
        <label class="toggle"><input type="checkbox" [ngModel]="enabled('preprocessing')" (ngModelChange)="toggle('preprocessing',$event)" />Create prepared image derivatives</label>
        @if (enabled('preprocessing')) { <div class="fields">
          <label>Rotation<select [ngModel]="get('preprocessing','rotation',0)" (ngModelChange)="set('preprocessing','rotation',+$event)"><option [ngValue]="0">0°</option><option [ngValue]="90">90°</option><option [ngValue]="180">180°</option><option [ngValue]="270">270°</option></select></label>
          <label>Contrast<input type="number" min=".25" max="4" step=".25" [ngModel]="get('preprocessing','contrast',1)" (ngModelChange)="set('preprocessing','contrast',$event)" /></label>
          <label class="toggle"><input type="checkbox" [ngModel]="get('preprocessing','grayscale',false)" (ngModelChange)="set('preprocessing','grayscale',$event)" />Grayscale</label>
        </div> }
      </div>
      <div class="group">
        <label class="toggle"><input type="checkbox" [ngModel]="enabled('case')" (ngModelChange)="toggle('case',$event)" />Group submissions into cases</label>
        @if (enabled('case')) { <div class="fields">
          <label>Customer scope<input data-testid="capability-case-customer" [ngModel]="get('case','customerId','')" (ngModelChange)="set('case','customerId',$event)" /></label>
          <label>Expected document types (comma-separated)<input [ngModel]="list('case','expectedDocumentTypes').join(', ')" (ngModelChange)="set('case','expectedDocumentTypes',split($event))" /></label>
          <label class="toggle"><input type="checkbox" [ngModel]="get('case','autoCollate',false)" (ngModelChange)="set('case','autoCollate',$event)" />Automatically collate verified references</label>
        </div> }
      </div>
      <div class="group">
        <label class="toggle"><input type="checkbox" [ngModel]="enabled('flow')" (ngModelChange)="toggle('flow',$event)" />Use a composable processing flow</label>
        @if (enabled('flow')) {
          <label>Saved processing flow<select data-testid="capability-flow" (change)="selectFlow($any($event.target).value)"><option value="">Choose saved flow to embed</option>@for (f of flows(); track f.id) { <option [value]="f.id">{{ f.id }} · revision {{ f.revision }}</option> }</select></label>
          <p class="muted">{{ flowName() || 'Select a saved flow. Configure its required capabilities above.' }}</p>
        }
      </div>
      <div class="group">
        <label class="toggle"><input type="checkbox" [ngModel]="enabled('output')" (ngModelChange)="toggle('output',$event)" />Generate additional approved output</label>
        @if (enabled('output')) { <div class="fields">
          <label>Output format<select data-testid="capability-output" [ngModel]="get('output','format','text-json')" (ngModelChange)="set('output','format',$event)"><option value="text-json">Text JSON</option><option value="redacted-text-pdf">Reconstructed redacted text PDF</option><option value="searchable-image-pdf">Searchable image PDF (OCR geometry required)</option></select></label>
          <label>Literal values to redact (one per line)<textarea [ngModel]="list('output','redactValues').join('\n')" (ngModelChange)="set('output','redactValues',$event.split('\n').filter(nonempty))"></textarea></label>
          <p class="muted">For pixel-redacted PDFs, supply exact page regions in the approved packet's extraction and output tools.</p>
        </div> }
      </div>
    </fieldset>
    @if (enabled('quality') || enabled('training') || enabled('reviewCollection')) { <p class="muted">Use Quality and training under Platform tools to change the review dataset, trained model or quality policy on a saved draft.</p> }
  </div>`,
})
export class CapabilityEditorComponent {
  value = input<WorkflowCapabilities | null | undefined>(null);
  disabled = input(false);
  valueChange = output<WorkflowCapabilities | null>();
  private http = inject(HttpClient);
  loading = signal(false); error = signal("");
  definitions = signal<DefinitionChoice[]>([]); templates = signal<TemplateChoice[]>([]);
  datasets = signal<DatasetChoice[]>([]); flows = signal<FlowChoice[]>([]);
  nonempty = (value: string) => value.trim().length > 0;
  enabled(key: Capability) { return !!this.value()?.[key]; }
  get(key: Capability, field: string, fallback: any): any { return this.value()?.[key]?.[field] ?? fallback; }
  list(key: Capability, field: string): string[] { const v = this.get(key, field, []); return Array.isArray(v) ? v : []; }
  split(value: string) { return value.split(',').map(v => v.trim()).filter(Boolean); }
  set(key: Capability, field: string, value: unknown) { this.replace(key, { ...this.value()?.[key], [field]: value }); }
  replace(key: Capability, value: Record<string, unknown> | null) {
    if (this.disabled()) return;
    const next = structuredClone(this.value() ?? {});
    if (value === null) delete next[key]; else next[key] = value;
    this.valueChange.emit(Object.keys(next).length ? next : null);
  }
  toggle(key: Capability, enabled: boolean) {
    const defaults: Partial<Record<Capability, Record<string, unknown>>> = {
      reference: { mode: 'local', requireVerified: true, autoVerify: false },
      operations: { reviewSlaHours: 24, defaultPriority: 0, leaseSeconds: 900 },
      extraction: { definitionId: '', version: 1, sha256: '' },
      recognition: { templates: [], minimumScore: .85, minimumMargin: .05, overridePrediction: false },
      preprocessing: { rotation: 0, contrast: 1, grayscale: false },
      case: { customerId: '', expectedDocumentTypes: [], autoCollate: false },
      flow: { definition: { name: '', blocks: [] } },
      output: { format: 'text-json', redactValues: [] },
    };
    this.replace(key, enabled ? defaults[key] ?? {} : null);
  }
  async load() {
    this.loading.set(true); this.error.set('');
    try {
      const [definitions, templates, datasets, flows] = await Promise.all([
        firstValueFrom(this.http.get<DefinitionChoice[]>('/api/extraction/definitions')),
        firstValueFrom(this.http.get<TemplateChoice[]>('/api/recognition/templates')),
        firstValueFrom(this.http.get<DatasetChoice[]>('/api/references/datasets')),
        firstValueFrom(this.http.get<FlowChoice[]>('/api/flows/library')),
      ]);
      this.definitions.set(definitions); this.templates.set(templates); this.datasets.set(datasets); this.flows.set(flows);
    } catch (e) { this.error.set(apiError(e, 'Could not load capability definitions.')); }
    finally { this.loading.set(false); }
  }
  extractionKey() { return this.get('extraction','definitionId','') + '@' + this.get('extraction','version',1); }
  selectDefinition(key: string) { const d = this.definitions().find(d => d.definition.id + '@' + d.definition.version === key); if (d) this.replace('extraction', { definitionId: d.definition.id, version: d.definition.version, sha256: d.sha256 }); }
  referenceKey() { return this.get('reference','datasetId','') + '@' + this.get('reference','datasetVersion',1); }
  referenceMode(mode: string) { this.replace('reference', { mode, requireVerified: this.get('reference','requireVerified',true), autoVerify: this.get('reference','autoVerify',false) }); }
  selectDataset(key: string) { const d = this.datasets().find(d => d.id + '@' + d.version === key); if (d) this.replace('reference', { ...this.value()?.reference, mode: 'local', datasetId: d.id, datasetVersion: d.version, datasetHash: d.hash }); }
  templateSelected(t: TemplateChoice) { const refs = this.get('recognition','templates',[]) as { id: string; version: number }[]; return refs.some(r => r.id === t.template.id && r.version === t.template.version); }
  toggleTemplate(t: TemplateChoice, enabled: boolean) {
    const refs = (this.get('recognition','templates',[]) as { id: string; version: number }[]).filter(r => r.id !== t.template.id || r.version !== t.template.version);
    this.set('recognition','templates',enabled ? [...refs, { id:t.template.id, version:t.template.version, sha256:t.sha256 }] : refs);
  }
  selectFlow(id: string) { const entry = this.flows().find(f => f.id === id); if (entry) this.replace('flow', { definition: structuredClone(entry.definition) }); }
  flowName() { return (this.get('flow','definition',{}) as { name?: string }).name; }
}
