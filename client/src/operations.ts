import { Component, DestroyRef, inject, signal } from "@angular/core";
import {
  HttpClient,
  HttpErrorResponse,
  HttpHeaders,
} from "@angular/common/http";
import { DatePipe } from "@angular/common";
import { MatButtonModule } from "@angular/material/button";
import { firstValueFrom } from "rxjs";
import { DeadLetter, Messaging, Operations } from "./models";

@Component({
  selector: "app-operations",
  standalone: true,
  imports: [DatePipe, MatButtonModule],
  template: `
    <div class="operations-summary">
      <button
        mat-button
        data-testid="operations-toggle"
        (click)="toggle()"
        [attr.aria-expanded]="open()"
        aria-controls="operations-content"
      >
        {{ open() ? "Hide" : "View" }} local operations
      </button>
      @if (open()) {
        <section id="operations-content" aria-label="Local operations summary">
          <button
            mat-button
            data-testid="messaging-refresh"
            (click)="refresh()"
            [disabled]="loading() || replaying() !== null"
          >
            Refresh summary
          </button>
          @if (loading()) {
            <p class="small" role="status">Refreshing…</p>
          }
          @if (error()) {
            <p class="small" role="alert">{{ error() }}</p>
          }
          @if (metrics(); as m) {
            <p class="small">
              LOCAL · {{ m.packets.total }} packets · {{ m.pages }} pages ·
              {{ m.documents }} documents · {{ m.reviewCorrections }} review
              corrections
            </p>
            <p class="small muted">
              Packets: {{ m.packets.queued }} queued ·
              {{ m.packets.processing }} processing · {{ m.packets.review }} in
              review · {{ m.packets.approved }} approved ·
              {{ m.packets.failed }} failed ·
              {{ m.packets.cancelled }} cancelled
            </p>
            <p class="small muted">
              Simulated deliveries: {{ m.deliveries.queued }} queued ·
              {{ m.deliveries.processing }} processing ·
              {{ m.deliveries.delivered }} delivered ·
              {{ m.deliveries.failed }} failed ·
              {{ m.deliveries.rejected }} rejected
            </p>
          }
          <h3>Durable local messaging</h3>
          <p class="small muted">
            Local processing with automatic transient retries. Simulated
            deliveries stay local; nothing is sent downstream.
          </p>
          @if (messagingError()) {
            <p class="small" role="alert">{{ messagingError() }}</p>
          }
          @if (replayError()) {
            <p class="small" role="alert">{{ replayError() }}</p>
          }
          @if (replayNotice()) {
            <p class="small" role="status">{{ replayNotice() }}</p>
          }
          @if (messaging(); as messaging) {
            @for (queue of messaging.queues; track queue.name) {
              <p class="small" data-testid="messaging-queue">
                <strong>{{ queue.name }}</strong> · {{ queue.pending }} pending
              </p>
            }
            <h4>Dead letters</h4>
            <p class="small muted">
              Up to 100 failed messages. Refresh to check processing outcomes.
              Replay queues another local attempt; it does not guarantee
              success.
            </p>
            @for (item of messaging.deadLetters; track item.id) {
              <article class="deadletter-item" data-testid="deadletter-item">
                <p class="small">
                  <strong>{{ item.messageType }}</strong> ·
                  {{ item.failedAt | date: "medium" }}
                </p>
                <p class="small">{{ item.reason }}</p>
                <p class="small muted">
                  Message: {{ item.id }}
                  @if (item.packetId) {
                    <br />Packet: {{ item.packetId }}
                  }
                  @if (item.deliveryId) {
                    <br />Delivery: {{ item.deliveryId }}
                  }
                </p>
                <button
                  mat-button
                  data-testid="replay-deadletter"
                  (click)="replay(item)"
                  [disabled]="
                    loading() || replaying() !== null || isReplayQueued(item)
                  "
                >
                  {{
                    replaying() === item.id
                      ? "Queuing replay…"
                      : isReplayQueued(item)
                        ? "Replay queued"
                        : "Replay dead letter"
                  }}
                </button>
              </article>
            } @empty {
              <p class="small muted">No dead letters.</p>
            }
          }
        </section>
      }
    </div>
  `,
})
export class OperationsComponent {
  private http = inject(HttpClient);
  private headers = new HttpHeaders({ "X-PaperworkClassifier": "1" });
  private generation = 0;
  private disposed = false;
  private queuedReplays = signal<Record<string, string>>({});
  open = signal(false);
  loading = signal(false);
  metrics = signal<Operations | null>(null);
  messaging = signal<Messaging | null>(null);
  replaying = signal<string | null>(null);
  error = signal("");
  messagingError = signal("");
  replayError = signal("");
  replayNotice = signal("");
  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.disposed = true;
      ++this.generation;
    });
  }
  toggle() {
    this.open.update((value) => !value);
    if (this.open()) void this.refresh();
    else {
      ++this.generation;
      this.loading.set(false);
    }
  }
  async refresh() {
    if (this.disposed || !this.open() || this.loading() || this.replaying())
      return;
    const generation = ++this.generation;
    this.loading.set(true);
    this.error.set("");
    this.messagingError.set("");
    const [operations, messaging] = await Promise.allSettled([
      firstValueFrom(this.http.get<Operations>("/api/operations")),
      firstValueFrom(this.http.get<Messaging>("/api/messaging")),
    ]);
    if (this.disposed || generation !== this.generation) return;
    if (operations.status === "fulfilled") this.metrics.set(operations.value);
    else this.error.set("Could not load local operations. Try refreshing.");
    if (messaging.status === "fulfilled") {
      this.messaging.set(messaging.value);
      this.queuedReplays.update((queued) =>
        Object.fromEntries(
          messaging.value.deadLetters
            .filter((item) => queued[item.id] === item.failedAt)
            .map((item) => [item.id, item.failedAt]),
        ),
      );
    } else {
      this.messagingError.set(
        "Could not load messaging. Displayed counts and dead letters may be out of date. Try refreshing.",
      );
    }
    this.loading.set(false);
  }
  isReplayQueued(item: DeadLetter) {
    return this.queuedReplays()[item.id] === item.failedAt;
  }
  async replay(item: DeadLetter) {
    if (
      this.disposed ||
      !this.open() ||
      this.loading() ||
      this.replaying() ||
      this.isReplayQueued(item)
    )
      return;
    this.replaying.set(item.id);
    this.replayError.set("");
    this.replayNotice.set("");
    try {
      await firstValueFrom(
        this.http.post<{ id: string; status: "queued" }>(
          `/api/messaging/deadletters/${encodeURIComponent(item.id)}/replay`,
          {},
          { headers: this.headers },
        ),
      );
      if (this.disposed) return;
      this.queuedReplays.update((queued) => ({
        ...queued,
        [item.id]: item.failedAt,
      }));
      this.replayNotice.set(
        "Replay queued for local processing. Refresh to check its outcome.",
      );
    } catch (error) {
      if (this.disposed) return;
      this.replayError.set(
        error instanceof HttpErrorResponse && error.status === 404
          ? "This dead letter is no longer available. Check the refreshed list."
          : error instanceof HttpErrorResponse && error.status === 409
            ? "This message changed or is terminal and cannot be replayed. Check the refreshed list."
            : "Could not confirm replay. Refresh and check the message before trying again.",
      );
    } finally {
      this.replaying.set(null);
      if (!this.disposed && this.open()) await this.refresh();
    }
  }
}
