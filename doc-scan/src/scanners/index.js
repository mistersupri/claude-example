const escl = require('./escl');

const registry = new Map();

async function discoverAll() {
  const errors = [];
  const tasks = [
    escl
      .discover()
      .then(async (found) => {
        for (const s of found) {
          s.hasAdf = await escl.getCapabilities(s.url).then((c) => c.hasAdf).catch(() => true);
        }
        return found;
      })
      .catch((err) => {
        errors.push(`Pencarian jaringan gagal: ${err.message}`);
        return [];
      }),
  ];
  if (process.platform === 'win32') {
    const wia = require('./wia');
    tasks.push(
      wia
        .list()
        .then((list) => list.map((s) => ({ ...s, hasAdf: true })))
        .catch((err) => {
          errors.push(`Pencarian pemindai USB (WIA) gagal: ${err.message}`);
          return [];
        }),
    );
  }
  const scanners = (await Promise.all(tasks)).flat();
  for (const s of scanners) registry.set(s.id, s);
  return { scanners: scanners.map(publicInfo), errors };
}

function publicInfo(s) {
  return { id: s.id, backend: s.backend, name: s.name, hasAdf: s.hasAdf !== false };
}

async function addManual(address) {
  const s = await escl.probe(address);
  s.hasAdf = await escl.getCapabilities(s.url).then((c) => c.hasAdf).catch(() => true);
  registry.set(s.id, s);
  return publicInfo(s);
}

async function scan(scannerId, options) {
  const s = registry.get(scannerId);
  if (!s) throw new Error('Pemindai tidak dikenal. Klik "Cari pemindai" lagi.');
  if (s.backend === 'wia') return require('./wia').scan(s, options);
  return escl.scan(s, options);
}

module.exports = { discoverAll, addManual, scan };
