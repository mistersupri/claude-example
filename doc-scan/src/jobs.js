const RESERVED = /^(con|prn|aux|nul|com[1-9]|lpt[1-9])$/i;
const TYPED_EXT = /\.(pdf|jpe?g)$/i;

function sanitizeSegment(value) {
  let s = String(value ?? '')
    .replace(/[\u0000-\u001f\\/:*?"<>|]/g, '_')
    .replace(/\s+/g, ' ')
    .trim();
  s = s.replace(/^\.+/, '').replace(/[. ]+$/, '');
  if (s === '') return '';
  if (RESERVED.test(s.split('.')[0])) s = '_' + s;
  return s.slice(0, 120);
}

function folderParts(value) {
  return String(value ?? '')
    .split(/[\\/]/)
    .map(sanitizeSegment)
    .filter(Boolean);
}

function cleanName(value) {
  return sanitizeSegment(String(value ?? '').replace(TYPED_EXT, ''));
}

function parseDelimited(text) {
  const src = String(text ?? '').replace(/^﻿/, '');
  const firstLine = src.split(/\r?\n/).find((l) => l.trim() !== '') ?? '';
  const counts = { '\t': 0, ';': 0, ',': 0 };
  for (const ch of firstLine) if (ch in counts) counts[ch]++;
  let delim = ',';
  if (counts['\t'] > 0) delim = '\t';
  else if (counts[';'] > counts[',']) delim = ';';

  const rows = [];
  let row = [];
  let cell = '';
  let quoted = false;
  const endRow = () => {
    row.push(cell);
    cell = '';
    if (row.some((c) => c.trim() !== '')) rows.push(row);
    row = [];
  };
  for (let i = 0; i < src.length; i++) {
    const ch = src[i];
    if (quoted) {
      if (ch === '"' && src[i + 1] === '"') {
        cell += '"';
        i++;
      } else if (ch === '"') quoted = false;
      else cell += ch;
    } else if (ch === '"' && cell === '') quoted = true;
    else if (ch === delim) {
      row.push(cell);
      cell = '';
    } else if (ch === '\n') endRow();
    else if (ch !== '\r') cell += ch;
  }
  endRow();
  return rows;
}

function dedupe(jobs) {
  const seen = new Map();
  return jobs.map((job) => {
    const base = [...job.folderParts, job.name].join('/').toLowerCase();
    const n = (seen.get(base) ?? 0) + 1;
    seen.set(base, n);
    const name = n === 1 ? job.name : `${job.name} (${n})`;
    return { ...job, name, label: [...job.folderParts, name].join('/') };
  });
}

function buildFromList(text) {
  const jobs = [];
  const skipped = [];
  String(text ?? '')
    .split(/\r?\n/)
    .forEach((line, i) => {
      if (line.trim() === '') return;
      const name = cleanName(line);
      if (name) jobs.push({ folderParts: [], name });
      else skipped.push({ row: i + 1, reason: 'Nama kosong setelah dibersihkan' });
    });
  return { jobs: dedupe(jobs), skipped };
}

function buildFromTable(rows, { folderCol, nameCol }) {
  const jobs = [];
  const skipped = [];
  if (!Number.isInteger(nameCol) || nameCol < 0) {
    return { jobs, skipped: [{ row: 0, reason: 'Kolom nama berkas belum dipilih' }] };
  }
  rows.forEach((cells, i) => {
    const name = cleanName(cells[nameCol]);
    if (!name) {
      skipped.push({ row: i + 1, reason: 'Kolom nama kosong' });
      return;
    }
    const folder = Number.isInteger(folderCol) && folderCol >= 0 ? folderParts(cells[folderCol]) : [];
    jobs.push({ folderParts: folder, name });
  });
  return { jobs: dedupe(jobs), skipped };
}

module.exports = { sanitizeSegment, folderParts, cleanName, parseDelimited, buildFromList, buildFromTable };
