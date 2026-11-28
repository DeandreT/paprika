import { Component, OnChanges, OnDestroy, SimpleChanges, computed, inject, input, output, signal } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { FormsModule } from '@angular/forms';
import { JsonPipe } from '@angular/common';
import { firstValueFrom } from 'rxjs';
import { Packet } from './models';
import { PacketFieldsComponent } from './packet-fields';

interface Definition { definition: { id: string; version: number }; sha256: string }
interface Template { template: { id: string; version: number; label: string }; sha256: string }
interface Artifact { id: string; format: string; sha256: string }

@Component({ selector: 'app-extraction-panel', standalone: true, imports: [FormsModule, JsonPipe, PacketFieldsComponent], templateUrl: './extraction-panel.html', styleUrl: './extraction-panel.css' })
export class ExtractionPanelComponent implements OnChanges, OnDestroy {
  packet = input<Packet | null>(null);
  reviewBlocked = input(false);
  fieldSaved = output<Packet>();
  fieldEditing = output<boolean>();
  reloadPacket = output<void>();
  editing = signal(false);
  opened = signal(false);
  fieldVersion = signal(0);
  sources = computed(() => [...new Map((this.packet()?.pages ?? []).map(page => [page.sourceId, { id: page.sourceId, name: page.sourceName }])).values()]);
  get packetId() { return this.packet()?.id ?? ''; }
  get revision() { return this.packet()?.revision ?? 0; }
  private generation = 0;
  private http = inject(HttpClient); private headers = { 'X-paprika': '1' };
  busy = signal(false); error = signal(''); notice = signal(''); definitions = signal<Definition[]>([]); templates = signal<Template[]>([]); outputs = signal<Artifact[]>([]); recognition = signal<unknown>(null); derivatives = signal<unknown>(null);
  definitionJson = JSON.stringify({ id: 'invoice-fields', version: 1, documentType: 'invoice', fields: [{ name: 'invoiceNumber', kind: 'text', label: 'Invoice', required: true }, { name: 'total', kind: 'number', label: 'Total', required: true }], tables: [{ name: 'items', header: 'SKU|Quantity|Price', columns: ['sku', 'quantity', 'price'], delimiter: '|' }] }, null, 2);
  definitionKey = ''; sourceId = ''; templateId = 'invoice-layout'; templateVersion = 1; templateLabel = 'invoice'; minimumScore = .85; minimumMargin = .05; rotation = 0; grayscale = false; contrast = 1;
  sourcePageNumber = 1;
  definitionId = 'invoice-fields'; definitionVersion = 1; documentType = 'invoice';
  fields = [{ name: 'invoiceNumber', kind: 'text', mode: 'label', expression: 'Invoice', required: true }, { name: 'total', kind: 'number', mode: 'label', expression: 'Total', required: true }];
  tables = [{ name: 'items', header: 'SKU|Quantity|Price', columns: 'sku, quantity, price', delimiter: '|' }];
  addField() { this.fields.push({ name: '', kind: 'text', mode: 'label', expression: '', required: false }); }
  addTable() { this.tables.push({ name: '', header: '', columns: '', delimiter: '|' }); }
  importAdvanced() { return this.action(async () => { const d = JSON.parse(this.definitionJson); this.definitionId = d.id; this.definitionVersion = d.version; this.documentType = d.documentType ?? ''; this.fields = d.fields.map((f: { name: string; kind: string; pattern?: string; label?: string; description?: string; required?: boolean }) => ({ name: f.name, kind: f.kind, mode: f.description != null ? 'ai' : f.pattern == null ? 'label' : 'pattern', expression: f.description ?? f.pattern ?? f.label ?? '', required: f.required ?? false })); this.tables = d.tables.map((t: { name: string; header: string; columns: string[]; delimiter: string }) => ({ ...t, columns: t.columns.join(', ') })); }); }
  outputFormat = 'text-json'; redactValues = ''; regionsJson = '[]';
  ngOnChanges(changes: SimpleChanges) {
    if (!changes['packet']) return;
    ++this.generation;
    this.busy.set(false);
    this.outputs.set([]); this.recognition.set(null); this.derivatives.set(null);
    if (!this.sources().some(s => s.id === this.sourceId)) this.sourceId = this.sources()[0]?.id ?? '';
    if (this.opened()) void this.load();
  }
  ngOnDestroy() { ++this.generation; }
  toggle(event: Event) {
    this.opened.set((event.target as HTMLDetailsElement).open);
    if (this.opened()) void this.load();
  }
  editingChanged(value: boolean) { this.editing.set(value); this.fieldEditing.emit(value); }
  private async action(fn: (generation: number) => Promise<void>) {
    if (this.busy() || this.editing()) return;
    const generation = ++this.generation;
    this.busy.set(true); this.error.set(''); this.notice.set('');
    try { await fn(generation); }
    catch (e) {
      if (generation === this.generation) this.error.set(e instanceof HttpErrorResponse ? e.error?.error ?? e.message : e instanceof Error ? e.message : 'Operation failed.');
    } finally { if (generation === this.generation) this.busy.set(false); }
  }
  private post<T>(url: string, body: unknown) { return firstValueFrom(this.http.post<T>(url, body, { headers: this.headers })); }
  private async refresh(generation: number) {
    if (generation !== this.generation) return;
    const id = this.packetId;
    const [definitions, templates] = await Promise.all([firstValueFrom(this.http.get<Definition[]>('/api/extraction/definitions')), firstValueFrom(this.http.get<Template[]>('/api/recognition/templates'))]);
    if (generation !== this.generation) return;
    this.definitions.set(definitions); this.templates.set(templates);
    if (!id) return;
    const [outputs, recognition, derivatives] = await Promise.all([firstValueFrom(this.http.get<Artifact[]>(`/api/outputs/${id}`)), firstValueFrom(this.http.get<unknown>(`/api/recognition/${id}`)), firstValueFrom(this.http.get<unknown>(`/api/preprocessing/${id}`))]);
    if (generation !== this.generation) return;
    this.outputs.set(outputs); this.recognition.set(recognition); this.derivatives.set(derivatives);
  }
  load() { return this.action(async generation => { this.fieldVersion.update(n => n + 1); await this.refresh(generation); }); }
  saveDefinition() {
    return this.action(async generation => {
      const definition = { id: this.definitionId, version: this.definitionVersion, documentType: this.documentType || null,
        fields: this.fields.map(f => ({ name: f.name, kind: f.kind, required: f.required, label: f.mode === 'label' ? f.expression : null, pattern: f.mode === 'pattern' ? f.expression : null, description: f.mode === 'ai' ? f.expression : null })),
        tables: this.tables.map(t => ({ ...t, columns: t.columns.split(',').map(c => c.trim()) })) };
      await this.post('/api/extraction/definitions', definition);
      if (generation !== this.generation) return;
      this.definitionJson = JSON.stringify(definition, null, 2);
      await this.refresh(generation);
      if (generation === this.generation) this.notice.set('Immutable definition version saved.');
    });
  }
  extract() {
    return this.action(async generation => {
      const [id, version] = this.definitionKey.split(':');
      await this.post(`/api/extraction/${this.packetId}/run`, { revision: this.revision, definitionId: id, version: Number(version) });
      if (generation === this.generation) this.fieldVersion.update(n => n + 1);
    });
  }
  train() { return this.action(async generation => { await this.post('/api/recognition/templates', { revision: this.revision, sourceId: this.sourceId, pageNumber: this.sourcePageNumber, id: this.templateId, version: this.templateVersion, label: this.templateLabel }); await this.refresh(generation); }); }
  recognize() { return this.action(async generation => { await this.post(`/api/recognition/${this.packetId}/run`, { revision: this.revision, policy: { templates: this.templates().map(t => ({ id: t.template.id, version: t.template.version, sha256: t.sha256 })), minimumScore: this.minimumScore, minimumMargin: this.minimumMargin } }); await this.refresh(generation); }); }
  preprocess() { return this.action(async generation => { await this.post(`/api/preprocessing/${this.packetId}`, { revision: this.revision, sourceId: this.sourceId, pageNumber: this.sourcePageNumber, policy: { rotation: Number(this.rotation), grayscale: this.grayscale, contrast: this.contrast } }); await this.refresh(generation); }); }
  generate() { return this.action(async generation => { await this.post(`/api/outputs/${this.packetId}`, { revision: this.revision, format: this.outputFormat, redactValues: this.redactValues.split('\n').filter(Boolean), regions: JSON.parse(this.regionsJson) }); await this.refresh(generation); }); }
}
