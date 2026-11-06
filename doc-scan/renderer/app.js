const $ = (id) => document.getElementById(id);
const STATUS_TEXT = { pending: 'Menunggu', scanning: 'Memindai...', done: 'Selesai', failed: 'Gagal', skipped: 'Dilewati' };
const SETTINGS_KEY = 'docscan.settings';

const state = {
  scanners: [],
  queue: [],
  busy: false,
  rows: [],
  outDir: '',
};

function message(el, text, isError = false) {
  el.textContent = text;
  el.classList.toggle('error', isError);
}

function loadSettings() {
  try {
    return JSON.parse(localStorage.getItem(SETTINGS_KEY) ?? '{}');
  } catch {
    return {};
  }
}
function saveSettings() {
  try {
    localStorage.setItem(
      SETTINGS_KEY,
      JSON.stringify({
        source: $('source').value,
        dpi: $('dpi').value,
        color: $('color').value,
        format: $('format').value,
        outDir: state.outDir,
      }),
    );
  } catch {
    /* penyimpanan tidak tersedia, pengaturan tidak diingat */
  }
}

function setOutDir(dir) {
  state.outDir = dir;
  $('outdir').textContent = dir;
  saveSettings();
}

/* Pemindai */

function renderScanners() {
  const select = $('scanner');
  select.replaceChildren();
  for (const s of state.scanners) {
    const opt = document.createElement('option');
    opt.value = s.id;
    opt.textContent = `${s.name} (${s.backend === 'wia' ? 'USB/WIA' : 'jaringan'})`;
    select.append(opt);
  }
  select.disabled = state.scanners.length === 0;
  syncSourceOptions();
  updateControls();
}

function currentScanner() {
  return state.scanners.find((s) => s.id === $('scanner').value);
}

function syncSourceOptions() {
  const adf = $('source').querySelector('option[value="adf"]');
  const s = currentScanner();
  adf.disabled = s ? !s.hasAdf : false;
  if (adf.disabled && $('source').value === 'adf') $('source').value = 'flatbed';
}

async function discover() {
  $('discover').disabled = true;
  message($('scanner-state'), 'Mencari pemindai di jaringan dan USB...');
  try {
    const { scanners, errors } = await window.api.discover();
    state.scanners = scanners;
    renderScanners();
    if (scanners.length > 0) {
      message($('scanner-state'), `${scanners.length} pemindai ditemukan.${errors.length ? ' ' + errors.join(' ') : ''}`, errors.length > 0);
    } else {
      message(
        $('scanner-state'),
        errors.length
          ? errors.join(' ')
          : 'Tidak ada pemindai ditemukan. Pastikan pemindai menyala dan satu jaringan dengan komputer, atau masukkan alamat IP.',
        true,
      );
    }
  } catch (err) {
    message($('scanner-state'), `Gagal mencari pemindai: ${err.message}`, true);
  } finally {
    $('discover').disabled = false;
  }
}

async function addManual() {
  const address = $('manual').value.trim();
  if (!address) {
    message($('scanner-state'), 'Isi alamat IP pemindai dulu.', true);
    return;
  }
  $('manual-add').disabled = true;
  message($('scanner-state'), `Menghubungi ${address}...`);
  try {
    const scanner = await window.api.addManual(address);
    state.scanners = [...state.scanners.filter((s) => s.id !== scanner.id), scanner];
    renderScanners();
    $('scanner').value = scanner.id;
    syncSourceOptions();
    message($('scanner-state'), `Terhubung ke ${scanner.name}.`);
  } catch (err) {
    message($('scanner-state'), err.message, true);
  } finally {
    $('manual-add').disabled = false;
  }
}

/* Daftar berkas */

function mode() {
  return document.querySelector('input[name="mode"]:checked').value;
}

function renderMode() {
  const table = mode() === 'table';
  $('panel-list').hidden = table;
  $('panel-table').hidden = !table;
}

async function parseTableText() {
  const text = $('table-text').value;
  state.rows = text.trim() ? await window.api.parseTable(text) : [];
  renderColumns();
}

function headerNames() {
  const cols = Math.max(0, ...state.rows.map((r) => r.length));
  const first = state.rows[0] ?? [];
  return Array.from({ length: cols }, (_, i) =>
    $('has-header').checked && first[i]?.trim() ? first[i].trim() : `Kolom ${i + 1}`,
  );
}

function dataRows() {
  return $('has-header').checked ? state.rows.slice(1) : state.rows;
}

function renderColumns() {
  const names = headerNames();
  const prevFolder = $('folder-col').value;
  const prevName = $('name-col').value;
  const fill = (select, withNone) => {
    select.replaceChildren();
    if (withNone) {
      const none = document.createElement('option');
      none.value = '-1';
      none.textContent = 'Tanpa folder (simpan langsung)';
      select.append(none);
    }
    names.forEach((n, i) => {
      const opt = document.createElement('option');
      opt.value = String(i);
      opt.textContent = n;
      select.append(opt);
    });
  };
  fill($('folder-col'), true);
  fill($('name-col'), false);
  if (prevFolder && $('folder-col').querySelector(`option[value="${prevFolder}"]`)) $('folder-col').value = prevFolder;
  if (prevName && $('name-col').querySelector(`option[value="${prevName}"]`)) $('name-col').value = prevName;
  else if (names.length > 1 && !prevName) $('name-col').value = '1';
  renderPreview(names);
}

function renderPreview(names) {
  const wrap = $('preview-wrap');
  const table = $('preview');
  table.replaceChildren();
  if (state.rows.length === 0) {
    wrap.hidden = true;
    return;
  }
  const head = table.createTHead().insertRow();
  names.forEach((n, i) => {
    const th = document.createElement('th');
    th.scope = 'col';
    th.textContent = n;
    if (String(i) === $('folder-col').value) th.textContent += ' (folder)';
    if (String(i) === $('name-col').value) th.textContent += ' (nama)';
    head.append(th);
  });
  const body = table.createTBody();
  dataRows()
    .slice(0, 5)
    .forEach((cells) => {
      const tr = body.insertRow();
      names.forEach((_, i) => {
        tr.insertCell().textContent = cells[i] ?? '';
      });
    });
  wrap.hidden = false;
}

async function openTable() {
  try {
    const file = await window.api.openTable();
    if (!file) return;
    $('table-text').value = file.text;
    $('table-file').textContent = file.name;
    await parseTableText();
  } catch (err) {
    message($('build-state'), `Gagal membuka file: ${err.message}`, true);
  }
}

async function buildQueue() {
  if (state.busy) return;
  const req =
    mode() === 'table'
      ? {
          mode: 'table',
          rows: dataRows(),
          folderCol: Number($('folder-col').value),
          nameCol: Number($('name-col').value),
        }
      : { mode: 'list', text: $('names').value };
  try {
    const { jobs, skipped } = await window.api.buildJobs(req);
    state.queue = jobs.map((job, i) => ({ ...job, id: i, status: 'pending', note: '' }));
    renderQueue();
    if (jobs.length === 0) {
      message($('build-state'), skipped[0]?.reason ?? 'Tidak ada nama berkas yang bisa dipakai.', true);
    } else {
      const extra = skipped.length ? ` ${skipped.length} baris dilewati karena nama kosong.` : '';
      message($('build-state'), `${jobs.length} berkas masuk antrean.${extra}`);
    }
  } catch (err) {
    message($('build-state'), `Gagal membuat antrean: ${err.message}`, true);
  }
}

/* Antrean */

function ext() {
  return $('format').value === 'pdf' ? '.pdf' : '.jpg';
}

function nextPending() {
  return state.queue.find((j) => j.status === 'pending');
}

function updateControls() {
  const pending = nextPending();
  $('scan-next').disabled = state.busy || !pending || !currentScanner();
  $('scan-next').textContent = pending ? `Pindai berikutnya: ${pending.name}` : 'Pindai berikutnya';
  $('build').disabled = state.busy;
  $('discover').disabled = state.busy;
  const done = state.queue.filter((j) => j.status === 'done').length;
  const failed = state.queue.filter((j) => j.status === 'failed').length;
  $('summary').textContent = state.queue.length
    ? `${done} dari ${state.queue.length} selesai${failed ? `, ${failed} gagal` : ''}`
    : 'Antrean kosong';
  if (state.queue.length === 0) {
    message($('queue-state'), 'Antrean kosong. Isi daftar berkas, lalu klik "Buat antrean".');
  } else if (!currentScanner() && pending) {
    message($('queue-state'), 'Pilih pemindai dulu (langkah 1) sebelum memindai.', true);
  } else if (!pending && !state.busy) {
    message($('queue-state'), failed ? 'Semua berkas diproses. Ada yang gagal, klik Ulangi pada berkas tersebut.' : 'Semua berkas selesai dipindai.');
  } else if (!state.busy) {
    message($('queue-state'), 'Letakkan dokumen di pemindai, lalu klik "Pindai berikutnya".');
  }
}

function renderQueue() {
  const list = $('queue');
  list.replaceChildren();
  const current = nextPending();
  for (const job of state.queue) {
    const li = document.createElement('li');
    li.dataset.status = job.status;
    if (job === current || job.status === 'scanning') li.classList.add('current');

    const num = document.createElement('span');
    num.className = 'num';
    num.textContent = String(job.id + 1);

    const label = document.createElement('span');
    label.className = 'label';
    label.textContent = job.label + ext();
    if (job.note) {
      const small = document.createElement('small');
      if (job.status === 'failed') small.className = 'error';
      small.textContent = job.note;
      label.append(small);
    }

    const right = document.createElement('span');
    right.className = 'right';
    const status = document.createElement('span');
    status.className = 'status';
    status.textContent = STATUS_TEXT[job.status];
    right.append(status);
    if (job.status === 'failed' && !state.busy) {
      right.append(rowButton('Ulangi', () => setStatus(job, 'pending')));
    }
    if (job.status === 'pending' && !state.busy) {
      right.append(rowButton('Lewati', () => setStatus(job, 'skipped')));
    }
    li.append(num, label, right);
    list.append(li);
  }
  updateControls();
}

function rowButton(text, onClick) {
  const b = document.createElement('button');
  b.type = 'button';
  b.textContent = text;
  b.addEventListener('click', onClick);
  return b;
}

function setStatus(job, status, note = '') {
  job.status = status;
  job.note = note;
  renderQueue();
}

async function scanNext() {
  if (state.busy) return;
  const scanner = currentScanner();
  let job = nextPending();
  if (!scanner || !job) return;
  state.busy = true;
  try {
    while (job) {
      setStatus(job, 'scanning');
      message($('queue-state'), `Memindai "${job.label}"...`);
      try {
        const res = await window.api.scan({
          scannerId: scanner.id,
          folderParts: job.folderParts,
          name: job.name,
          outDir: state.outDir,
          source: $('source').value,
          dpi: Number($('dpi').value),
          color: $('color').value,
          format: $('format').value,
        });
        setStatus(job, 'done', `${res.pageCount} halaman`);
      } catch (err) {
        setStatus(job, 'failed', cleanError(err));
        break;
      }
      job = $('auto').checked ? nextPending() : null;
    }
  } finally {
    state.busy = false;
    renderQueue();
  }
}

function cleanError(err) {
  return String(err.message ?? err).replace(/^Error invoking remote method '[^']+': (Error: )?/, '');
}

/* Awal */

async function init() {
  const saved = loadSettings();
  for (const key of ['source', 'dpi', 'color', 'format']) {
    if (saved[key] && $(key).querySelector(`option[value="${saved[key]}"]`)) $(key).value = saved[key];
  }
  const defaults = await window.api.defaults();
  setOutDir(saved.outDir || defaults.outDir);
  renderMode();
  renderQueue();

  $('discover').addEventListener('click', discover);
  $('manual-add').addEventListener('click', addManual);
  $('manual').addEventListener('keydown', (e) => {
    if (e.key === 'Enter') addManual();
  });
  $('scanner').addEventListener('change', () => {
    syncSourceOptions();
    updateControls();
  });
  document.querySelectorAll('input[name="mode"]').forEach((r) => r.addEventListener('change', renderMode));
  $('table-text').addEventListener('input', () => {
    $('table-file').textContent = 'atau tempel dari Excel di bawah';
    parseTableText();
  });
  $('has-header').addEventListener('change', renderColumns);
  $('folder-col').addEventListener('change', () => renderPreview(headerNames()));
  $('name-col').addEventListener('change', () => renderPreview(headerNames()));
  $('open-table').addEventListener('click', openTable);
  $('choose-dir').addEventListener('click', async () => {
    const dir = await window.api.chooseFolder();
    if (dir) setOutDir(dir);
  });
  $('open-dir').addEventListener('click', () => window.api.openFolder(state.outDir));
  $('build').addEventListener('click', buildQueue);
  $('scan-next').addEventListener('click', scanNext);
  for (const key of ['source', 'dpi', 'color', 'format']) {
    $(key).addEventListener('change', () => {
      saveSettings();
      renderQueue();
    });
  }
}

init();
