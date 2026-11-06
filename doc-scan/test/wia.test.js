const test = require('node:test');
const assert = require('node:assert/strict');
const { execFirst, powershellCandidates } = require('../src/scanners/wia');

test('execFirst melewati program yang tidak ada dan memakai kandidat berikutnya', async () => {
  const out = await execFirst(['/tidak/ada/powershell.exe', process.execPath], ['-p', '1+1'], {});
  assert.equal(out.trim(), '2');
});

test('execFirst memberi pesan jelas bila tidak ada kandidat yang ditemukan', async () => {
  await assert.rejects(execFirst(['/tidak/ada/a', '/tidak/ada/b'], [], {}), /PowerShell tidak ditemukan/);
});

test('execFirst meneruskan pesan error dari program yang berjalan tetapi gagal', async () => {
  await assert.rejects(execFirst([process.execPath], ['-e', 'console.error("boom"); process.exit(1)'], {}), /boom/);
});

test('powershellCandidates mengutamakan alamat lengkap di System32', () => {
  const c = powershellCandidates();
  assert.match(c[0], /System32.WindowsPowerShell.v1\.0.powershell\.exe$/);
  assert.deepEqual(c.slice(1), ['powershell.exe', 'pwsh.exe']);
});
