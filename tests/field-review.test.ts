import { expect, test } from 'bun:test';
import { normalizedValue, pageHash, reviewDocuments, selectedSpan, type ExtractionRun } from '../client/src/field-review';
import type { Packet, Page } from '../client/src/models';

test('a textarea selection points to the exact UTF-16 span in the unmodified page', async () => {
  // Textareas collapse CRLF to LF; both the earlier astral character and the
  // newline inside the selected address affect offsets/lengths sent to .NET.
  const text = '📦 Order\r\nShip to:\r\n10 Pine St\r\nUnit 2\r\nUSA';
  const page = { id: 'synthetic-page-1', text } as Page;
  const selection = '10 Pine St\nUnit 2';
  const start = text.replaceAll('\r\n', '\n').indexOf(selection);
  expect(selectedSpan(page, start, start + selection.length)).toEqual({
    pageId: page.id,
    offset: text.indexOf('10 Pine St'),
    length: '10 Pine St\r\nUnit 2'.length,
    text: '10 Pine St\r\nUnit 2',
  });
  expect(selectedSpan(page, start, start)).toBeNull();
  expect(await pageHash(text)).toBe(new Bun.CryptoHasher('sha256').update(text).digest('hex'));
});

test('typed replacements preserve decimal digits and normalize dates and booleans', () => {
  expect(normalizedValue('9,007,199,254,740,993.50', 'number')).toBe('9007199254740993.50');
  expect(normalizedValue(' 03/14/2026 ', 'date')).toBe('2026-03-14');
  expect(normalizedValue('2026-02-29', 'date')).toBeNull();
  expect(normalizedValue(' NO ', 'boolean')).toBe('false');
  expect(normalizedValue(' 00015 ', 'text')).toBe('00015');
});

test('a legacy clear stays uncategorized across approval and does not label a fresh extraction', () => {
  const packet = { documents: [{ id: 'doc', type: 'invoice' }] } as Packet;
  function run(id: string, revision: number, value: string | null, status: string): ExtractionRun {
    return {
      id, packetRevision: revision, packetId: 'packet',
      definition: { id: 'fields', version: 1, fields: [{ name: 'note', kind: 'text', required: false }] },
      results: [{ documentId: 'doc', tables: [], fields: [{ name: 'note', value, status, evidence: [] }] }],
    };
  }
  const predicted = run('initial', 1, 'Internal use', 'extracted');
  const cleared = run('manual', 2, null, 'corrected');
  const carried = run('carry', 3, null, 'corrected');
  const fresh = run('new', 4, 'Internal use', 'extracted');
  const history = {
    currentConfiguredRunId: fresh.id, runs: [predicted, cleared, carried, fresh],
    corrections: [
      { previousRunId: predicted.id, resultId: cleared.id, actor: 'reviewer', at: '',
        correction: { documentId: 'doc', field: 'note', reason: 'Omit from delivery' } },
      { previousRunId: cleared.id, resultId: carried.id, actor: 'reviewer', at: '' },
      // After regrouping, the new run reused only a different document.
      { previousRunId: carried.id, resultId: fresh.id, actor: 'system', at: '', documentIds: ['other'] },
    ],
  };
  const approved = reviewDocuments(packet, carried, history, 'doc')[0].fields[0];
  expect(approved.source?.value).toBe('Internal use');
  expect(approved.final.value).toBeNull();
  expect(approved.changes.map(c => [c.category, c.note])).toEqual([[null, 'Omit from delivery']]);
  expect(reviewDocuments(packet, fresh, history, 'doc')[0].fields[0].changes).toEqual([]);
});
