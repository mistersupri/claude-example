const test = require('node:test');
const assert = require('node:assert/strict');
const { jpegInfo, jpegsToPdf } = require('../src/pdf');
const { fakeJpeg } = require('./helpers');

test('jpegInfo membaca ukuran dan jumlah komponen', () => {
  assert.deepEqual(jpegInfo(fakeJpeg(2480, 3508, 3)), { width: 2480, height: 3508, components: 3 });
  assert.equal(jpegInfo(fakeJpeg(10, 20, 1)).components, 1);
});

test('jpegInfo menolak data bukan JPEG', () => {
  assert.throws(() => jpegInfo(Buffer.from('hello world')), /bukan JPEG/);
});

test('jpegsToPdf menghasilkan PDF dengan halaman sesuai ukuran kertas', () => {
  const pdf = jpegsToPdf([fakeJpeg(2480, 3508), fakeJpeg(2480, 3508)], 300);
  const text = pdf.toString('latin1');
  assert.ok(text.startsWith('%PDF-1.4'));
  assert.match(text, /\/Count 2/);
  assert.match(text, /\/MediaBox \[0 0 595\.20 841\.92\]/);
  assert.ok(text.trimEnd().endsWith('%%EOF'));
  const startxref = Number(text.match(/startxref\n(\d+)/)[1]);
  assert.equal(text.slice(startxref, startxref + 4), 'xref');
  const firstEntry = text.slice(startxref).split('\n')[3];
  assert.match(firstEntry, /^0000000009 00000 n/);
  assert.equal(text.slice(9, 14), '1 0 o');
});

test('jpegsToPdf menolak daftar kosong', () => {
  assert.throws(() => jpegsToPdf([], 300), /Tidak ada halaman/);
});
