import { test, expect, type APIRequestContext, type APIResponse } from '@playwright/test';
import { randomUUID } from 'node:crypto';
import { inflateRawSync } from 'node:zlib';
import { textPdf } from './fixtures';

const headers = { 'X-paprika': '1' };
type Ref = { id: string; version: number };
type Split = { mode: string; pageCount: number; pattern: string };
type TypeConfig = { keywords: string[]; instructions: string; split: Split };
type WorkflowConfig = {
  release: Ref; mode: string; providerProfile: string; model: string; instructions: string;
  defaultSplit: Split; identifierKinds: string[]; requiredIdentifierKinds: string[];
};
type Entry = Ref & {
  kind: 'type' | 'release' | 'workflow'; revision: number; status: string;
  name: string; description: string; documentType?: TypeConfig;
  release?: { types: Ref[] }; workflow?: WorkflowConfig;
};
type Snapshot = Ref & {
  hash: string; releaseId: string; releaseVersion: number; instructions: string;
  defaultSplit: Split; types: (Ref & TypeConfig & { name: string })[];
};
type Packet = {
  id: string; revision: number; status: string; testRun: boolean; workflow: Snapshot;
  parentPacketId: string | null; runNumber: number;
  pages: { id: string; pageNumber: number; predictedType: string; reviewReasons: string[] }[];
  documents: { id: string; type: string; pageIds: string[]; businessReference?: string; reviewReasons: string[] }[];
};
type Catalog = { entries: Entry[]; defaultWorkflow: Ref; stats: (Ref & { packets: number })[] };

const unique = () => `wf-test-${randomUUID().replaceAll('-', '')}`;
const ref = ({ id, version }: Ref): Ref => ({ id, version });
const split = (mode: string, options: Partial<Split> = {}): Split => ({ mode, pageCount: 1, pattern: '', ...options });
const path = (entry: Entry) => `/api/workflows/catalog/${entry.kind}/${entry.id}/${entry.version}`;

async function json<T>(response: APIResponse): Promise<T> {
  expect(response.ok(), `${response.status()} ${await response.text()}`).toBeTruthy();
  return response.json();
}

async function error(response: APIResponse, statuses: number[]) {
  expect(statuses, await response.text()).toContain(response.status());
  expect((await response.json()).error).toEqual(expect.any(String));
}

const catalog = (request: APIRequestContext) => request.get('/api/workflows').then(json<Catalog>);
const create = (request: APIRequestContext, entry: Partial<Entry>) => request.post('/api/workflows/catalog', {
  headers, data: { id: unique(), description: 'Synthetic acceptance fixture only.', ...entry },
}).then(json<Entry>);
const save = (request: APIRequestContext, entry: Entry) => request.put(path(entry), { headers, data: entry }).then(json<Entry>);
const action = (request: APIRequestContext, entry: Entry, operation: string, extra = {}) => request.post(`${path(entry)}/${operation}`, {
  headers, data: { revision: entry.revision, ...extra },
}).then(json<Entry>);
const setDefault = (request: APIRequestContext, workflow: Ref) => request.put('/api/workflows/default', {
  headers, data: ref(workflow),
}).then(json<Ref>);

async function makeType(request: APIRequestContext, policy = split('inherit'), publish = true) {
  // Neither the display name nor a built-in type ID gives the classifier the answer.
  const keyword = `ZXMARK${randomUUID().replaceAll('-', '').toUpperCase()}`;
  let entry = await create(request, {
    kind: 'type', name: 'Synthetic fixture category',
    documentType: { keywords: [keyword], instructions: 'Synthetic fixture instructions.', split: policy },
  });
  if (publish) entry = await action(request, entry, 'publish');
  return { entry, keyword };
}

async function makeRelease(request: APIRequestContext, types: Entry[], publish = true) {
  const unknown = (await catalog(request)).entries.find(e => e.kind === 'type' && e.id === 'unknown' && e.status === 'published');
  expect(unknown, 'Catalog must contain a published unknown fallback').toBeTruthy();
  let entry = await create(request, {
    kind: 'release', name: 'Synthetic fixture release', release: { types: [...types.map(ref), ref(unknown!)] },
  });
  if (publish) entry = await action(request, entry, 'publish');
  return entry;
}

async function makeWorkflow(request: APIRequestContext, release: Entry, policy = split('consecutive'), publish = true,
  overrides: Partial<WorkflowConfig> = {}) {
  let entry = await create(request, {
    kind: 'workflow', name: 'Synthetic fixture workflow', workflow: {
      release: ref(release), mode: 'simulation', providerProfile: 'default', model: 'local-simulation-1',
      instructions: 'Synthetic workflow instructions.', defaultSplit: policy,
      identifierKinds: ['load', 'shipment', 'order', 'bol', 'receipt'], requiredIdentifierKinds: [], ...overrides,
    },
  });
  if (publish) entry = await action(request, entry, 'publish');
  return entry;
}

async function fixture(request: APIRequestContext, policy = split('inherit')) {
  const type = await makeType(request, policy);
  const release = await makeRelease(request, [type.entry]);
  return { ...type, release };
}

function upload(request: APIRequestContext, pages: string[], workflow?: Entry, extra: Record<string, string> = {}, key?: string) {
  return request.post('/api/packets', {
    headers: { ...headers, ...(key ? { 'Idempotency-Key': key } : {}) },
    multipart: {
      files: { name: 'synthetic-workflows.pdf', mimeType: 'application/pdf', buffer: textPdf(pages) },
      ...(workflow ? { workflowId: workflow.id, workflowVersion: String(workflow.version) } : {}), ...extra,
    },
  });
}

async function settled(request: APIRequestContext, accepted: Packet): Promise<Packet> {
  let packet = accepted;
  await expect.poll(async () => {
    packet = await json<Packet>(await request.get(`/api/packets/${accepted.id}`));
    return packet.status;
  }, { message: `Process synthetic packet ${accepted.id}`, timeout: 30_000 }).toBe('review');
  return packet;
}

async function processPdf(request: APIRequestContext, pages: string[], workflow: Entry, extra: Record<string, string> = {}) {
  return settled(request, await json<Packet>(await upload(request, pages, workflow, extra)));
}

function assertGroups(packet: Packet, sizes: number[], typeId?: string) {
  expect(packet.documents.map(d => d.pageIds.length)).toEqual(sizes);
  expect(packet.documents.flatMap(d => d.pageIds)).toEqual(packet.pages.map(p => p.id));
  if (typeId) {
    expect(packet.pages.map(p => p.predictedType)).toEqual(packet.pages.map(() => typeId));
    expect(packet.documents.map(d => d.type)).toEqual(sizes.map(() => typeId));
  }
}

// Read the actual central directory: ZIP entries may use data descriptors, so local
// headers alone cannot reliably locate the compressed manifest produced by .NET.
function manifestFromZip(bytes: Buffer) {
  let end = bytes.length - 22;
  while (end >= Math.max(0, bytes.length - 65_557) && bytes.readUInt32LE(end) !== 0x06054b50) end--;
  expect(end, 'ZIP end-of-central-directory').toBeGreaterThanOrEqual(0);
  let cursor = bytes.readUInt32LE(end + 16);
  for (let n = 0; n < bytes.readUInt16LE(end + 10); n++) {
    expect(bytes.readUInt32LE(cursor)).toBe(0x02014b50);
    const method = bytes.readUInt16LE(cursor + 10), size = bytes.readUInt32LE(cursor + 20);
    const nameSize = bytes.readUInt16LE(cursor + 28), extraSize = bytes.readUInt16LE(cursor + 30), commentSize = bytes.readUInt16LE(cursor + 32);
    const name = bytes.subarray(cursor + 46, cursor + 46 + nameSize).toString();
    if (name === 'manifest.json') {
      const local = bytes.readUInt32LE(cursor + 42);
      const start = local + 30 + bytes.readUInt16LE(local + 26) + bytes.readUInt16LE(local + 28);
      const compressed = bytes.subarray(start, start + size);
      expect([0, 8]).toContain(method);
      return JSON.parse((method === 8 ? inflateRawSync(compressed) : compressed).toString());
    }
    cursor += 46 + nameSize + extraSize + commentSize;
  }
  throw new Error('Export did not contain manifest.json');
}

test.describe('versioned workflow API acceptance', () => {
  // Default changes are global; the project config also uses one worker.
  test.describe.configure({ mode: 'serial' });

  test('catalog drafts support CRUD, stale-revision protection, publication, versions, cloning and archival', async ({ request }) => {
    const { entry: draft } = await makeType(request, split('inherit'), false);
    expect(draft.status).toBe('draft');
    expect(draft.version).toBe(1);
    const edited = await save(request, { ...draft, name: 'Edited synthetic fixture category', description: 'Revised synthetic description.' });
    expect(edited.revision).toBeGreaterThan(draft.revision);
    await error(await request.put(path(draft), { headers, data: { ...draft, name: 'Stale edit' } }), [409]);
    await error(await request.post(`${path(draft)}/publish`, { headers, data: { revision: draft.revision } }), [409]);
    const published = await action(request, edited, 'publish');
    expect(published.status).toBe('published');
    await error(await request.put(path(published), { headers, data: { ...published, name: 'Published mutation' } }), [400, 409]);

    const next = await action(request, published, 'versions');
    expect(next).toMatchObject({ id: published.id, version: published.version + 1, status: 'draft', documentType: published.documentType });
    const clone = await action(request, published, 'clone', { name: 'Cloned synthetic fixture category' });
    expect(clone.id).not.toBe(published.id);
    expect(clone).toMatchObject({ version: 1, status: 'draft', name: 'Cloned synthetic fixture category', documentType: published.documentType });
    await error(await request.post(`${path(edited)}/archive`, { headers, data: { revision: draft.revision } }), [409]);
    const archived = await action(request, published, 'archive');
    expect(archived.status).toBe('archived');
    const entries = (await catalog(request)).entries;
    expect(entries.find(e => e.kind === 'type' && e.id === published.id && e.version === published.version)).toEqual(archived);
    expect(entries.find(e => e.id === next.id && e.version === next.version)).toEqual(next);
    expect(entries.find(e => e.id === clone.id && e.version === clone.version)).toEqual(clone);
  });

  test('invalid references, missing unknown fallback and unpublished dependencies cannot publish', async ({ request }) => {
    const { entry: draftType } = await makeType(request, split('inherit'), false);
    const draftRelease = await makeRelease(request, [draftType], false);
    await error(await request.post(`${path(draftRelease)}/publish`, { headers, data: { revision: draftRelease.revision } }), [400, 409]);
    const draftWorkflow = await makeWorkflow(request, draftRelease, split('consecutive'), false);
    await error(await request.post(`${path(draftWorkflow)}/publish`, { headers, data: { revision: draftWorkflow.revision } }), [400, 409]);
    await error(await request.put('/api/workflows/default', { headers, data: ref(draftWorkflow) }), [400, 409]);
    await error(await request.put(path(draftRelease), {
      headers, data: { ...draftRelease, release: { types: [...draftRelease.release!.types, { id: unique(), version: 999 }] } },
    }), [400, 404]);
    await error(await request.put(path(draftRelease), {
      headers, data: { ...draftRelease, release: { types: [ref(draftType)] } },
    }), [400]);
    await error(await request.put(path(draftWorkflow), {
      headers, data: { ...draftWorkflow, workflow: { ...draftWorkflow.workflow, release: { id: unique(), version: 999 } } },
    }), [400, 404]);
    await error(await request.put(path(draftWorkflow), {
      headers, data: { ...draftWorkflow, workflow: { ...draftWorkflow.workflow, defaultSplit: split('inherit') } },
    }), [400]);
  });

  test('two workflows share one exact release and apply different inherited split defaults', async ({ request }) => {
    const f = await fixture(request);
    const consecutive = await makeWorkflow(request, f.release, split('consecutive'));
    const separate = await makeWorkflow(request, f.release, split('separate'));
    const pages = [1, 2, 3].map(n => `SYNTHETIC TEST ONLY\n${f.keyword}\nSequence ${n}`);
    const grouped = await processPdf(request, pages, consecutive);
    const individual = await processPdf(request, pages, separate);
    assertGroups(grouped, [3], f.entry.id);
    assertGroups(individual, [1, 1, 1], f.entry.id);
    expect(grouped.workflow.releaseId).toBe(individual.workflow.releaseId);
    expect(grouped.workflow.releaseVersion).toBe(individual.workflow.releaseVersion);
    expect(grouped.workflow.types).toEqual(individual.workflow.types);
    expect(grouped.workflow.hash).not.toBe(individual.workflow.hash);
    expect(grouped.testRun).toBe(false);
    const unknown = await processPdf(request, [f.keyword, 'SYNTHETIC UNMATCHED MATERIAL 82941', f.keyword], consecutive);
    assertGroups(unknown, [1, 1, 1]);
    expect(unknown.pages[1].predictedType).toBe('unknown');
    expect(unknown.documents[1].type).toBe('unknown');
  });

  test('per-type identifier policy overrides workflow separate default and flags missing evidence', async ({ request }) => {
    const f = await fixture(request, split('identifier', { pattern: 'CASE: ([A-Z0-9-]+)' }));
    const workflow = await makeWorkflow(request, f.release, split('separate'));
    const packet = await processPdf(request, ['A-001', 'A-001', 'B-002', 'B-002'].map(value => `${f.keyword}\nCASE: ${value}`), workflow);
    assertGroups(packet, [2, 2], f.entry.id);
    expect(packet.workflow.types.find(t => t.id === f.entry.id)?.split.mode).toBe('identifier');
    expect(packet.workflow.defaultSplit.mode).toBe('separate');
    const missing = await processPdf(request, [`${f.keyword}\nNo case evidence on this page`], workflow);
    assertGroups(missing, [1], f.entry.id);
    expect([...missing.pages[0].reviewReasons, ...missing.documents[0].reviewReasons].join(' ')).toMatch(/identifier|missing|evidence/i);
    const conflicting = await processPdf(request, [`${f.keyword}\nCASE: A-001\nCASE: B-002`], workflow);
    assertGroups(conflicting, [1], f.entry.id);
    expect(conflicting.documents[0].reviewReasons.join(' ')).toMatch(/ambiguous|conflict/i);
  });

  test('consecutive grouping preserves hard boundaries between uploaded PDF sources', async ({ request }) => {
    const f = await fixture(request);
    const workflow = await makeWorkflow(request, f.release, split('consecutive'));
    const data = new FormData();
    for (const name of ['synthetic-source-one.pdf', 'synthetic-source-two.pdf']) {
      data.append('files', new Blob([new Uint8Array(textPdf([f.keyword, f.keyword]))], { type: 'application/pdf' }), name);
    }
    data.append('workflowId', workflow.id);
    data.append('workflowVersion', String(workflow.version));
    const packet = await settled(request, await json<Packet>(await request.post('/api/packets', { headers, multipart: data })));
    assertGroups(packet, [2, 2], f.entry.id);
    expect(packet.pages.map(p => p.pageNumber)).toEqual([1, 2, 1, 2]);
  });

  for (const scenario of [
    { mode: 'first', pattern: 'BEGIN RECORD', markers: ['BEGIN RECORD', 'CONTINUATION', 'BEGIN RECORD', 'CONTINUATION'], sizes: [2, 2] },
    { mode: 'last', pattern: 'END RECORD', markers: ['CONTINUATION', 'END RECORD', 'CONTINUATION', 'END RECORD'], sizes: [2, 2] },
    { mode: 'fixed', pattern: '', markers: ['SEGMENT A', 'SEGMENT B', 'SEGMENT C', 'SEGMENT D', 'SEGMENT E'], sizes: [2, 2, 1] },
  ]) {
    test(`per-type ${scenario.mode} policy produces the expected real-PDF boundaries`, async ({ request }) => {
      const f = await fixture(request, split(scenario.mode, { pattern: scenario.pattern, pageCount: scenario.mode === 'fixed' ? 2 : 1 }));
      const workflow = await makeWorkflow(request, f.release, split('separate'));
      // Place first/last markers at the text edges, independent of multiline regex flags.
      const pages = scenario.markers.map(marker => scenario.mode === 'first' ? `${marker}\n${f.keyword}` : `${f.keyword}\n${marker}`);
      const packet = await processPdf(request, pages, workflow);
      assertGroups(packet, scenario.sizes, f.entry.id);
    });
  }

  test('required identifiers add actionable review reasons without dropping classified pages', async ({ request }) => {
    const f = await fixture(request);
    const workflow = await makeWorkflow(request, f.release, split('consecutive'), true, {
      identifierKinds: ['shipment'], requiredIdentifierKinds: ['shipment'],
    });
    const packet = await processPdf(request, [`SYNTHETIC TEST ONLY\n${f.keyword}\nNo business reference supplied`], workflow);
    assertGroups(packet, [1], f.entry.id);
    const reasons = packet.documents.flatMap(d => d.reviewReasons).join(' ');
    expect(reasons).toMatch(/shipment/i);
    expect(reasons).toMatch(/required|missing/i);
    expect(packet.status).toBe('review');
    await error(await request.get(`/api/packets/${packet.id}/export`), [409]);
  });

  test('draft preview freezes saved configuration and supports correction/export with test provenance but rejects delivery', async ({ request }) => {
    const primary = await makeType(request, split('fixed', { pageCount: 2 }), false);
    const correction = await makeType(request, split('inherit'), false);
    const release = await makeRelease(request, [primary.entry, correction.entry], false);
    let workflow = await makeWorkflow(request, release, split('separate'), false);
    const stale = workflow;
    workflow = await save(request, { ...workflow, workflow: { ...workflow.workflow!, instructions: 'Saved draft preview instructions.' } });
    const pages = [primary.keyword, primary.keyword, primary.keyword];
    await error(await upload(request, pages, workflow), [400, 409]);
    await error(await upload(request, pages, workflow, { testRun: 'true' }), [400, 409]);
    await error(await upload(request, pages, stale, { testRun: 'true', workflowRevision: String(stale.revision) }), [409]);
    let packet = await processPdf(request, pages, workflow, { testRun: 'true', workflowRevision: String(workflow.revision) });
    assertGroups(packet, [2, 1], primary.entry.id);
    expect(packet.testRun).toBe(true);
    expect(packet.workflow).toMatchObject({ id: workflow.id, version: workflow.version, instructions: 'Saved draft preview instructions.' });
    expect(packet.workflow.types.find(t => t.id === primary.entry.id)).toMatchObject({ version: primary.entry.version, keywords: [primary.keyword], split: split('fixed', { pageCount: 2 }) });
    const snapshot = structuredClone(packet.workflow);
    await save(request, { ...primary.entry, documentType: { ...primary.entry.documentType!, split: split('separate') } });
    await save(request, { ...workflow, workflow: { ...workflow.workflow!, instructions: 'Later draft instructions.' } });
    expect((await json<Packet>(await request.get(`/api/packets/${packet.id}`))).workflow).toEqual(snapshot);

    await error(await request.get(`/api/packets/${packet.id}/export`), [409]);
    const documents = packet.documents.map((d, index) => index === 0 ? { ...d, type: correction.entry.id, businessReference: 'SYNTHETIC-CORRECTION-001' } : d);
    packet = await json<Packet>(await request.put(`/api/packets/${packet.id}/review`, {
      headers, data: { revision: packet.revision, documents, approve: false },
    }));
    expect(packet.documents[0]).toMatchObject({ type: correction.entry.id, businessReference: 'SYNTHETIC-CORRECTION-001' });
    packet = await json<Packet>(await request.put(`/api/packets/${packet.id}/review`, {
      headers, data: { revision: packet.revision, documents: packet.documents, approve: true },
    }));
    expect(packet.status).toBe('approved');
    expect(packet.workflow).toEqual(snapshot);
    const exported = await request.get(`/api/packets/${packet.id}/export`);
    expect(exported.ok(), await exported.text()).toBeTruthy();
    expect(exported.headers()['content-type']).toContain('zip');
    const manifest = manifestFromZip(await exported.body());
    expect(manifest.testRun).toBe(true);
    expect(manifest.workflow).toEqual(snapshot);
    expect(manifest.documents[0]).toMatchObject({ type: correction.entry.id, businessReference: 'SYNTHETIC-CORRECTION-001' });
    const pdf = await request.get(`/api/packets/${packet.id}/documents/${packet.documents[0].id}/download`);
    expect(pdf.ok()).toBeTruthy();
    expect((await pdf.body()).subarray(0, 5).toString()).toBe('%PDF-');
    await error(await request.post(`/api/packets/${packet.id}/deliveries`, {
      headers, data: { revision: packet.revision, scenario: 'success' },
    }), [400, 409]);
    expect(await json(await request.get(`/api/packets/${packet.id}/deliveries`))).toEqual([]);
    const stats = (await catalog(request)).stats.find(s => s.id === workflow.id && s.version === workflow.version);
    expect(stats?.packets ?? 0).toBe(0);
  });

  test('published changes preserve the original snapshot and ordinary reprocess; explicit selection creates a different run', async ({ request }) => {
    const f = await fixture(request);
    const workflow = await makeWorkflow(request, f.release, split('consecutive'));
    const original = await processPdf(request, [f.keyword, f.keyword, f.keyword], workflow);
    const snapshot = structuredClone(original.workflow);
    let next = await action(request, workflow, 'versions');
    next = await save(request, { ...next, workflow: { ...next.workflow!, defaultSplit: split('separate'), instructions: 'New published instructions.' } });
    next = await action(request, next, 'publish');
    const newPacket = await processPdf(request, [f.keyword, f.keyword, f.keyword], next);
    assertGroups(newPacket, [1, 1, 1], f.entry.id);
    expect(newPacket.workflow.hash).not.toBe(snapshot.hash);
    expect((await json<Packet>(await request.get(`/api/packets/${original.id}`))).workflow).toEqual(snapshot);

    // A reviewed packet is not retryable; rejecting it must also preserve the snapshot.
    // Successful failed-packet retry requires a backend failure fixture/unit test.
    await error(await request.post(`/api/packets/${original.id}/retry`, { headers }), [409]);
    const inherited = await settled(request, await json<Packet>(await request.post(`/api/packets/${original.id}/reprocess`, {
      headers, data: { revision: original.revision },
    })));
    expect(inherited.id).not.toBe(original.id);
    expect(inherited.parentPacketId).toBe(original.id);
    expect(inherited.runNumber).toBe(original.runNumber + 1);
    expect(inherited.workflow).toEqual(snapshot);
    assertGroups(inherited, [3], f.entry.id);
    const selected = await settled(request, await json<Packet>(await request.post(`/api/packets/${original.id}/reprocess`, {
      headers, data: { revision: original.revision, workflowId: next.id, workflowVersion: next.version },
    })));
    expect(selected.parentPacketId).toBe(original.id);
    expect(selected.workflow).toEqual(newPacket.workflow);
    assertGroups(selected, [1, 1, 1], f.entry.id);
    const unchanged = await json<Packet>(await request.get(`/api/packets/${original.id}`));
    expect(unchanged.workflow).toEqual(snapshot);
    expect(unchanged.documents).toEqual(original.documents);
    expect(unchanged.revision).toBe(original.revision);
  });

  test('lost-response idempotency replay retains its original workflow after default change and archival', async ({ page, request }) => {
    const originalDefault = (await catalog(request)).defaultWorkflow;
    const f = await fixture(request);
    const first = await makeWorkflow(request, f.release, split('consecutive'));
    const replacement = await makeWorkflow(request, f.release, split('separate'));
    const pages = [f.keyword, f.keyword];
    const key = unique();
    let accepted: Packet | undefined;
    try {
      expect(await setDefault(request, first)).toEqual(ref(first));
      await error(await request.post(`${path(first)}/archive`, { headers, data: { revision: first.revision } }), [400, 409]);
      await page.goto('/');
      await page.route('**/api/packets', async route => {
        if (route.request().method() !== 'POST') return route.continue();
        // The server commits acceptance, but the submitting client never sees the response.
        accepted = await json<Packet>(await route.fetch());
        await route.abort('failed');
      });
      const submission = await page.evaluate(async ({ bytes, key }) => {
        const data = new FormData();
        data.append('files', new Blob([new Uint8Array(bytes)], { type: 'application/pdf' }), 'synthetic-workflows.pdf');
        try {
          await fetch('/api/packets', { method: 'POST', headers: { 'X-paprika': '1', 'Idempotency-Key': key }, body: data });
          return 'unexpected-response';
        } catch { return 'response-lost'; }
      }, { bytes: [...textPdf(pages)], key });
      expect(submission).toBe('response-lost');
      expect(accepted).toBeTruthy();
      expect(accepted!.workflow).toMatchObject(ref(first));
      const original = await settled(request, accepted!);

      expect(await setDefault(request, replacement)).toEqual(ref(replacement));
      expect((await action(request, first, 'archive')).status).toBe('archived');
      const replay = await json<Packet>(await upload(request, pages, undefined, {}, key));
      expect(replay.id).toBe(original.id);
      expect(replay.workflow).toEqual(original.workflow);
      expect(replay.documents).toEqual(original.documents);
      await error(await upload(request, pages, replacement, {}, key), [409]);
      await error(await upload(request, [...pages, 'Changed content'], undefined, {}, key), [409]);
      const fresh = await settled(request, await json<Packet>(await upload(request, pages, undefined, {}, unique())));
      expect(fresh.id).not.toBe(original.id);
      expect(fresh.workflow).toMatchObject(ref(replacement));
      assertGroups(fresh, [1, 1], f.entry.id);
      assertGroups(original, [2], f.entry.id);
      const entries = (await catalog(request)).entries;
      expect(entries.find(e => e.kind === 'workflow' && e.id === first.id && e.version === first.version)?.status).toBe('archived');
    } finally {
      await setDefault(request, originalDefault);
      expect((await catalog(request)).defaultWorkflow).toEqual(originalDefault);
    }
  });
});
