import { Component, effect, inject, input, output, signal } from '@angular/core';
import { HttpClient, HttpHeaders } from '@angular/common/http';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';

interface Entry { reference: string; recordId: string; label: string; }
interface Dataset { id: string; version: number; name: string; synthetic: boolean; hash: string; entries: Entry[]; }
interface ReferenceResult { packetId: string; revision: number; documentId: string; reference: string; status: string; matches: Entry[]; provenance: string; datasetHash: string; synthetic: boolean; resultHash: string; candidateReferences: string[] | null; }
interface Connector { id: string; kind: string; configured: boolean; requiresIdempotentReceiver: boolean; }
interface Delivery { id: string; packetId: string; revision: number; connectorId: string; status: string; transportAcknowledged: boolean; businessStatus: string; receiptId: string | null; attempts: number; error: string | null; contentHash: string; }
interface Imported { name: string; status: string; packetId: string | null; }

@Component({ selector: 'app-reference-panel', standalone: true, imports: [FormsModule], templateUrl: './reference-panel.html', styleUrl: './reference-panel.css' })
export class ReferencePanelComponent {
  packetId = input(''); revision = input(0); refreshed = output<void>();
  private http = inject(HttpClient); private headers = new HttpHeaders({ 'X-PaperworkClassifier': '1' });
  datasets = signal<Dataset[]>([]); results = signal<ReferenceResult[]>([]); connectors = signal<Connector[]>([]); deliveries = signal<Delivery[]>([]); imports = signal<Imported[]>([]);
  busy = signal(false); error = signal(''); notice = signal('');
  datasetId = ''; datasetName = ''; synthetic = true; entriesJson = '[{"reference":"SYNTHETIC-100","recordId":"example-1","label":"Synthetic example"}]';
  workflowId = ''; workflowVersion: number | null = null; testRun = true;
  receiptIds: Record<string, string> = {}; receiptStatuses: Record<string, string> = {};
  constructor() { effect(() => { const id=this.packetId(); this.revision(); void this.refresh(id); }); }
  private async run(action: () => Promise<void>) { if(this.busy()) return; this.busy.set(true); this.error.set(''); this.notice.set(''); try { await action(); this.refreshed.emit(); } catch(e: unknown) { const error=e as { error?: { error?: string }; message?: string }; this.error.set(error.error?.error || error.message || 'Request failed.'); } finally { this.busy.set(false); } }
  async refresh(id = this.packetId()) {
    try {
      const [datasets,connectors,deliveries,results] = await Promise.all([
        firstValueFrom(this.http.get<Dataset[]>('/api/references/datasets')).catch(() => [] as Dataset[]),
        firstValueFrom(this.http.get<Connector[]>('/api/connectors/')).catch(() => [] as Connector[]),
        firstValueFrom(this.http.get<Delivery[]>('/api/connectors/deliveries', { params: id ? {packetId:id} : {} })).catch(() => [] as Delivery[]),
        id ? firstValueFrom(this.http.get<ReferenceResult[]>('/api/references/packets/'+encodeURIComponent(id))) : Promise.resolve([] as ReferenceResult[]),
      ]);
      if(id!==this.packetId())return;
      this.datasets.set(datasets);this.connectors.set(connectors);this.deliveries.set(deliveries);this.results.set(results.filter(r=>r.revision===this.revision()));
    } catch { this.error.set('Reference and connector data could not be loaded. Administrative access may be required.'); }
  }
  createDataset() { return this.run(async()=> { const entries: unknown=JSON.parse(this.entriesJson); if(!Array.isArray(entries))throw new Error('Entries must be a JSON array.'); await firstValueFrom(this.http.post('/api/references/datasets',{id:this.datasetId,name:this.datasetName,synthetic:this.synthetic,entries},{headers:this.headers})); await this.refresh();this.notice.set('Immutable dataset version created. Pin its ID, version and hash in a workflow reference policy.'); }); }
  lookup() { const packetId=this.packetId(),revision=this.revision(); return this.run(async()=>{await firstValueFrom(this.http.post('/api/references/packets/'+encodeURIComponent(packetId)+'/lookup',{packetId,revision},{headers:this.headers}));await this.refresh();}); }
  verify(result: ReferenceResult, recordId: string) { return this.run(async()=>{await firstValueFrom(this.http.post('/api/references/packets/'+encodeURIComponent(result.packetId)+'/verify',{packetId:result.packetId,revision:result.revision,documentId:result.documentId,resultHash:result.resultHash,recordId},{headers:this.headers}));await this.refresh();}); }
  scan(connector: Connector) { return this.run(async()=>{const rows=await firstValueFrom(this.http.post<Imported[]>('/api/connectors/'+encodeURIComponent(connector.id)+'/import',{workflowId:this.workflowId || null,workflowVersion:this.workflowVersion,testRun:this.testRun},{headers:this.headers}));this.imports.set(rows);this.notice.set('Folder scan completed. Existing content was not imported twice.');}); }
  deliver(connector: Connector) { const packetId=this.packetId(),revision=this.revision(); return this.run(async()=>{await firstValueFrom(this.http.post('/api/connectors/'+encodeURIComponent(connector.id)+'/packets/'+encodeURIComponent(packetId)+'/deliveries',{packetId,revision},{headers:this.headers}));await this.refresh();}); }
  retry(delivery: Delivery) { return this.run(async()=>{await firstValueFrom(this.http.post('/api/connectors/deliveries/'+encodeURIComponent(delivery.id)+'/retry',{}, {headers:this.headers}));await this.refresh();}); }
  receipt(delivery: Delivery) { return this.run(async()=>{await firstValueFrom(this.http.post('/api/connectors/deliveries/'+encodeURIComponent(delivery.id)+'/receipt',{businessStatus:this.receiptStatuses[delivery.id] || 'accepted',receiptId:this.receiptIds[delivery.id] || ''},{headers:this.headers}));await this.refresh();}); }
}
