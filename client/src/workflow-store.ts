import { Injectable, computed, inject, signal } from "@angular/core";
import { HttpClient, HttpErrorResponse } from "@angular/common/http";
import { firstValueFrom } from "rxjs";
import { CatalogEntry, ClassifierId, ClassifierProvider, LegacyClassifierId, VersionRef, WorkflowCatalog, WorkflowConfig, SplitPolicy } from "./models";

export const splitPolicy = (mode: SplitPolicy["mode"] = "classifier"): SplitPolicy => ({ mode, pageCount: 1, pattern: "" });
export const refKey = (ref: VersionRef | null | undefined) => ref ? `${ref.id}@${ref.version}` : "";
export const classifierNames: Record<ClassifierId, string> = { jev: "Jev", openai: "OpenAI Decisions", custom: "Self-hosted classifier" };
type Binding = Pick<WorkflowConfig, "providerProfile" | "model" | "mode">;
type BindingProjection = Pick<CatalogEntry, "resolvedClassifierProvider" | "classifierBindingUnavailable">;
const isLegacyClassifierId = (id: unknown): id is LegacyClassifierId => id === "jev" || id === "openai";
const isClassifierId = (id: unknown): id is ClassifierId => id === "custom" || isLegacyClassifierId(id);

export function classifierInfo(workflow: Binding, providers: ClassifierProvider[], projection: BindingProjection) {
  // The server resolves legacy models; a self-hosted classifier always needs an explicit pin.
  const profile = workflow.providerProfile;
  const resolvedLegacy = projection.resolvedClassifierProvider;
  const id = isClassifierId(profile) ? profile
    : (profile === "default" || profile == null) && isLegacyClassifierId(resolvedLegacy) ? resolvedLegacy : undefined;
  const provider = providers.find(p => p.id === id);
  const modelMatches = !!provider && provider.model === workflow.model && !!workflow.model;
  const bindingUnavailable = id === "custom" && projection.classifierBindingUnavailable === true;
  const ready = modelMatches && provider.configured && !bindingUnavailable;
  const name = id ? classifierNames[id] : "Unresolved classifier";
  const status = !workflow.model ? "Choose a classifier to pin its model."
    : !id ? `${name} · ${workflow.model}. This model needs an explicit provider.`
    : `${name} · ${workflow.model} · ${!modelMatches ? "Pinned model unavailable" : bindingUnavailable ? "Pinned endpoint unavailable" : ready ? "Configured" : "Not configured"}`;
  return { id, name, provider, modelMatches, bindingUnavailable, ready, status };
}
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
  private readonly empty: WorkflowCatalog = { entries: [], defaultWorkflow: null, policies: [], providerProfiles: [], classifierProviders: [], stats: [], liveModel: "", canUpload: false };
  catalog = signal(this.empty);
  clear() { this.catalog.set(this.empty); }
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
        canUpload: result?.canUpload === true,
        liveModel: result?.liveModel ?? "",
        entries: Array.isArray(result?.entries) ? result.entries.filter(Boolean).map(normalizeEntry) : [],
        defaultWorkflow: result?.defaultWorkflow ?? null,
        policies: Array.isArray(result?.policies) ? result.policies : [],
        providerProfiles: Array.isArray(result?.providerProfiles) ? result.providerProfiles : [],
        classifierProviders: Array.isArray(result?.classifierProviders) ? result.classifierProviders.filter(p => p && isClassifierId(p.id)) : [],
        stats: Array.isArray(result?.stats) ? result.stats.filter(Boolean) : [],
      });
    } catch (error) { this.clear(); this.error.set(apiError(error, "Could not load workflows. Retry catalog loading.")); }
    finally { this.loading.set(false); }
  }
}
