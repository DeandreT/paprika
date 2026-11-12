import { Component, computed, input, output } from "@angular/core";
import { FormsModule } from "@angular/forms";
import { SplitMode, SplitPolicy } from "./models";

@Component({
  selector: "app-split-editor",
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="form-grid">
      <label>{{ label() }}<select [ngModel]="policy().mode" (ngModelChange)="change('mode', $event)" [disabled]="disabled()">
        @if (inherit()) { <option value="inherit">Inherit workflow default</option> }
        <option value="classifier">Classifier boundaries</option>
        <option value="separate">Every page separately</option>
        <option value="consecutive">Consecutive pages of the same type</option>
        <option value="manual">Manual review</option>
        <option value="fixed">Fixed page count</option>
        <option value="first">First-page marker</option>
        <option value="last">Last-page marker</option>
        <option value="identifier">Identifier change</option>
      </select></label>
      @if (policy().mode === 'fixed') {
        <label>Pages per document<input type="number" min="1" max="10000" step="1" [ngModel]="policy().pageCount" (ngModelChange)="change('pageCount', $event)" [disabled]="disabled()" /></label>
      }
    </div>
    <div class="split-explanation" data-testid="split-explanation" aria-live="polite">
      <p>{{ explanation().description }}</p>
      <p class="small muted">Example: {{ explanation().example }}</p>
    </div>
    @if (['first', 'last', 'identifier'].includes(policy().mode)) {
      <label>Marker pattern<input [ngModel]="policy().pattern" (ngModelChange)="change('pattern', $event)" [disabled]="disabled()" placeholder="Order\\s*#?\\s*([A-Z0-9-]+)" /></label>
      <details class="small"><summary>Pattern help</summary><p>Use a .NET regular expression matched against page text. Identifier change uses the first captured group.</p><code>{{ policy().mode === 'identifier' ? 'Order\\s*#?\\s*([A-Z0-9-]+)' : policy().mode === 'first' ? 'Page\\s+1\\s+of' : 'End of document' }}</code></details>
    }
    <p class="small muted">Uploaded files stay separate. Document-type overrides take precedence.</p>
  `,
})
export class SplitEditorComponent {
  policy = input.required<SplitPolicy>();
  label = input("Split policy");
  inherit = input(false);
  disabled = input(false);
  policyChange = output<SplitPolicy>();
  private readonly explanations: Record<SplitMode, { description: string; example: string }> = {
    inherit: { description: "Use the workflow's default split policy for this document type.", example: "If the workflow uses fixed groups of 2 pages, this type does too." },
    classifier: { description: "Start a document where the classifier predicts a new document begins. Simulation uses local rules.", example: "Pages 1 and 3 are predicted starts: pages 1–2 form one document, page 3 starts the next." },
    separate: { description: "Make every page its own document.", example: "A 3-page upload becomes 3 documents." },
    consecutive: { description: "Group adjacent pages with the same predicted type. A type change starts a new document.", example: "Invoice, invoice, receipt becomes a 2-page invoice and a 1-page receipt." },
    manual: { description: "Start with one document per page, then group the pages yourself in review.", example: "Merge pages 1 and 2 when they belong to the same invoice." },
    fixed: { description: "Group a fixed number of consecutive pages of the same type. Short groups are flagged for review.", example: "With 2 pages per document, 5 invoice pages become groups of 2, 2 and 1." },
    first: { description: "Start a new document whenever a page matches the first-page pattern.", example: "Match 'Page 1 of' to start each new multi-page invoice." },
    last: { description: "End the current document after a page matches the last-page pattern.", example: "Match 'End of document' to keep that page with the document it closes." },
    identifier: { description: "Start a new document when the captured identifier changes. Missing or conflicting values are flagged for review.", example: "Order A100 on two pages, then A200: the third page starts a new document." },
  };
  explanation = computed(() => this.explanations[this.policy().mode]);
  change(field: keyof SplitPolicy, value: string | number) {
    const policy = { ...this.policy(), [field]: value };
    if (field === "mode") {
      if (value !== "fixed") policy.pageCount = 1;
      if (!["first", "last", "identifier"].includes(String(value))) policy.pattern = "";
    }
    this.policyChange.emit(policy);
  }
}
