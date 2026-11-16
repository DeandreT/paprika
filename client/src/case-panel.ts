import { Component, DestroyRef, effect, inject, input, output, signal } from "@angular/core";
import { HttpClient, HttpErrorResponse, HttpHeaders } from "@angular/common/http";
import { FormsModule } from "@angular/forms";
import { firstValueFrom } from "rxjs";

interface CasePacket { id: string; name: string; revision: number; documents: { id: string; type: string; businessReference: string }[]; workflow: { types: { id: string; name: string }[] } | null; }
interface CaseSource { sourceId: string; sourceName: string; sha256: string | null; pageIds: string[]; }
interface CaseMember { packetId: string; packetRevision: number; documentId: string; documentType: string; sources: CaseSource[]; addedBy: string; }
interface CaseRecord { id: string; revision: number; customerId: string; workflowId: string; businessReference: string; referenceVerified: boolean; testRun: boolean; expectedDocumentTypes: string[]; members: CaseMember[]; complete: boolean; checklist: { documentType: string; count: number; present: boolean }[]; }
interface CaseProposal { businessReference: string; candidateCaseIds: string[]; autoCollated: boolean; reason: string; }

@Component({ selector: "app-case-panel", standalone: true, imports: [FormsModule], templateUrl: "./case-panel.html", styleUrl: "./case-panel.css" })
export class CasePanelComponent {
  private http = inject(HttpClient);
  private headers = new HttpHeaders({ "X-paprika": "1" });
  private generation = 0;
  packetId = input(""); revision = input(0); refreshed = output<void>();
  records = signal<CaseRecord[]>([]); packets = signal<CasePacket[]>([]); packet = signal<CasePacket | null>(null); proposal = signal<CaseProposal | null>(null);
  error = signal(""); notice = signal(""); busy = signal(false);
  selectedPacketId = ""; businessReference = ""; selectedDocuments: string[] = []; checklistEdits: Record<string, string[]> = {};
  constructor() { inject(DestroyRef).onDestroy(() => { this.generation++; }); effect(() => { const id = this.packetId(); this.revision(); this.selectedPacketId = id; void this.refresh(); }); }
  private message(error: unknown) { return error instanceof HttpErrorResponse ? error.error?.error || error.error?.message || `Request failed (${error.status}).` : "Request failed."; }
  async refresh() {
    const generation = ++this.generation; this.error.set(""); this.selectedDocuments = []; this.packet.set(null); this.proposal.set(null);
    try {
      const records = await firstValueFrom(this.http.get<CaseRecord[]>("/api/cases"));
      if (generation !== this.generation) return; this.records.set(records); this.checklistEdits = Object.fromEntries(records.map(c => [c.id, [...c.expectedDocumentTypes]]));
      if (this.selectedPacketId) {
        const [packet, proposal] = await Promise.all([firstValueFrom(this.http.get<CasePacket>(`/api/packets/${encodeURIComponent(this.selectedPacketId)}`)), firstValueFrom(this.http.get<CaseProposal | null>(`/api/cases/proposals/${encodeURIComponent(this.selectedPacketId)}`))]);
        if (generation !== this.generation) return; this.packet.set(packet); this.proposal.set(proposal); this.businessReference = packet.documents.find(d => d.businessReference)?.businessReference ?? "";
      } else {
        const packets = await firstValueFrom(this.http.get<CasePacket[]>("/api/packets")); if (generation === this.generation) this.packets.set(packets);
      }
    } catch (error) { if (generation === this.generation) this.error.set(this.message(error)); }
  }
  selectDocument(id: string, selected: boolean) { this.selectedDocuments = selected ? [...this.selectedDocuments, id] : this.selectedDocuments.filter(d => d !== id); }
  selectExpected(record: CaseRecord, id: string, selected: boolean) { const values = this.checklistEdits[record.id] ?? []; this.checklistEdits[record.id] = selected ? [...values, id] : values.filter(d => d !== id); }
  async mutate(action: () => Promise<unknown>) {
    if (this.busy()) return; const generation = this.generation; this.busy.set(true); this.error.set("");
    try { await action(); if (generation === this.generation) { this.notice.set("Case saved."); await this.refresh(); this.refreshed.emit(); } }
    catch (error) { if (generation === this.generation) this.error.set(this.message(error)); } finally { this.busy.set(false); }
  }
  create() { const packet = this.packet(); if (!packet) return; const request = { packetId: packet.id, packetRevision: packet.revision, businessReference: this.businessReference, documentIds: [...this.selectedDocuments] }; void this.mutate(() => firstValueFrom(this.http.post<CaseRecord>("/api/cases", request, { headers: this.headers }))); }
  add(record: CaseRecord) { const packet = this.packet(); if (!packet) return; const request = { revision: record.revision, packetId: packet.id, packetRevision: packet.revision, documentIds: [...this.selectedDocuments] }; void this.mutate(() => firstValueFrom(this.http.post<CaseRecord>(`/api/cases/${record.id}/members`, request, { headers: this.headers }))); }
  remove(record: CaseRecord, member: CaseMember) { void this.mutate(async () => { const packet = await firstValueFrom(this.http.get<CasePacket>(`/api/packets/${encodeURIComponent(member.packetId)}`)); return firstValueFrom(this.http.post<CaseRecord>(`/api/cases/${record.id}/remove-members`, { revision: record.revision, packetId: packet.id, packetRevision: packet.revision, documentIds: [member.documentId] }, { headers: this.headers })); }); }
  refreshMember(record: CaseRecord, member: CaseMember) { void this.mutate(async () => { const packet = await firstValueFrom(this.http.get<CasePacket>(`/api/packets/${encodeURIComponent(member.packetId)}`)); return firstValueFrom(this.http.post<CaseRecord>(`/api/cases/${record.id}/members`, { revision: record.revision, packetId: packet.id, packetRevision: packet.revision, documentIds: [member.documentId], refresh: true }, { headers: this.headers })); }); }
  saveChecklist(record: CaseRecord) { const request = { revision: record.revision, expectedDocumentTypes: [...(this.checklistEdits[record.id] ?? record.expectedDocumentTypes)] }; void this.mutate(() => firstValueFrom(this.http.put<CaseRecord>(`/api/cases/${record.id}`, request, { headers: this.headers }))); }
  availableTypes(record: CaseRecord) { return [...new Set([...record.expectedDocumentTypes, ...(this.packet()?.workflow?.types.map(t => t.id) ?? []), ...record.members.map(m => m.documentType)])]; }
}
