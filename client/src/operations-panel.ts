import { Component, EventEmitter, Input, OnInit, Output, inject, signal } from "@angular/core";
import { DatePipe } from "@angular/common";
import { FormsModule } from "@angular/forms";
import { HttpClient, HttpErrorResponse } from "@angular/common/http";
import { firstValueFrom } from "rxjs";

interface Identity { name: string; roles: string[]; workflows: string[]; administrator: boolean; }
interface Session { mode: string; authenticated: boolean; identity: Identity | null; }
interface TaskEvent { at: string; actor: string; action: string; revision: number; outcome: string | null; }
interface ReviewTask { id: string; packetId: string; workflowId: string; packetRevision: number; revision: number; state: string; priority: number; createdAt: string; dueAt: string; claimedBy: string | null; leaseUntil: string | null; outcome: string | null; history: TaskEvent[]; }
interface Stats { total: number; available: number; claimed: number; completed: number; overdue: number; }
interface Candidate { packetId: string; revision: number; eligible: boolean; reasons: string[]; }
interface Retention { policy: { minimumAgeDays: number; executionEnabled: boolean }; executionMode: string; candidates: Candidate[]; }
interface PurgePlan { packetId: string; eligible: boolean; blockers: string[]; }

@Component({
  selector: "app-operations-panel",
  standalone: true,
  imports: [FormsModule, DatePipe],
  templateUrl: "./operations-panel.html",
  styleUrl: "./operations-panel.css",
})
export class OperationsPanelComponent implements OnInit {
  @Input() packetId = "";
  @Input() revision = 0;
  @Output() refreshed = new EventEmitter<void>();
  private http = inject(HttpClient);
  private base = "/api/operations-platform";
  session = signal<Session | null>(null);
  tasks = signal<ReviewTask[]>([]);
  stats = signal<Stats | null>(null);
  retention = signal<Retention | null>(null);
  plan = signal<PurgePlan | null>(null);
  error = signal("");
  busy = signal(false);
  token = "";
  showAll = true;
  leaseSeconds = 900;
  schedulePriority = 0;
  scheduleDue = "";
  confirmArchive = false;
  confirmPurge = false;
  administrator(): boolean { return this.session()?.identity?.administrator ?? false; }
  canReview(): boolean { return this.administrator() || (this.session()?.identity?.roles.includes("reviewer") ?? false); }
  visibleTasks(): ReviewTask[] { return this.tasks().filter(t => this.showAll || t.packetId === this.packetId); }
  owned(task: ReviewTask): boolean { return task.state === "claimed" && task.claimedBy === this.session()?.identity?.name; }
  async ngOnInit(): Promise<void> { await this.refresh(); }
  async refresh(): Promise<void> {
    await this.run(async () => {
      const session = await firstValueFrom(this.http.get<Session>(`${this.base}/session`));
      this.session.set(session);
      if (!session.authenticated) { this.tasks.set([]); this.stats.set(null); this.retention.set(null); return; }
      await this.load();
    });
  }
  private async load(): Promise<void> {
    const [tasks, stats] = await Promise.all([
      firstValueFrom(this.http.get<ReviewTask[]>(`${this.base}/tasks`)),
      firstValueFrom(this.http.get<Stats>(`${this.base}/stats`)),
    ]);
    this.tasks.set(tasks); this.stats.set(stats);
    if (this.administrator()) this.retention.set(await firstValueFrom(this.http.get<Retention>(`${this.base}/retention`)));
  }
  async signIn(): Promise<void> {
    const token = this.token;
    this.token = "";
    await this.run(async () => {
      this.session.set(await firstValueFrom(this.http.post<Session>(`${this.base}/session`, {}, { headers: { "X-paprika": "1", Authorization: `Bearer ${token}` } })));
      await this.load(); this.refreshed.emit();
    });
  }
  async signOut(): Promise<void> {
    await this.run(async () => {
      await firstValueFrom(this.http.delete(`${this.base}/session`, { headers: { "X-paprika": "1" } }));
      this.session.set(null); this.tasks.set([]); this.stats.set(null); this.retention.set(null); this.plan.set(null); this.refreshed.emit();
    });
    await this.refresh();
  }
  async change(task: ReviewTask, action: "claim" | "renew" | "release"): Promise<void> {
    await this.run(async () => {
      await firstValueFrom(this.http.post(`${this.base}/tasks/${encodeURIComponent(task.id)}/${action}`, { revision: task.revision, leaseSeconds: this.leaseSeconds }, { headers: { "X-paprika": "1" } }));
      await this.load(); this.refreshed.emit();
    });
  }
  async openReview(): Promise<void> {
    await this.run(async () => {
      await firstValueFrom(this.http.post(`${this.base}/tasks/open`, { packetId: this.packetId, revision: this.revision }, { headers: { "X-paprika": "1" } }));
      await this.load(); this.refreshed.emit();
    });
  }
  async schedule(task: ReviewTask): Promise<void> {
    await this.run(async () => {
      if (!this.scheduleDue) throw new Error("Choose a due date first.");
      await firstValueFrom(this.http.put(`${this.base}/tasks/${encodeURIComponent(task.id)}/schedule`, { revision: task.revision, priority: this.schedulePriority, dueAt: new Date(this.scheduleDue).toISOString() }, { headers: { "X-paprika": "1" } }));
      await this.load();
    });
  }
  async archive(candidate: Candidate): Promise<void> {
    if (!this.confirmArchive) return;
    await this.run(async () => {
      await firstValueFrom(this.http.post(`${this.base}/retention/execute`, { packetId: candidate.packetId, revision: candidate.revision, confirmArchive: true }, { headers: { "X-paprika": "1" } }));
      this.confirmArchive = false; await this.load(); this.refreshed.emit();
    });
  }
  async previewPurge(id: string): Promise<void> {
    await this.run(async () => this.plan.set(await firstValueFrom(this.http.get<PurgePlan>(`${this.base}/retention/${encodeURIComponent(id)}/purge-plan`))));
  }
  async purge(candidate: Candidate): Promise<void> {
    if (!this.confirmPurge || !this.plan()?.eligible || this.plan()?.packetId !== candidate.packetId) return;
    await this.run(async () => {
      await firstValueFrom(this.http.post(`${this.base}/retention/purge`, { packetId: candidate.packetId, revision: candidate.revision, confirmPermanentDeletion: true }, { headers: { "X-paprika": "1" } }));
      this.confirmPurge = false; this.plan.set(null); await this.load(); this.refreshed.emit();
    });
  }
  private async run(action: () => Promise<unknown>): Promise<void> {
    if (this.busy()) return;
    this.busy.set(true); this.error.set("");
    try { await action(); }
    catch (error) { this.error.set(error instanceof HttpErrorResponse ? error.error?.error ?? `Operation failed (${error.status}). Refresh before retrying.` : error instanceof Error ? error.message : "Operation failed."); }
    finally { this.busy.set(false); }
  }
}
