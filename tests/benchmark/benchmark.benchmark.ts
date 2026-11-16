import { test, expect } from '@playwright/test';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { tmpdir } from 'node:os';
import { textPdf } from '../fixtures';
import { cases } from '../benchmark-cases';

const headers = { 'X-paprika': '1' };
const root = resolve(__dirname, '../..');
const python = process.env['PAPRIKA_BENCHMARK_PYTHON'] ?? 'python';

function runPython(args: string[]) {
  const result = spawnSync(python, args, { encoding: 'utf8' });
  expect(result.error, 'Python must be available for the shared evaluator').toBeUndefined();
  expect(result.status, result.stderr || result.stdout).toBe(0);
  return JSON.parse(result.stdout);
}

test('frozen synthetic predictions, independent truth, and corrected exports', async ({ request }) => {
  expect(process.env['PAPRIKA_BENCHMARK_ISOLATED'], 'Run through scripts/benchmark.ps1').toBe('1');
  const config = await (await request.get('/api/config')).json();
  expect(config.mode).toBe('simulation');
  expect(config.ocrConfigured).toBe(false);
  expect(config.classifierConfigured).toBe(false);
  const scratch = mkdtempSync(join(process.env['LOCALAPPDATA'] ? join(process.env['LOCALAPPDATA'], 'Temp/opencode') : tmpdir(), 'paprika-benchmark-evaluation-'));
  const results: any[] = [];
  try {
    for (const fixture of cases) {
      await test.step(fixture.name, async () => {
        const form = new FormData();
        fixture.files.forEach((pages, i) => form.append('files', new Blob([new Uint8Array(textPdf(pages))], { type: 'application/pdf' }), `${fixture.name}-${i}.pdf`));
        const upload = await request.post('/api/packets', { headers, multipart: form });
        expect(upload.ok(), await upload.text()).toBe(true);
        const created = await upload.json();
        let packet: any;
        await expect.poll(async () => {
          packet = await (await request.get(`/api/packets/${created.id}`)).json();
          return packet.status;
        }).toBe('review');
        const frozen = structuredClone(packet);
        expect(packet.mode).toBe('simulation');
        expect(packet.processing.model).toBe('local-simulation-1');
        expect(packet.pages).toHaveLength(fixture.files.reduce((n, pages) => n + pages.length, 0));
        expect((await request.get(`/api/packets/${packet.id}/export`)).status()).toBe(409);
        const sourceIds = fixture.files.map((_, i) => {
          const sourcePage = packet.pages.find((p: any) => p.sourceName === `${fixture.name}-${i}.pdf`);
          expect(sourcePage).toBeTruthy();
          return sourcePage.sourceId;
        });
        const truth = {
          packetId: packet.id,
          documents: fixture.truth.map(d => ({ type: d.type, pages: d.pages.map(([source, pageNumber]) => ({ sourceId: sourceIds[source], pageNumber })) })),
        };
        writeFileSync(join(scratch, 'prediction.json'), JSON.stringify(frozen));
        writeFileSync(join(scratch, 'truth.json'), JSON.stringify(truth));
        // The production evaluator validates unique complete membership and computes all metrics.
        const metrics = runPython([join(root, 'scripts/evaluate.py'), join(scratch, 'prediction.json'), join(scratch, 'truth.json')]);
        if (fixture.conflictingReferences) {
          for (const value of fixture.conflictingReferences) {
            expect(packet.identifierCandidates.some((c: any) => c.value === value && c.pageIds.length > 0)).toBe(true);
          }
          expect(packet.documents.every((d: any) => !d.businessReference)).toBe(true);
        }
        const documents = truth.documents.map((d, i) => ({
          id: `reviewed-${i}`, type: d.type, businessReference: '', reviewReasons: [],
          pageIds: d.pages.map(p => packet.pages.find((page: any) => page.sourceId === p.sourceId && page.pageNumber === p.pageNumber).id),
        }));
        const review = await request.put(`/api/packets/${packet.id}/review`, {
          headers, data: { revision: packet.revision, documents, approve: true },
        });
        expect(review.ok(), await review.text()).toBe(true);
        const approved = await (await request.get(`/api/packets/${packet.id}`)).json();
        expect(approved.status).toBe('approved');
        expect(approved.pages).toEqual(frozen.pages);
        expect(approved.identifierCandidates).toEqual(frozen.identifierCandidates);
        const history = await (await request.get(`/api/packets/${packet.id}/history`)).json();
        expect(history[0].documents).toEqual(frozen.documents);
        const exported = await request.get(`/api/packets/${packet.id}/export`);
        expect(exported.ok()).toBe(true);
        writeFileSync(join(scratch, 'export.zip'), await exported.body());
        // Python's standard ZIP reader avoids another Node dependency.
        const archive = runPython(['-c',
          'import json,sys,zipfile; z=zipfile.ZipFile(sys.argv[1]); m=json.loads(z.read("manifest.json")); print(json.dumps({"manifest":m,"pdfsValid":all(z.read(d["file"]).startswith(b"%PDF-") for d in m["documents"]),"files":z.namelist()}))',
          join(scratch, 'export.zip')]);
        expect(archive.pdfsValid).toBe(true);
        expect(archive.files).toHaveLength(truth.documents.length + 1);
        expect(archive.manifest.revision).toBe(approved.revision);
        expect(archive.manifest.documents.map((d: any) => ({ type: d.type, pages: d.pages.map((p: any) => ({ sourceId: p.sourceId, pageNumber: p.pageNumber })) }))).toEqual(truth.documents);
        results.push({
          case: fixture.name, expectedManualIntervention: fixture.expectedManualIntervention,
          prediction: metrics,
          correction: { approvedDocuments: documents.length, preservedPages: approved.pages.length, manifestMatchesTruth: true, originalPredictionsUnchanged: true },
        });
      });
    }
    const countFields = ['pages', 'correctlyTypedPages', 'expectedDocuments', 'proposedDocuments', 'exactlyReconstructedDocuments', 'fullyCorrectDocuments', 'extraDocumentStarts', 'missedDocumentStarts'];
    const aggregate: Record<string, number> = Object.fromEntries(countFields.map(key => [key, results.reduce((n, item) => n + item.prediction[key], 0)]));
    aggregate.pageTypeAccuracy = aggregate.correctlyTypedPages / aggregate.pages;
    aggregate.exactReconstructionRecall = aggregate.exactlyReconstructedDocuments / aggregate.expectedDocuments;
    aggregate.fullyCorrectDocumentRecall = aggregate.fullyCorrectDocuments / aggregate.expectedDocuments;
    const report = {
      mode: 'synthetic-simulation',
      notice: 'Fixed synthetic text PDFs and offline heuristic rules only. These results do not measure real document accuracy, OCR quality, or Azure/Jev provider accuracy.',
      fixtures: cases.length,
      aggregate,
      invariants: { everyPagePreserved: true, noAutomaticApproval: true, providersConfigured: false, correctedManifestsMatchTruth: true, originalPredictionsUnchanged: true },
      cases: results,
    };
    const output = process.env['PAPRIKA_BENCHMARK_REPORT'];
    expect(output, 'Harness must supply a new report path').toBeTruthy();
    writeFileSync(output!, JSON.stringify(report, null, 2) + '\n', { flag: 'wx' });
    console.log(JSON.stringify({ mode: report.mode, fixtures: report.fixtures, ...aggregate }, null, 2));
    console.log(`Report: ${output}`);
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});
