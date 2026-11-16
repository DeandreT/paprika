import {
  Component,
  DestroyRef,
  effect,
  inject,
  input,
  signal,
} from "@angular/core";
import { HttpClient, HttpHeaders } from "@angular/common/http";
import { DatePipe } from "@angular/common";
import { FormsModule } from "@angular/forms";
import { MatButtonModule } from "@angular/material/button";
import { firstValueFrom } from "rxjs";
import { DeliveryResponse, Packet } from "./models";

@Component({
  selector: "app-delivery",
  standalone: true,
  imports: [DatePipe, FormsModule, MatButtonModule],
  template: `
    <section class="delivery-panel" aria-label="Simulated delivery">
      <h3>Simulated delivery</h3>
      <p class="simulation-notice">
        SIMULATED DELIVERY · Local artifacts only · Nothing sent downstream
      </p>
      <div class="delivery-actions">
        <label
          >Simulation scenario<select
            data-testid="delivery-scenario"
            [ngModel]="scenario()"
            (ngModelChange)="scenario.set($event)"
            [disabled]="working()"
          >
            <option value="success">Success</option>
            <option value="transient-once">
              Transient failure, then automatic retry
            </option>
            <option value="transient-always">
              Repeated transient failure (dead letter)
            </option>
            <option value="reject">Reject (terminal)</option>
          </select></label
        >
        <button
          mat-stroked-button
          data-testid="queue-delivery"
          (click)="queue()"
          [disabled]="
            packet().testRun || packet().status !== 'approved' || dirty() || busy() || working()
          "
        >
          Queue simulated delivery
        </button>
      </div>
      @if (packet().testRun) { <p class="small muted">Test previews support review and export. Simulated delivery is unavailable for test runs.</p> }
      @if (packet().status !== "approved" || dirty()) {
        <p class="small muted">
          Save and approve the current review before queuing delivery.
        </p>
      }
      @if (error()) {
        <p role="alert" class="small">
          {{ error() }}
          <button mat-button (click)="refresh()" [disabled]="working()">
            Refresh deliveries
          </button>
        </p>
      }
      @for (item of deliveries(); track item.id) {
        <article class="delivery-item" data-testid="delivery-item">
          <p>
            <strong>Revision {{ item.revision }}</strong> ·
            <span class="badge">{{ item.status }}</span> ·
            {{ item.attempts }} attempt(s)
          </p>
          <p class="small muted">
            {{ item.createdAt | date: "medium" }} · {{ item.scenario }}
          </p>
          @if (item.supersedesDeliveryId) {
            <p class="small">
              Supersedes delivery {{ item.supersedesDeliveryId }}
            </p>
          }
          @if (item.error) {
            <p class="small">{{ item.error }}</p>
          }
          @if (item.status === "failed") {
            <p class="small muted">
              For exhausted message retries, use dead-letter replay in local
              operations. Repeated transient failures can fail again after
              replay.
            </p>
            <button
              mat-button
              data-testid="retry-delivery"
              (click)="retry(item)"
               [disabled]="working() || busy() || packet().testRun"
            >
              Retry simulated delivery
            </button>
          }
          @if (item.status === "rejected") {
            <p class="small">
              Terminal simulated rejection. This delivery cannot be retried.
              Review the packet and queue a new simulated delivery when ready.
            </p>
          }
          @if (item.status === "delivered" && item.artifactUrl) {
            <a
              mat-button
              data-testid="delivery-download"
              [href]="item.artifactUrl"
              >↓ Download receipt / artifact ZIP</a
            >
          }
          <details>
            <summary class="small">Delivery details</summary>
            <p class="small">
              ID: {{ item.id }}<br />Updated:
              {{ item.updatedAt | date: "medium" }}<br />SHA-256:
              {{ item.sha256 || "Not available" }}
            </p>
          </details>
        </article>
      } @empty {
        <p class="small muted">No simulated deliveries for this run.</p>
      }
    </section>
  `,
})
export class DeliveryComponent {
  packet = input.required<Packet>();
  dirty = input(false);
  busy = input(false);
  private http = inject(HttpClient);
  private headers = new HttpHeaders({ "X-paprika": "1" });
  private timer?: ReturnType<typeof setTimeout>;
  private generation = 0;
  private disposed = false;
  deliveries = signal<DeliveryResponse[]>([]);
  scenario = signal<DeliveryResponse["scenario"]>("success");
  working = signal(false);
  error = signal("");
  constructor() {
    effect(() => {
      const id = this.packet().id;
      ++this.generation;
      clearTimeout(this.timer);
      this.deliveries.set([]);
      this.error.set("");
      void this.refresh(id);
    });
    inject(DestroyRef).onDestroy(() => {
      this.disposed = true;
      ++this.generation;
      clearTimeout(this.timer);
    });
  }
  async refresh(id = this.packet().id) {
    const generation = this.generation;
    clearTimeout(this.timer);
    try {
      const result = await firstValueFrom(
        this.http.get<DeliveryResponse[]>(
          `/api/packets/${encodeURIComponent(id)}/deliveries`,
        ),
      );
      if (
        this.disposed ||
        generation !== this.generation ||
        id !== this.packet().id
      )
        return;
      this.deliveries.set(
        (Array.isArray(result) ? result : []).sort((a, b) => b.createdAt.localeCompare(a.createdAt)),
      );
      this.error.set("");
    } catch {
      if (this.disposed || generation !== this.generation) return;
      this.error.set("Could not refresh simulated deliveries. Try again.");
    }
    if (
      !this.disposed &&
      generation === this.generation &&
      this.deliveries().some(
        (d) => d.status === "queued" || d.status === "processing",
      )
    )
      this.timer = setTimeout(() => void this.refresh(id), 2000);
  }
  async queue() {
    const packet = this.packet();
    if (
      packet.testRun ||
      packet.status !== "approved" ||
      this.dirty() ||
      this.busy() ||
      this.working()
    )
      return;
    await this.send(
      `/api/packets/${encodeURIComponent(packet.id)}/deliveries`,
      { revision: packet.revision, scenario: this.scenario() },
    );
  }
  async retry(item: DeliveryResponse) {
    if (this.packet().testRun || item.status !== "failed" || this.busy() || this.working()) return;
    await this.send(`/api/deliveries/${encodeURIComponent(item.id)}/retry`, {});
  }
  private async send(url: string, body: unknown) {
    const id = this.packet().id;
    this.working.set(true);
    this.error.set("");
    try {
      await firstValueFrom(
        this.http.post<DeliveryResponse>(url, body, { headers: this.headers }),
      );
      if (this.packet().id === id) await this.refresh(id);
    } catch {
      if (this.packet().id === id)
        this.error.set(
          "Could not update simulated delivery. Refresh deliveries before trying again; another session may have changed this revision.",
        );
    } finally {
      this.working.set(false);
    }
  }
}
