/** Small real PDF containing synthetic text only; no fonts or services to download. */
export function textPdf(pages: string[]): Buffer {
  const objects = ['<< /Type /Catalog /Pages 2 0 R >>', '', '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>'];
  const kids: string[] = [];
  for (const text of pages) {
    const page = objects.length + 1, stream = page + 1;
    kids.push(`${page} 0 R`);
    objects.push(`<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents ${stream} 0 R >>`);
    const content = 'BT /F1 14 Tf 50 740 Td ' + text.split('\n').map(line =>
      '(' + line.replace(/\\/g, '\\\\').replace(/\(/g, '\\(').replace(/\)/g, '\\)') + ') Tj'
    ).join(' 0 -24 Td ') + ' ET';
    objects.push(`<< /Length ${Buffer.byteLength(content, 'ascii')} >>\nstream\n${content}\nendstream`);
  }
  objects[1] = `<< /Type /Pages /Count ${pages.length} /Kids [${kids.join(' ')}] >>`;
  let output = '%PDF-1.4\n';
  const offsets = [0];
  objects.forEach((object, index) => {
    offsets.push(Buffer.byteLength(output, 'ascii'));
    output += `${index + 1} 0 obj\n${object}\nendobj\n`;
  });
  const xref = Buffer.byteLength(output, 'ascii');
  output += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n`;
  output += offsets.slice(1).map(n => `${String(n).padStart(10, '0')} 00000 n \n`).join('');
  output += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  return Buffer.from(output, 'ascii');
}
