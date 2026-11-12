// Labels are authored independently of API predictions. Source indexes are zero-based;
// page numbers are one-based. Keep imperfect cases: they measure known limitations.
export type TruthDocument = { type: string; pages: [number, number][] };
export type BenchmarkCase = {
  name: string;
  files: string[][];
  truth: TruthDocument[];
  expectedManualIntervention: string;
  conflictingReferences?: string[];
};
const doc = (type: string, ...pages: [number, number][]): TruthDocument => ({ type, pages });
export const cases: BenchmarkCase[] = [
  {
    name: 'clear-document-types',
    files: [['BILL OF LADING', 'PROOF OF DELIVERY', 'PACKING LIST', 'INVOICE', 'RECEIPT']],
    truth: [doc('bol', [0, 1]), doc('pod', [0, 2]), doc('packing_list', [0, 3]), doc('invoice', [0, 4]), doc('receipt', [0, 5])],
    expectedManualIntervention: 'Verify all five types and approve.',
  },
  {
    name: 'adjacent-distinct-same-type',
    files: [['BILL OF LADING\nLOAD: SYN-A\nPage 1 of 1', 'BILL OF LADING\nLOAD: SYN-B\nPage 1 of 1']],
    truth: [doc('bol', [0, 1]), doc('bol', [0, 2])],
    expectedManualIntervention: 'Verify separate documents despite identical types.',
  },
  {
    name: 'explicit-continuation',
    files: [['INVOICE\nORDER: SYN-A\nPage 1 of 2', 'INVOICE\nORDER: SYN-A\nPage 2 of 2 - continued']],
    truth: [doc('invoice', [0, 1], [0, 2])],
    expectedManualIntervention: 'Verify continuation and approve.',
  },
  {
    name: 'file-boundary',
    files: [['PACKING LIST\nPage 1 of 1'], ['PACKING LIST\nPage 2 of 2 - continuation']],
    truth: [doc('packing_list', [0, 1]), doc('packing_list', [1, 1])],
    expectedManualIntervention: 'Verify the separate source file is a distinct document.',
  },
  {
    name: 'conflicting-reference-candidates',
    files: [['BILL OF LADING\nLOAD: SYN-A\nPage 1 of 2', 'BILL OF LADING\nLOAD: SYN-B\nPage 2 of 2']],
    truth: [doc('bol', [0, 1], [0, 2])],
    conflictingReferences: ['SYN-A', 'SYN-B'],
    expectedManualIntervention: 'Resolve conflicting load references before business association.',
  },
  {
    name: 'unknown-and-blank',
    files: [['Unlabelled supporting sheet\nUnrecognized contents', '']],
    truth: [doc('unknown', [0, 1]), doc('unknown', [0, 2])],
    expectedManualIntervention: 'Inspect unknown and blank pages; preserve both.',
  },
  {
    name: 'invoice-with-other-type-reference',
    files: [['INVOICE\nAmount due: 42.00\nRelated BILL OF LADING: SYN-A']],
    truth: [doc('invoice', [0, 1])],
    expectedManualIntervention: 'Relabel the invoice: multiple type keywords make the heuristic uncertain.',
  },
  {
    name: 'implicit-continuation',
    files: [['BILL OF LADING\nLOAD: SYN-A\nShipment terms begin', 'BILL OF LADING\nLOAD: SYN-A\nRemaining shipment terms']],
    truth: [doc('bol', [0, 1], [0, 2])],
    expectedManualIntervention: 'Merge the two pages; matching references alone do not establish continuation.',
  },
  {
    name: 'interleaved-same-type-documents',
    files: [['BILL OF LADING\nLOAD: SYN-A\nPage 1 of 2', 'BILL OF LADING\nLOAD: SYN-B\nPage 1 of 2', 'BILL OF LADING\nLOAD: SYN-A\nPage 2 of 2', 'BILL OF LADING\nLOAD: SYN-B\nPage 2 of 2']],
    truth: [doc('bol', [0, 1], [0, 3]), doc('bol', [0, 2], [0, 4])],
    expectedManualIntervention: 'Move noncontiguous pages into their matching documents; assembly is sequential.',
  },
  {
    name: 'out-of-order-document',
    files: [['INVOICE\nORDER: SYN-A\nPage 2 of 2', 'INVOICE\nORDER: SYN-A\nPage 1 of 2']],
    truth: [doc('invoice', [0, 2], [0, 1])],
    expectedManualIntervention: 'Merge and reorder the pages to restore document order.',
  },
];
