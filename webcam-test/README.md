# Webcam Test

Aplikasi Windows kecil untuk menyalakan webcam laptop, melihat pratinjaunya, dan menyimpan foto.

Status tampilan: **draft tanpa arahan desain** (belum ada `DESIGN.md`). UI memakai kontrol bawaan Windows Forms apa adanya.

## Cara mencoba

1. Buka halaman **Releases** di repo ini, pilih rilis `webcam-test-v1.0.x` terbaru.
2. Unduh `WebcamTest.exe` (atau `WebcamTest-win-x64.zip` lalu ekstrak).
3. Klik dua kali `WebcamTest.exe`. Tidak perlu memasang .NET.
   Exe ini tidak ditandatangani, jadi SmartScreen bisa muncul: pilih **More info**, lalu **Run anyway**.

## Pemakaian

| Kontrol | Fungsi | Pintasan |
|---|---|---|
| Daftar **Kamera** | Memilih kamera menurut nama dari Windows. Kamera inframerah (Windows Hello) diberi tanda "(inframerah)" dan ditaruh paling bawah | |
| **Nyalakan kamera** / **Matikan kamera** | Menyalakan atau mematikan pratinjau. Saat kamera sedang dibuka, tombol ini membatalkan | Alt+N / Alt+M |
| **Ambil foto** | Menyimpan gambar saat ini sebagai JPG | Enter atau Alt+A |
| **Buka folder foto** | Membuka Explorer di folder foto, dengan foto terakhir terpilih | Alt+F |
| **Info perangkat** | Menampilkan kamera menurut Windows, perangkat di Device Manager, status layanan kamera, izin kamera Windows, tipe GPU, dan catatan sesi. Bisa disalin ke clipboard | Alt+I |

Foto tersimpan di `%USERPROFILE%\Pictures\WebcamTest\foto-YYYYMMDD-HHMMSS-mmm.jpg`.

## Jika kamera tidak muncul atau gambar hitam

Aplikasi ini memakai API kamera resmi Windows (`MediaCapture`). Cara pertama yang dicoba adalah **pratinjau XAML**: kontrol `CaptureElement` yang sama dengan aplikasi Camera bawaan, dipasang di jendela lewat XAML Islands. Foto diambil dari bingkai pratinjau itu. Jika cara ini gagal, aplikasi mencoba cara lain dan memberi pesan yang spesifik:

- **Windows menolak akses kamera**: Settings > Privacy & security > Camera, nyalakan **Camera access** dan **Let desktop apps access your camera**.
- **Tidak merespons saat dibuka (15 detik)** atau **terbuka tetapi tidak ada gambar**: aplikasi menunggu 8 detik, lalu otomatis mencoba cara lain (aliran pratinjau, aliran rekam, kendali penuh, dan aliran berwarna pertama) sebelum menyerah: restart layanan **Windows Camera Frame Server** di `services.msc`, dan tutup aplikasi lain yang memakai kamera.
- **Gambar masih hitam setelah 6 detik**: cek penutup lensa atau tombol kamera di keyboard, matikan efek kamera (Windows Studio Effects, aplikasi kamera pabrikan) di Settings > Bluetooth & devices > Cameras, atau pilih kamera lain di daftar.
- Tekan **Info perangkat** untuk melihat catatan sesi, lalu salin dan kirim saat melapor masalah.

## Build sendiri

Butuh Windows dan .NET 8 SDK.

```powershell
cd webcam-test
dotnet run
```

Build rilis yang sama dengan GitHub Actions:

```powershell
dotnet publish WebcamTest.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -o publish
```

## Rilis otomatis

Workflow `.github/workflows/webcam-test-release.yml` berjalan setiap ada push yang mengubah folder `webcam-test/` (atau lewat tombol **Run workflow** di tab Actions). Workflow ini membangun exe di runner Windows dan membuat GitHub Release baru dengan tag `webcam-test-v1.0.<nomor run>`.

## Teknologi

- .NET 8 Windows Forms: jendela native Windows, tanpa runtime tambahan karena dipublish self-contained.
- Windows `MediaCapture` dengan `MediaFrameReader` untuk membaca kamera (butuh Windows 10 versi 2004 atau lebih baru).
