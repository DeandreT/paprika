import { expect } from '@playwright/test';
import { inflateRawSync } from 'node:zlib';

// .NET can use data descriptors, so locate the entry in the central directory.
export function jsonFromZip(bytes: Buffer, filename: string) {
  const earliestEnd = Math.max(0, bytes.length - 65_557);
  let end = bytes.length - 22;
  while (end >= earliestEnd && bytes.readUInt32LE(end) !== 0x06054b50) end--;
  expect(end, 'ZIP end-of-central-directory').toBeGreaterThanOrEqual(earliestEnd);
  let cursor = bytes.readUInt32LE(end + 16);
  for (let n = 0; n < bytes.readUInt16LE(end + 10); n++) {
    expect(bytes.readUInt32LE(cursor)).toBe(0x02014b50);
    const method = bytes.readUInt16LE(cursor + 10);
    const size = bytes.readUInt32LE(cursor + 20);
    const nameSize = bytes.readUInt16LE(cursor + 28);
    const extraSize = bytes.readUInt16LE(cursor + 30);
    const commentSize = bytes.readUInt16LE(cursor + 32);
    const name = bytes.subarray(cursor + 46, cursor + 46 + nameSize).toString();
    if (name === filename) {
      const local = bytes.readUInt32LE(cursor + 42);
      const start = local + 30 + bytes.readUInt16LE(local + 26) + bytes.readUInt16LE(local + 28);
      const compressed = bytes.subarray(start, start + size);
      expect([0, 8]).toContain(method);
      return JSON.parse((method === 8 ? inflateRawSync(compressed) : compressed).toString());
    }
    cursor += 46 + nameSize + extraSize + commentSize;
  }
  throw new Error(`Export did not contain ${filename}`);
}
