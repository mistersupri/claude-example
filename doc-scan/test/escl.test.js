const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const escl = require('../src/scanners/escl');
const { fakeJpeg } = require('./helpers');

const CAPS = `<?xml version="1.0"?>
<scan:ScannerCapabilities xmlns:pwg="http://www.pwg.org/schemas/2010/12/sm" xmlns:scan="http://schemas.hp.com/imaging/escl/2011/05/03">
<pwg:MakeAndModel>Brother MFC-TEST</pwg:MakeAndModel>
<scan:Platen><scan:PlatenInputCaps><scan:MaxWidth>2550</scan:MaxWidth><scan:MaxHeight>3507</scan:MaxHeight></scan:PlatenInputCaps></scan:Platen>
<scan:Adf><scan:AdfSimplexInputCaps><scan:MaxWidth>2480</scan:MaxWidth><scan:MaxHeight>3500</scan:MaxHeight></scan:AdfSimplexInputCaps></scan:Adf>
</scan:ScannerCapabilities>`;

function mockScanner({ adfPages = 3, busyFirst = false } = {}) {
  const state = { posted: [], served: 0, busy: busyFirst };
  const server = http.createServer((req, res) => {
    if (req.url === '/eSCL/ScannerCapabilities') {
      res.writeHead(200, { 'Content-Type': 'text/xml' });
      return res.end(CAPS);
    }
    if (req.method === 'POST' && req.url === '/eSCL/ScanJobs') {
      let body = '';
      req.on('data', (c) => (body += c));
      req.on('end', () => {
        state.posted.push(body);
        state.served = 0;
        res.writeHead(201, { Location: `http://${req.headers.host}/eSCL/ScanJobs/42` });
        res.end();
      });
      return;
    }
    if (req.url === '/eSCL/ScanJobs/42/NextDocument') {
      if (state.busy) {
        state.busy = false;
        res.writeHead(503);
        return res.end();
      }
      const limit = state.posted.at(-1).includes('Feeder') ? adfPages : 1;
      if (state.served >= limit) {
        res.writeHead(404);
        return res.end();
      }
      state.served++;
      res.writeHead(200, { 'Content-Type': 'image/jpeg' });
      return res.end(fakeJpeg(100, 100));
    }
    res.writeHead(404);
    res.end();
  });
  return new Promise((resolve) =>
    server.listen(0, '127.0.0.1', () => resolve({ server, state, url: `http://127.0.0.1:${server.address().port}/eSCL` })),
  );
}

test('parseCapabilities membaca model, ADF, dan ukuran maksimum', () => {
  const caps = escl.parseCapabilities(CAPS);
  assert.equal(caps.model, 'Brother MFC-TEST');
  assert.equal(caps.hasAdf, true);
  assert.deepEqual(caps.platenMax, { width: 2550, height: 3507 });
  assert.deepEqual(caps.adfMax, { width: 2480, height: 3500 });
});

test('probe menemukan pemindai lewat alamat manual', async () => {
  const { server, url } = await mockScanner();
  try {
    const s = await escl.probe(url);
    assert.equal(s.name, 'Brother MFC-TEST');
    assert.equal(s.backend, 'escl');
  } finally {
    server.close();
  }
});

test('probe memberi pesan jelas saat alamat tidak menjawab', async () => {
  await assert.rejects(escl.probe('http://127.0.0.1:1/eSCL'), /Tidak bisa terhubung/);
});

test('scan flatbed mengambil satu halaman dan memakai ukuran maksimum platen', async () => {
  const { server, state, url } = await mockScanner();
  try {
    const pages = await escl.scan({ url }, { source: 'flatbed', dpi: 300, color: 'color' });
    assert.equal(pages.length, 1);
    assert.match(state.posted[0], /<pwg:InputSource>Platen</);
    assert.match(state.posted[0], /<pwg:Width>2550</);
    assert.match(state.posted[0], /<scan:XResolution>300</);
  } finally {
    server.close();
  }
});

test('scan ADF mengambil semua halaman sampai 404 dan menunggu saat 503', async () => {
  const { server, state, url } = await mockScanner({ adfPages: 3, busyFirst: true });
  try {
    const pages = await escl.scan({ url }, { source: 'adf', dpi: 200, color: 'gray' });
    assert.equal(pages.length, 3);
    assert.match(state.posted[0], /Feeder/);
    assert.match(state.posted[0], /Grayscale8/);
  } finally {
    server.close();
  }
});

test('scan ADF tanpa kertas memberi pesan pengumpan kosong', async () => {
  const { server, url } = await mockScanner({ adfPages: 0 });
  try {
    await assert.rejects(escl.scan({ url }, { source: 'adf', dpi: 300, color: 'color' }), /Pengumpan dokumen kosong/);
  } finally {
    server.close();
  }
});
