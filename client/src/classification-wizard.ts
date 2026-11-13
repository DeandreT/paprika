import { Component, computed, input, signal } from "@angular/core";
import { FormsModule } from "@angular/forms";
import { MatButtonModule } from "@angular/material/button";
import { MatProgressBarModule } from "@angular/material/progress-bar";
import { PagePreviewComponent } from "./pdf-preview";
import type { AppComponent } from "./app";

// A focused view of the existing workspace controller: uploads, revisions and approval
// use the same operations as Advanced, including pending-upload and stale-edit guards.
@Component({
  selector: "app-classification-wizard",
  standalone: true,
  imports: [FormsModule, MatButtonModule, MatProgressBarModule, PagePreviewComponent],
  templateUrl: "./classification-wizard.html",
})
export class ClassificationWizardComponent {
  workspace = input.required<AppComponent>();
  choosingPacket = signal(false);
  search = signal("");
  detailsOpen = signal(false);
  zoom = signal(1);
  reviewing = computed(() => this.workspace().classificationStage() === 'review'
    && ['review', 'approved'].includes(this.workspace().packet()?.status ?? '')
    && !!this.workspace().selectedDoc());
  changeZoom(delta: number) { this.zoom.update(value => Math.min(3, Math.max(1, value + delta))); }
  readonly stages = ["upload", "review", "approve", "export"];
  readonly labels = ["Upload", "Review", "Approve", "Export"];
  step = computed(() => this.stages.indexOf(this.workspace().classificationStage()));
  documentIndex = computed(() => this.workspace().documents().findIndex(d => d.id === this.workspace().selectedDoc()?.id));
  pageIndex = computed(() => this.workspace().selectedDoc()?.pageIds.indexOf(this.workspace().selectedId()) ?? -1);
  packets = computed(() => this.workspace().packets().filter(p => p.name.toLowerCase().includes(this.search().toLowerCase())));

  async open(id: string) {
    await this.workspace().open(id);
    if (this.workspace().packet()?.id === id) this.choosingPacket.set(false);
  }
  movePage(delta: number) {
    const w = this.workspace();
    const id = w.selectedDoc()?.pageIds[this.pageIndex() + delta];
    if (id) w.selectedId.set(id);
  }
  finishReview() {
    const w = this.workspace();
    if (w.editable() && w.documents().length) w.classificationStage.set("approve");
  }
  async approve() {
    const w = this.workspace();
    await w.save(true);
    if (w.packet()?.status === "approved" && !w.dirty()) w.classificationStage.set("export");
  }
}
