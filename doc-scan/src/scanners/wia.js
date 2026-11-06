const { execFile } = require('node:child_process');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');

const SCRIPT = `
param([string]$Mode,[string]$DeviceId,[string]$Source,[int]$Dpi,[string]$Color,[string]$OutDir)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
try {
  $dm = New-Object -ComObject WIA.DeviceManager
  if ($Mode -eq 'list') {
    $out = @()
    foreach ($info in $dm.DeviceInfos) {
      if ($info.Type -eq 1) { $out += @{ id = [string]$info.DeviceID; name = [string]$info.Properties.Item('Name').Value } }
    }
    ConvertTo-Json -InputObject @($out) -Compress
    exit 0
  }

  $dev = $null
  foreach ($info in $dm.DeviceInfos) { if ($info.DeviceID -eq $DeviceId) { $dev = $info.Connect() } }
  if ($null -eq $dev) { throw 'Pemindai tidak ditemukan. Pastikan terhubung dan menyala.' }

  function Get-WiaProp($props, $id) {
    foreach ($p in $props) { if ($p.PropertyID -eq $id) { return $p.Value } }
    return $null
  }
  function Set-WiaProp($props, $id, $value) {
    foreach ($p in $props) { if ($p.PropertyID -eq $id) { try { $p.Value = $value } catch {} ; return } }
  }

  if ($Source -eq 'adf') {
    Set-WiaProp $dev.Properties 3088 1
    Set-WiaProp $dev.Properties 3096 0
  } else {
    Set-WiaProp $dev.Properties 3088 2
  }

  $item = $dev.Items.Item(1)
  $intent = 1
  if ($Color -eq 'gray') { $intent = 2 }
  Set-WiaProp $item.Properties 6146 $intent
  Set-WiaProp $item.Properties 6147 $Dpi
  Set-WiaProp $item.Properties 6148 $Dpi
  Set-WiaProp $item.Properties 6149 0
  Set-WiaProp $item.Properties 6150 0
  $bedW = Get-WiaProp $dev.Properties 3074
  $bedH = Get-WiaProp $dev.Properties 3075
  if ($Source -ne 'adf' -and $bedW -and $bedH) {
    Set-WiaProp $item.Properties 6151 ([int]($bedW / 1000.0 * $Dpi))
    Set-WiaProp $item.Properties 6152 ([int]($bedH / 1000.0 * $Dpi))
  }

  Add-Type -AssemblyName System.Drawing
  $jpegFormat = '{B96B3CAE-0728-11D3-9D7B-0000F81EF32E}'
  $count = 0
  while ($true) {
    try {
      $img = $item.Transfer($jpegFormat)
    } catch {
      $paperEmpty = ($_.Exception.HResult -eq -2145320957) -or ($_.Exception.Message -match '80210003')
      if ($paperEmpty -and $count -gt 0) { break }
      if ($paperEmpty) { throw 'Pengumpan dokumen kosong. Letakkan kertas lalu coba lagi.' }
      throw
    }
    $count++
    $ext = [string]$img.FileExtension
    $raw = Join-Path $OutDir ('page{0:D3}.{1}' -f $count, $ext.TrimStart('.'))
    $img.SaveFile($raw)
    if ($ext -match 'jpe?g') {
      Write-Output $raw
    } else {
      $final = Join-Path $OutDir ('page{0:D3}.jpg' -f $count)
      $bmp = [System.Drawing.Image]::FromFile($raw)
      $bmp.Save($final, [System.Drawing.Imaging.ImageFormat]::Jpeg)
      $bmp.Dispose()
      Remove-Item $raw
      Write-Output $final
    }
    if ($Source -ne 'adf') { break }
  }
  exit 0
} catch {
  [Console]::Error.WriteLine($_.Exception.Message)
  exit 1
}
`;

let scriptPath;
async function ensureScript() {
  if (!scriptPath) {
    const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'docscan-wia-'));
    scriptPath = path.join(dir, 'wia.ps1');
    await fs.writeFile(scriptPath, `﻿${SCRIPT}`, 'utf8');
  }
  return scriptPath;
}

async function run(args, timeout) {
  const file = await ensureScript();
  return new Promise((resolve, reject) => {
    execFile(
      'powershell.exe',
      ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', file, ...args],
      { timeout, windowsHide: true, maxBuffer: 1024 * 1024 },
      (err, stdout, stderr) => {
        if (err) reject(new Error((stderr || err.message).trim()));
        else resolve(stdout);
      },
    );
  });
}

async function list() {
  const out = await run(['-Mode', 'list'], 20000);
  const parsed = JSON.parse(out.trim() || '[]');
  return parsed.map((d) => ({ id: `wia:${d.id}`, backend: 'wia', name: d.name, deviceId: d.id }));
}

async function scan(scanner, { source, dpi, color }) {
  const outDir = await fs.mkdtemp(path.join(os.tmpdir(), 'docscan-pages-'));
  try {
    const out = await run(
      ['-Mode', 'scan', '-DeviceId', scanner.deviceId, '-Source', source, '-Dpi', String(dpi), '-Color', color, '-OutDir', outDir],
      10 * 60 * 1000,
    );
    const files = out.split(/\r?\n/).map((l) => l.trim()).filter(Boolean);
    if (files.length === 0) throw new Error('Pemindai tidak menghasilkan halaman.');
    return await Promise.all(files.map((f) => fs.readFile(f)));
  } finally {
    await fs.rm(outDir, { recursive: true, force: true });
  }
}

module.exports = { list, scan };
