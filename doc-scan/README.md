# DocScan

Aplikasi desktop (Windows dan macOS) untuk memindai dokumen secara berurutan dengan pemindai bermerek seperti Brother dan Canon. Nama berkas dan foldernya diambil dari daftar atau tabel yang Anda berikan, jadi Anda tidak perlu mengetik nama tiap kali memindai.

Status antarmuka: draft tanpa arahan visual (belum ada `DESIGN.md`). Tampilan polos mengikuti tema terang atau gelap sistem, dengan satu warna aksen.

## Cara pakai

1. **Pemindai.** Klik "Cari pemindai". Jika tidak muncul, buka "Pemindai tidak muncul?" lalu masukkan alamat IP pemindai.
2. **Daftar berkas.** Pilih salah satu:
   - **Daftar nama**: satu nama per baris. Dipindai dari atas ke bawah.
   - **Tabel**: buka file CSV atau tempel sel dari Excel. Pilih kolom yang jadi folder (opsional) dan kolom yang jadi nama berkas. Kolom folder boleh berisi subfolder dengan pemisah `/`.
3. **Buat antrean**, letakkan dokumen, lalu klik "Pindai berikutnya". Dengan ADF, centang "Lanjut otomatis" agar seluruh antrean berjalan tanpa klik.

Hasil disimpan di folder tujuan (bawaan: `Documents/DocScan`), misalnya `Budi/KTP.pdf`.

Aturan penamaan:
- Karakter yang dilarang sistem berkas (`\ / : * ? " < > |`) diganti `_`.
- Nama ganda diberi nomor: `KTP.pdf`, `KTP (2).pdf`.
- Berkas yang sudah ada di disk tidak ditimpa. Hasil baru diberi nomor.
- Ekstensi `.pdf` atau `.jpg` yang ikut diketik dibuang, ekstensi ditentukan pilihan Format.
- Baris dengan nama kosong dilewati dan jumlahnya dilaporkan.

PDF dari ADF berisi semua halaman yang ditarik pemindai. Format JPG menyimpan satu berkas per halaman (`nama.jpg`, `nama-2.jpg`, dan seterusnya).

## Dukungan pemindai

| Jalur | Windows | macOS | Syarat |
| --- | --- | --- | --- |
| AirScan (eSCL), pemindai jaringan | ya | ya | Pemindai satu jaringan dengan komputer dan mendukung AirScan. Mayoritas Brother dan Canon jaringan keluaran beberapa tahun terakhir mendukungnya. |
| WIA, pemindai USB | ya | tidak | Driver WIA dari pabrikan terpasang. |

Pemindai USB di macOS belum didukung. Jika pemindai Anda hanya USB, hubungkan ke jaringan jika modelnya punya Wi-Fi atau Ethernet.

Yang sudah diuji: logika penamaan, pembuatan PDF (divalidasi dengan `pdfinfo`), klien eSCL terhadap pemindai tiruan, dan antarmuka di Electron. Yang belum diuji dengan perangkat nyata: pemindai fisik, jalur WIA di Windows, dan aplikasi hasil build di macOS. Mohon dicek lewat draft rilis dan laporkan masalahnya.

## Pengembangan

```
npm install
npm start
npm test
npm run dist
```

Rilis dibuat oleh `.github/workflows/doc-scan-release.yml`: tes, build Windows dan macOS, lalu draft rilis GitHub.

Aplikasi belum punya ikon sendiri, jadi memakai ikon bawaan Electron. Letakkan ikon di `build/icon.png` (1024x1024) untuk menggantinya.
