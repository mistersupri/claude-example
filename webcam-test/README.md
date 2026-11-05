# Webcam Test

Aplikasi Windows kecil untuk menyalakan webcam laptop, melihat pratinjaunya, dan menyimpan foto.

Status tampilan: **draft tanpa arahan desain** (belum ada `DESIGN.md`). UI memakai kontrol bawaan Windows Forms apa adanya.

## Cara mencoba

1. Buka halaman **Releases** di repo ini, pilih rilis `webcam-test-v1.0.x` terbaru.
2. Unduh `WebcamTest.zip` dan **ekstrak semua isinya** ke satu folder (jangan jalankan exe langsung dari dalam zip).
3. Klik dua kali `WebcamTest.exe`. Butuh Windows 10 versi 1903 atau lebih baru; .NET Framework 4.8 sudah termasuk di Windows.
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

Butuh Windows dan Visual Studio 2022 (atau Build Tools) dengan MSBuild. Paket XamlHost memakai tugas build yang tidak didukung `dotnet build`, jadi pakai `msbuild`.

```powershell
cd webcam-test
msbuild WebcamTest.csproj /restore /p:Configuration=Release
```

Hasilnya ada di `bin\Release\net48\`; jalankan `WebcamTest.exe` dari folder itu. Perintah yang sama dipakai GitHub Actions.

## Rilis otomatis

Workflow `.github/workflows/webcam-test-release.yml` berjalan setiap ada push yang mengubah folder `webcam-test/` (atau lewat tombol **Run workflow** di tab Actions). Workflow ini membangun aplikasi di runner Windows dan membuat GitHub Release baru berisi `WebcamTest.zip`, dengan tag `webcam-test-v1.0.<nomor run>`.

## Teknologi

- .NET Framework 4.8 Windows Forms. Dipilih karena itu cara yang didukung untuk menempelkan kontrol pratinjau kamera milik Windows (UWP XAML `CaptureElement`) ke jendela WinForms lewat XAML Islands (`Microsoft.Toolkit.Forms.UI.XamlHost`). Proyeksi Windows SDK untuk .NET 5 ke atas tidak menyertakan `Windows.UI.Xaml`.
- Windows `MediaCapture` untuk membaca kamera (butuh Windows 10 versi 1903 atau lebih baru).
