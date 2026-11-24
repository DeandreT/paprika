import { Component, EventEmitter, Input, OnChanges, Output, inject, signal } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { FormsModule } from '@angular/forms';
import { JsonPipe } from '@angular/common';
import { firstValueFrom } from 'rxjs';

interface Definition { definition: { id: string; version: number }; sha256: string }
interface Template { template: { id: string; version: number; label: string }; sha256: string }
interface Evidence { pageId: string; offset: number; length: number; text: string; textSha256: string }
interface Run { id: string; packetRevision: number; results: { documentId: string; fields: { name: string; value: string | null; status: string; evidence: Evidence[] }[]; tables: unknown[] }[] }
interface Packet { sources: { id: string; name: string }[]; pages: { id: string; sourceId: string; sourceName: string; pageNumber: number }[] }
interface Artifact { id: string; format: string; sha256: string }

@Component({ selector: 'app-extraction-panel', standalone: true, imports: [FormsModule, JsonPipe], templateUrl: './extraction-panel.html', styleUrl: './extraction-panel.css' })
export class ExtractionPanelComponent implements OnChanges {
  @Input() packetId = ''; @Input() revision = 0; @Output() refreshed = new EventEmitter<void>();
  private http = inject(HttpClient); private headers = { 'X-paprika': '1' }; private opened = false;
  busy = signal(false); error = signal(''); notice = signal(''); definitions = signal<Definition[]>([]); templates = signal<Template[]>([]); runs = signal<Run[]>([]); packet = signal<Packet | null>(null); outputs = signal<Artifact[]>([]); history = signal<unknown>(null); recognition = signal<unknown>(null); derivatives = signal<unknown>(null);
  definitionJson = JSON.stringify({ id: 'invoice-fields', version: 1, documentType: 'invoice', fields: [{ name: 'invoiceNumber', kind: 'text', label: 'Invoice', required: true }, { name: 'total', kind: 'number', label: 'Total', required: true }], tables: [{ name: 'items', header: 'SKU|Quantity|Price', columns: ['sku', 'quantity', 'price'], delimiter: '|' }] }, null, 2);
  definitionKey = ''; sourceId = ''; templateId = 'invoice-layout'; templateVersion = 1; templateLabel = 'invoice'; minimumScore = .85; minimumMargin = .05; rotation = 0; grayscale = false; contrast = 1;
  sourcePageNumber = 1;
  definitionId = 'invoice-fields'; definitionVersion = 1; documentType = 'invoice';
  fields = [{ name: 'invoiceNumber', kind: 'text', mode: 'label', expression: 'Invoice', required: true }, { name: 'total', kind: 'number', mode: 'label', expression: 'Total', required: true }];
  tables = [{ name: 'items', header: 'SKU|Quantity|Price', columns: 'sku, quantity, price', delimiter: '|' }];
  addField() { this.fields.push({ name: '', kind: 'text', mode: 'label', expression: '', required: false }); }
  addTable() { this.tables.push({ name: '', header: '', columns: '', delimiter: '|' }); }
  importAdvanced() { return this.action(async () => { const d = JSON.parse(this.definitionJson); this.definitionId = d.id; this.definitionVersion = d.version; this.documentType = d.documentType ?? ''; this.fields = d.fields.map((f: { name: string; kind: string; pattern?: string; label?: string; description?: string; required?: boolean }) => ({ name: f.name, kind: f.kind, mode: f.description != null ? 'ai' : f.pattern == null ? 'label' : 'pattern', expression: f.description ?? f.pattern ?? f.label ?? '', required: f.required ?? false })); this.tables = d.tables.map((t: { name: string; header: string; columns: string[]; delimiter: string }) => ({ ...t, columns: t.columns.join(', ') })); }); }
  correctionRun = ''; correctionDocument = ''; correctionField = ''; correctionValue = ''; correctionReason = ''; correctionEvidence = '[]';
  outputFormat = 'text-json'; redactValues = ''; regionsJson = '[]';
  ngOnChanges() { if (this.opened) void this.load(); }
  toggle(event: Event) { this.opened = (event.target as HTMLDetailsElement).open; if (this.opened) void this.load(); }
  private async action(fn: () => Promise<void>) { this.busy.set(true); this.error.set(''); this.notice.set(''); try { await fn(); } catch (e) { this.error.set(e instanceof HttpErrorResponse ? e.error?.error ?? e.message : e instanceof Error ? e.message : 'Operation failed.'); } finally { this.busy.set(false); } }
  private post<T>(url: string, body: unknown) { return firstValueFrom(this.http.post<T>(url, body, { headers: this.headers })); }
  private async refresh() {
    const id = this.packetId;
    const [definitions, templates] = await Promise.all([firstValueFrom(this.http.get<Definition[]>('/api/extraction/definitions')), firstValueFrom(this.http.get<Template[]>('/api/recognition/templates'))]);
    this.definitions.set(definitions); this.templates.set(templates);
    if (!id) { this.packet.set(null); this.runs.set([]); this.outputs.set([]); return; }
    const [packet, extraction, outputs, recognition, derivatives] = await Promise.all([firstValueFrom(this.http.get<Packet>(`/api/packets/${id}`)), firstValueFrom(this.http.get<{ runs: Run[]; corrections: unknown }>(`/api/extraction/${id}`)), firstValueFrom(this.http.get<Artifact[]>(`/api/outputs/${id}`)), firstValueFrom(this.http.get<unknown>(`/api/recognition/${id}`)), firstValueFrom(this.http.get<unknown>(`/api/preprocessing/${id}`))]);
    if (id !== this.packetId) return; packet.sources = [...new Map(packet.pages.map(p => [p.sourceId, { id: p.sourceId, name: p.sourceName }])).values()]; this.packet.set(packet); this.runs.set(extraction.runs); this.outputs.set(outputs); this.history.set(extraction.corrections); this.recognition.set(recognition); this.derivatives.set(derivatives);
    if (!packet.sources.some(s => s.id === this.sourceId)) this.sourceId = packet.sources[0]?.id ?? '';
  }
  load() { return this.action(() => this.refresh()); }
  saveDefinition() { return this.action(async () => { const definition = { id: this.definitionId, version: this.definitionVersion, documentType: this.documentType || null, fields: this.fields.map(f => ({ name: f.name, kind: f.kind, required: f.required, label: f.mode === 'label' ? f.expression : null, pattern: f.mode === 'pattern' ? f.expression : null, description: f.mode === 'ai' ? f.expression : null })), tables: this.tables.map(t => ({ ...t, columns: t.columns.split(',').map(c => c.trim()) })) }; await this.post('/api/extraction/definitions', definition); this.definitionJson = JSON.stringify(definition, null, 2); await this.refresh(); this.notice.set('Immutable definition version saved.'); }); }
  extract() { return this.action(async () => { const [id, version] = this.definitionKey.split(':'); await this.post(`/api/extraction/${this.packetId}/run`, { revision: this.revision, definitionId: id, version: Number(version) }); await this.refresh(); }); }
  edit(run: Run, documentId: string, field: Run['results'][number]['fields'][number]) { this.correctionRun = run.id; this.correctionDocument = documentId; this.correctionField = field.name; this.correctionValue = field.value ?? ''; this.correctionEvidence = JSON.stringify(field.evidence, null, 2); }
  correct() { return this.action(async () => { await this.post(`/api/extraction/${this.packetId}/corrections`, { revision: this.revision, runId: this.correctionRun, documentId: this.correctionDocument, field: this.correctionField, value: this.correctionValue || null, reason: this.correctionReason, evidence: JSON.parse(this.correctionEvidence) }); this.refreshed.emit(); await this.refresh(); this.notice.set('Correction saved; packet approval invalidated. Reloaded history retains the original run.'); }); }
  train() { return this.action(async () => { await this.post('/api/recognition/templates', { revision: this.revision, sourceId: this.sourceId, pageNumber: this.sourcePageNumber, id: this.templateId, version: this.templateVersion, label: this.templateLabel }); await this.refresh(); }); }
  recognize() { return this.action(async () => { await this.post(`/api/recognition/${this.packetId}/run`, { revision: this.revision, policy: { templates: this.templates().map(t => ({ id: t.template.id, version: t.template.version, sha256: t.sha256 })), minimumScore: this.minimumScore, minimumMargin: this.minimumMargin } }); await this.refresh(); }); }
  preprocess() { return this.action(async () => { await this.post(`/api/preprocessing/${this.packetId}`, { revision: this.revision, sourceId: this.sourceId, pageNumber: this.sourcePageNumber, policy: { rotation: Number(this.rotation), grayscale: this.grayscale, contrast: this.contrast } }); await this.refresh(); }); }
  generate() { return this.action(async () => { await this.post(`/api/outputs/${this.packetId}`, { revision: this.revision, format: this.outputFormat, redactValues: this.redactValues.split('\n').filter(Boolean), regions: JSON.parse(this.regionsJson) }); await this.refresh(); }); }
}
