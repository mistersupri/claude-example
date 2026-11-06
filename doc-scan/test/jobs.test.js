const test = require('node:test');
const assert = require('node:assert/strict');
const jobs = require('../src/jobs');

test('sanitizeSegment membersihkan karakter terlarang dan nama khusus Windows', () => {
  assert.equal(jobs.sanitizeSegment('a/b:c*d'), 'a_b_c_d');
  assert.equal(jobs.sanitizeSegment('  ..  '), '');
  assert.equal(jobs.sanitizeSegment('..'), '');
  assert.equal(jobs.sanitizeSegment('CON'), '_CON');
  assert.equal(jobs.sanitizeSegment('nama. '), 'nama');
});

test('folderParts memecah subfolder dan membuang traversal', () => {
  assert.deepEqual(jobs.folderParts('2024/../Bulan 01\\A'), ['2024', 'Bulan 01', 'A']);
});

test('buildFromList menghormati urutan, membuang ekstensi ketikan, dan menomori duplikat', () => {
  const { jobs: out, skipped } = jobs.buildFromList('Faktur A.pdf\n\nFaktur B\nfaktur a\n  \n???');
  assert.deepEqual(out.map((j) => j.label), ['Faktur A', 'Faktur B', 'faktur a (2)', '___']);
  assert.equal(skipped.length, 0);
});

test('parseDelimited mengenali tab, titik koma, koma, dan tanda kutip', () => {
  assert.deepEqual(jobs.parseDelimited('a\tb\n1\t2'), [['a', 'b'], ['1', '2']]);
  assert.deepEqual(jobs.parseDelimited('a;b\r\n1;2\r\n'), [['a', 'b'], ['1', '2']]);
  assert.deepEqual(jobs.parseDelimited('a,b\n"x, y","say ""hi"""\n\n'), [['a', 'b'], ['x, y', 'say "hi"']]);
  assert.deepEqual(jobs.parseDelimited('﻿a,b'), [['a', 'b']]);
});

test('buildFromTable memakai kolom folder dan nama, melewati nama kosong', () => {
  const rows = [
    ['Budi', 'KTP'],
    ['Budi', 'KK'],
    ['Sari', ''],
    ['Budi', 'KTP'],
  ];
  const { jobs: out, skipped } = jobs.buildFromTable(rows, { folderCol: 0, nameCol: 1 });
  assert.deepEqual(out.map((j) => j.label), ['Budi/KTP', 'Budi/KK', 'Budi/KTP (2)']);
  assert.deepEqual(skipped, [{ row: 3, reason: 'Kolom nama kosong' }]);
});

test('buildFromTable tanpa kolom folder menyimpan langsung', () => {
  const { jobs: out } = jobs.buildFromTable([['x']], { folderCol: -1, nameCol: 0 });
  assert.deepEqual(out[0].folderParts, []);
});

test('buildFromTable menolak kolom nama yang belum dipilih', () => {
  const { jobs: out, skipped } = jobs.buildFromTable([['x']], { folderCol: -1, nameCol: -1 });
  assert.equal(out.length, 0);
  assert.equal(skipped.length, 1);
});
