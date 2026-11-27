import { Packet, Page } from "./models";

export const correctionLabels = {
  "wrong-selection": "Wrong text selected",
  transcription: "Text read incorrectly",
  omit: "Omit from delivery",
};
export type Category = keyof typeof correctionLabels;
export type FieldKind = "text" | "number" | "boolean" | "date";
export interface Evidence { pageId: string; offset: number; length: number; text: string; textSha256: string }
export interface FieldValue { name: string; value: string | null; status: string; evidence: Evidence[] }
interface FieldDefinition { name: string; required: boolean; kind: FieldKind }
interface Result { documentId: string; fields: FieldValue[]; tables: unknown[] }
export interface ExtractionRun {
  id: string;
  packetId: string;
  packetRevision: number;
  definition: { id: string; version: number; fields: FieldDefinition[] };
  results: Result[];
}
interface HistoryEntry {
  previousRunId: string;
  resultId: string;
  documentIds?: string[];
  at: string;
  actor: string;
  correction?: { documentId: string; field: string; reason: string; category?: Category | null };
  before?: FieldValue;
  after?: FieldValue;
}
export interface ExtractionHistory {
  currentConfiguredRunId: string | null;
  runs: ExtractionRun[];
  corrections: HistoryEntry[];
}
interface FieldChange {
  resultId: string;
  revision: number;
  category: Category | null;
  note: string;
  before?: FieldValue;
  after?: FieldValue;
  at: string;
  actor: string;
}
export interface ReviewedField {
  definition: FieldDefinition;
  final: FieldValue;
  source?: FieldValue;
  changes: FieldChange[];
}

// Trace only explicit parent links. A new extraction of a regrouped document
// is a new prediction, even if its document ID and field names stayed the same.
export function reviewDocuments(packet: Packet, run: ExtractionRun, history: ExtractionHistory, documentId: string) {
  const runs = new Map(history.runs.map(item => [item.id, item]));
  const parents = new Map(history.corrections.map(entry => [entry.resultId, entry]));
  const definitions = new Map(run.definition.fields.map(field => [field.name, field]));
  return run.results.filter(result => !documentId || result.documentId === documentId).map(result => {
    const id = result.documentId;
    const changes = new Map<string, FieldChange[]>();
    let ancestor = run;
    let original = result;
    const visited = new Set<string>();
    while (!visited.has(ancestor.id)) {
      visited.add(ancestor.id);
      const link = parents.get(ancestor.id);
      if (!link || (link.documentIds && !link.documentIds.includes(id))) break;
      const previous = runs.get(link.previousRunId);
      const previousResult = previous?.results.find(item => item.documentId === id);
      const correction = link.correction;
      if (correction?.documentId === id) {
        const field = correction.field;
        const entries = changes.get(field) ?? [];
        entries.push({
          resultId: link.resultId, revision: ancestor.packetRevision,
          category: correction.category ?? null, note: correction.reason, at: link.at, actor: link.actor,
          before: link.before ?? previousResult?.fields.find(value => value.name === field),
          after: link.after ?? original.fields.find(value => value.name === field),
        });
        changes.set(field, entries);
      }
      if (!previous || !previousResult) break;
      ancestor = previous;
      original = previousResult;
    }
    const fields: ReviewedField[] = result.fields.flatMap(final => {
      const definition = definitions.get(final.name);
      if (!definition) return [];
      const entries = changes.get(final.name) ?? [];
      const first = entries.at(-1)?.before ?? original.fields.find(field => field.name === final.name);
      return [{ definition, final, changes: entries, source: first?.status === "corrected" ? undefined : first }];
    });
    const index = packet.documents.findIndex(doc => doc.id === id);
    const doc = packet.documents[index];
    return { id, index, type: doc?.type, fields, tables: result.tables };
  });
}

// Keep decimals as strings. A JavaScript number loses significant digits and
// decimal scale before the server gets a chance to normalize the field.
export function normalizedValue(input: string, kind: FieldKind): string | null {
  const value = input.trim();
  if (!value) return null;
  switch (kind) {
    case "number": {
      const match = /^([+-]?)(\d[\d,]*)?(?:\.(\d*))?([+-]?)$/.exec(value);
      if (!match || (match[1] && match[4]) || (!match[2] && !match[3])) return null;
      const whole = (match[2] ?? "").replaceAll(",", "").replace(/^0+/, "") || "0";
      const fraction = match[3] ?? "";
      const negative = (match[1] || match[4]) === "-" && (whole !== "0" || /[1-9]/.test(fraction));
      return (negative ? "-" : "") + whole + (fraction ? "." + fraction : "");
    }
    case "date": {
      const iso = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
      const us = /^(\d{2})\/(\d{2})\/(\d{4})$/.exec(value);
      const date = iso ? value : us ? `${us[3]}-${us[1]}-${us[2]}` : "";
      const parsed = new Date(date + "T00:00:00Z");
      return date && !date.startsWith("0000") && Number.isFinite(parsed.getTime()) && parsed.toISOString().startsWith(date + "T") ? date : null;
    }
    case "boolean":
      if (/^(yes|true)$/i.test(value)) return "true";
      if (/^(no|false)$/i.test(value)) return "false";
      return null;
    default: return value;
  }
}

// Textarea selectionStart/End count UTF-16 code units, as does .NET, but
// textarea values collapse CRLF to LF. Map both boundaries to the original text.
export function selectedSpan(page: Page, start: number, end: number): Omit<Evidence, "textSha256"> | null {
  if (end <= start) return null;
  let offset = start;
  let limit = end;
  for (let cr = page.text.indexOf("\r\n"); cr !== -1 && cr < limit; cr = page.text.indexOf("\r\n", cr + 2)) {
    if (cr < offset) offset++;
    limit++;
  }
  const text = page.text.slice(offset, limit);
  return limit <= page.text.length && text.trim() ? { pageId: page.id, offset, length: limit - offset, text } : null;
}

export async function pageHash(text: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text));
  return Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, "0")).join("");
}
