import { Injectable, computed, inject, signal } from "@angular/core";
import { HttpClient, HttpErrorResponse } from "@angular/common/http";
import { firstValueFrom } from "rxjs";
import { CatalogEntry, VersionRef, WorkflowCatalog, SplitPolicy } from "./models";

export const splitPolicy = (mode: SplitPolicy["mode"] = "classifier"): SplitPolicy => ({ mode, pageCount: 1, pattern: "" });
export const refKey = (ref: VersionRef | null | undefined) => ref ? `${ref.id}@${ref.version}` : "";
export const apiError = (error: unknown, fallback: string) => {
  const body = error instanceof HttpErrorResponse ? error.error : null;
  return body && typeof body.error === "string" ? `${fallback} ${body.error.slice(0, 1000)}` : fallback;
};
export function normalizeEntry(entry: CatalogEntry): CatalogEntry {
  const value = structuredClone(entry);
  value.description ??= "";
  if (value.kind === "type") value.documentType = {
    keywords: Array.isArray(value.documentType?.keywords) ? value.documentType.keywords : [],
    instructions: value.documentType?.instructions ?? "",
    split: { ...splitPolicy("inherit"), ...value.documentType?.split },
  };
  if (value.kind === "release") value.release = { types: Array.isArray(value.release?.types) ? value.release.types.filter(Boolean) : [] };
  if (value.kind === "workflow") value.workflow = {
    release: value.workflow?.release ?? { id: "", version: 1 },
    mode: value.workflow?.mode ?? "simulation",
    providerProfile: value.workflow?.providerProfile ?? "default",
    model: value.workflow?.model ?? "",
    instructions: value.workflow?.instructions ?? "",
    defaultSplit: { ...splitPolicy(), ...value.workflow?.defaultSplit },
    identifierKinds: Array.isArray(value.workflow?.identifierKinds) ? value.workflow.identifierKinds : [],
    requiredIdentifierKinds: Array.isArray(value.workflow?.requiredIdentifierKinds) ? value.workflow.requiredIdentifierKinds : [],
    ...(value.workflow?.capabilities ? { capabilities: value.workflow.capabilities } : {}),
  };
  return value;
}
@Injectable({ providedIn: "root" })
export class WorkflowStore {
  private http = inject(HttpClient);
  catalog = signal<WorkflowCatalog>({ entries: [], defaultWorkflow: null, policies: [], providerProfiles: [], stats: [], liveModel: "" });
  loading = signal(false);
  error = signal("");
  published = computed(() => this.catalog().entries.filter(e => e.kind === "workflow" && e.status === "published"));
  defaultEntry = computed(() => this.published().find(e => refKey(e) === refKey(this.catalog().defaultWorkflow)));
  async load() {
    this.loading.set(true);
    this.error.set("");
    try {
      const result = await firstValueFrom(this.http.get<WorkflowCatalog>("/api/workflows"));
      this.catalog.set({
        liveModel: result?.liveModel ?? "",
        entries: Array.isArray(result?.entries) ? result.entries.filter(Boolean).map(normalizeEntry) : [],
        defaultWorkflow: result?.defaultWorkflow ?? null,
        policies: Array.isArray(result?.policies) ? result.policies : [],
        providerProfiles: Array.isArray(result?.providerProfiles) ? result.providerProfiles : [],
        stats: Array.isArray(result?.stats) ? result.stats.filter(Boolean) : [],
      });
    } catch (error) { this.error.set(apiError(error, "Could not load workflows. Retry catalog loading.")); }
    finally { this.loading.set(false); }
  }
}
