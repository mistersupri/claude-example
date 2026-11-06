const http = require('node:http');
const https = require('node:https');

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function request(url, { method = 'GET', body, headers = {}, timeout = 30000 } = {}) {
  return new Promise((resolve, reject) => {
    const u = new URL(url);
    const mod = u.protocol === 'https:' ? https : http;
    // Pemindai di jaringan lokal memakai sertifikat self-signed, jadi hanya URL pemindai yang dilonggarkan.
    const req = mod.request(u, { method, headers, timeout, rejectUnauthorized: false }, (res) => {
      const chunks = [];
      res.on('data', (c) => chunks.push(c));
      res.on('end', () => resolve({ status: res.statusCode, headers: res.headers, body: Buffer.concat(chunks) }));
    });
    req.on('timeout', () => req.destroy(new Error('Pemindai tidak merespons (timeout)')));
    req.on('error', reject);
    if (body) req.write(body);
    req.end();
  });
}

const tagRe = (tag) => new RegExp(`<(?:\\w+:)?${tag}[\\s>]`);

function sectionMax(xml, section) {
  const m = xml.match(new RegExp(`<(?:\\w+:)?${section}[\\s\\S]*?</(?:\\w+:)?${section}>`));
  if (!m) return null;
  const w = m[0].match(/<(?:\w+:)?MaxWidth>(\d+)</);
  const h = m[0].match(/<(?:\w+:)?MaxHeight>(\d+)</);
  return w && h ? { width: Number(w[1]), height: Number(h[1]) } : null;
}

function parseCapabilities(xml) {
  const model = xml.match(/<(?:\w+:)?(?:MakeAndModel|ModelName)>([^<]+)</);
  return {
    model: model ? model[1].trim() : '',
    hasAdf: tagRe('Adf').test(xml),
    hasPlaten: tagRe('Platen').test(xml),
    platenMax: sectionMax(xml, 'PlatenInputCaps'),
    adfMax: sectionMax(xml, 'AdfSimplexInputCaps'),
  };
}

async function getCapabilities(baseUrl) {
  const res = await request(`${baseUrl}/ScannerCapabilities`, { timeout: 8000 });
  if (res.status !== 200) throw new Error(`Pemindai menjawab HTTP ${res.status} pada ScannerCapabilities`);
  return parseCapabilities(res.body.toString('utf8'));
}

function buildSettings({ source, dpi, color, max }) {
  const size = max ?? { width: 2481, height: 3507 };
  const mode = color === 'gray' ? 'Grayscale8' : 'RGB24';
  const input = source === 'adf' ? 'Feeder' : 'Platen';
  return `<?xml version="1.0" encoding="UTF-8"?>
<scan:ScanSettings xmlns:pwg="http://www.pwg.org/schemas/2010/12/sm" xmlns:scan="http://schemas.hp.com/imaging/escl/2011/05/03">
<pwg:Version>2.0</pwg:Version>
<scan:Intent>Document</scan:Intent>
<pwg:ScanRegions><pwg:ScanRegion>
<pwg:ContentRegionUnits>escl:ThreeHundredthsOfInches</pwg:ContentRegionUnits>
<pwg:Width>${size.width}</pwg:Width><pwg:Height>${size.height}</pwg:Height>
<pwg:XOffset>0</pwg:XOffset><pwg:YOffset>0</pwg:YOffset>
</pwg:ScanRegion></pwg:ScanRegions>
<pwg:InputSource>${input}</pwg:InputSource>
<scan:ColorMode>${mode}</scan:ColorMode>
<scan:XResolution>${dpi}</scan:XResolution><scan:YResolution>${dpi}</scan:YResolution>
<pwg:DocumentFormat>image/jpeg</pwg:DocumentFormat>
</scan:ScanSettings>`;
}

async function scan(scanner, { source, dpi, color }) {
  const base = scanner.url;
  const caps = await getCapabilities(base).catch(() => null);
  const max = source === 'adf' ? caps?.adfMax : caps?.platenMax;
  const post = await request(`${base}/ScanJobs`, {
    method: 'POST',
    headers: { 'Content-Type': 'text/xml' },
    body: buildSettings({ source, dpi, color, max }),
  });
  if (post.status === 503) throw new Error('Pemindai sedang sibuk. Tunggu sebentar lalu coba lagi.');
  if (post.status !== 201) throw new Error(`Pemindai menolak pengaturan pindai (HTTP ${post.status}). Coba resolusi atau sumber lain.`);
  const location = post.headers.location;
  if (!location) throw new Error('Pemindai tidak mengirim alamat pekerjaan pindai');
  const jobUrl = new URL(location, `${base}/`).href.replace(/\/$/, '');

  const pages = [];
  let deadline = Date.now() + 120000;
  for (;;) {
    const res = await request(`${jobUrl}/NextDocument`, { timeout: 120000 });
    if (res.status === 200) {
      pages.push(res.body);
      deadline = Date.now() + 120000;
      if (source !== 'adf') break;
    } else if (res.status === 503) {
      if (Date.now() > deadline) throw new Error('Pemindai terlalu lama menyiapkan halaman');
      await sleep(1000);
    } else if (res.status === 404 || res.status === 409) {
      if (pages.length === 0) {
        throw new Error(
          source === 'adf'
            ? 'Pengumpan dokumen kosong atau pemindai sedang dipakai. Letakkan kertas lalu coba lagi.'
            : 'Pemindai tidak menghasilkan halaman.',
        );
      }
      break;
    } else {
      throw new Error(`Pemindai menjawab HTTP ${res.status} saat mengambil halaman`);
    }
  }
  return pages;
}

function discover(timeoutMs = 4000) {
  const { Bonjour } = require('bonjour-service');
  return new Promise((resolve) => {
    const bonjour = new Bonjour();
    const found = new Map();
    const handler = (secure) => (svc) => {
      const addr = (svc.addresses ?? []).find((a) => /^\d+\.\d+\.\d+\.\d+$/.test(a)) ?? svc.host;
      if (!addr) return;
      if (found.has(addr) && secure) return;
      const rs = String(svc.txt?.rs ?? 'eSCL').replace(/^\/|\/$/g, '');
      found.set(addr, {
        id: `escl:${addr}:${svc.port}`,
        backend: 'escl',
        name: String(svc.txt?.ty || svc.name),
        url: `${secure ? 'https' : 'http'}://${addr}:${svc.port}/${rs}`,
      });
    };
    const plain = bonjour.find({ type: 'uscan' }, handler(false));
    const secure = bonjour.find({ type: 'uscans' }, handler(true));
    setTimeout(() => {
      plain.stop();
      secure.stop();
      bonjour.destroy();
      resolve([...found.values()]);
    }, timeoutMs);
  });
}

async function probe(address) {
  const raw = String(address ?? '').trim();
  if (!raw) throw new Error('Alamat pemindai kosong');
  let candidates;
  if (/^https?:\/\//i.test(raw)) {
    const u = new URL(raw);
    const path = u.pathname.replace(/\/$/, '') || '/eSCL';
    candidates = [`${u.protocol}//${u.host}${path}`];
  } else {
    const host = raw.replace(/\/.*$/, '');
    candidates = [`http://${host}/eSCL`, `https://${host}/eSCL`];
  }
  let lastError;
  for (const url of candidates) {
    try {
      const caps = await getCapabilities(url);
      const host = new URL(url).host;
      return { id: `escl:${host}`, backend: 'escl', name: caps.model || host, url };
    } catch (err) {
      lastError = err;
    }
  }
  throw new Error(`Tidak bisa terhubung ke ${raw}: ${lastError?.message ?? 'tanpa respons'}`);
}

module.exports = { discover, probe, scan, getCapabilities, parseCapabilities, buildSettings };
