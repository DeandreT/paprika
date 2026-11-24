export interface Config {
  mode: "simulation" | "demo" | "live";
  ocrConfigured: boolean;
  classifierConfigured: boolean;
  documentTypes: { id: string; label: string }[];
  maxUploadMb: number;
}
export interface Page {
  id: string;
  sourceId: string;
  sourceName: string;
  pageNumber: number;
  previewUrl: string;
  text: string;
  predictedType: string;
  confidence: number;
  startProbability: number;
  reviewReasons: string[];
}
export interface Document {
  id: string;
  type: string;
  pageIds: string[];
  businessReference: string;
  reviewReasons: string[];
}
export interface Packet {
  workflow?: WorkflowSnapshot;
  testRun: boolean;
  id: string;
  name: string;
  status:
    "queued" | "processing" | "review" | "approved" | "failed" | "cancelled";
  parentPacketId: string | null;
  runNumber: number;
  identifierCandidates: IdentifierCandidate[];
  mode: "simulation" | "demo" | "live";
  createdAt: string;
  revision: number;
  error?: string;
  pages: Page[];
  documents: Document[];
  processing?: { stage: string; model?: string; elapsedMs?: number };
  audit?: { at: string; action: string; revision: number }[];
}
export interface VersionRef { id: string; version: number }
export type CatalogKind = "type" | "release" | "workflow";
export type SplitMode = "inherit" | "classifier" | "separate" | "consecutive" | "manual" | "fixed" | "first" | "last" | "identifier";
export interface SplitPolicy { mode: SplitMode; pageCount: number; pattern: string }
export interface TypeConfig { keywords: string[]; instructions: string; split: SplitPolicy }
export interface WorkflowCapabilities {
  quality?: Record<string, unknown> | null;
  training?: Record<string, unknown> | null;
  reference?: Record<string, unknown> | null;
  operations?: Record<string, unknown> | null;
  extraction?: Record<string, unknown> | null;
  recognition?: Record<string, unknown> | null;
  preprocessing?: Record<string, unknown> | null;
  output?: Record<string, unknown> | null;
  flow?: Record<string, unknown> | null;
  case?: Record<string, unknown> | null;
}
export interface WorkflowConfig {
  release: VersionRef;
  mode: "simulation" | "live";
  providerProfile: string;
  model: string;
  instructions: string;
  defaultSplit: SplitPolicy;
  identifierKinds: string[];
  requiredIdentifierKinds: string[];
  capabilities?: WorkflowCapabilities | null;
}
export interface CatalogEntry extends VersionRef {
  resolvedClassifierProvider?: LegacyClassifierId;
  classifierBindingNeedsRepair?: boolean;
  classifierBindingUnavailable?: boolean;
  kind: CatalogKind;
  revision: number;
  status: "draft" | "published" | "archived";
  name: string;
  description: string;
  documentType?: TypeConfig;
  release?: { types: VersionRef[] };
  workflow?: WorkflowConfig;
}
export interface WorkflowStats extends VersionRef {
  packets: number; review: number; failed: number; approved: number; corrections: number;
}
export type LegacyClassifierId = "jev" | "openai";
export type ClassifierId = LegacyClassifierId | "custom";
export interface ClassifierProvider { id: ClassifierId; model: string; configured: boolean }
export interface WorkflowCatalog {
  canUpload: boolean;
  liveModel: string;
  entries: CatalogEntry[];
  defaultWorkflow: VersionRef | null;
  policies: string[];
  providerProfiles: string[];
  classifierProviders: ClassifierProvider[];
  stats: WorkflowStats[];
}
export interface WorkflowSnapshot extends VersionRef {
  name: string;
  releaseId: string;
  releaseVersion: number;
  hash: string;
  mode: "simulation" | "live";
  providerProfile: string;
  model: string;
  instructions: string;
  defaultSplit: SplitPolicy;
  identifierKinds: string[];
  requiredIdentifierKinds: string[];
  types: (VersionRef & TypeConfig & { name: string; description: string })[];
  capabilities?: WorkflowCapabilities | null;
}
export interface PacketRevision {
  revision: number;
  status: string;
  at: string;
  documents: Document[];
  action: string;
}
export interface IdentifierCandidate {
  kind: string;
  value: string;
  pageIds: string[];
  evidence: string;
}
export interface DeliveryResponse {
  id: string;
  packetId: string;
  revision: number;
  status: "queued" | "processing" | "delivered" | "failed" | "rejected";
  mode: "simulation";
  scenario: "success" | "transient-once" | "transient-always" | "reject";
  attempts: number;
  createdAt: string;
  updatedAt: string;
  error: string | null;
  artifactUrl: string | null;
  sha256: string | null;
  supersedesDeliveryId: string | null;
}
export interface Operations {
  mode: "local";
  packets: Record<Packet["status"] | "total", number>;
  pages: number;
  documents: number;
  deliveries: Record<DeliveryResponse["status"], number>;
  reviewCorrections: number;
}
export interface DeadLetter {
  id: string;
  messageType: string;
  packetId: string | null;
  deliveryId: string | null;
  failedAt: string;
  reason: string;
}
export interface Messaging {
  mode: "durable-local";
  queues: { name: string; pending: number }[];
  deadLetters: DeadLetter[];
}
