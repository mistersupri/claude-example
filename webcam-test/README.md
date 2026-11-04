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
| Daftar **Kamera** | Memilih kamera (Kamera 1 biasanya webcam bawaan laptop) | |
| **Nyalakan kamera** / **Matikan kamera** | Menyalakan atau mematikan pratinjau | Alt+N / Alt+M |
| **Ambil foto** | Menyimpan gambar saat ini sebagai JPG | Enter atau Alt+A |
| **Buka folder foto** | Membuka Explorer di folder foto, dengan foto terakhir terpilih | Alt+F |
| **Info perangkat** | Menampilkan daftar kamera Windows (termasuk yang berlabel IR), tipe GPU, dan hasil percobaan terakhir. Bisa disalin ke clipboard | Alt+I |

Foto tersimpan di `%USERPROFILE%\Pictures\WebcamTest\foto-YYYYMMDD-HHMMSS-mmm.jpg`.

Jika kamera tidak bisa dibuka, cek:

- Kamera tidak sedang dipakai aplikasi lain (Zoom, Teams, browser).
- Settings > Privacy & security > Camera: izinkan **Let desktop apps access your camera**.
- Coba pilih nomor kamera lain di daftar.

Jika pratinjau hitam atau kamera tidak muncul:

- Pilihan **Otomatis** mencoba Kamera 1 sampai 4, masing-masing lewat DirectShow, DirectShow MJPG 720p, lalu Media Foundation, dan memakai yang pertama menghasilkan gambar bukan hitam. Status bar menunjukkan percobaan yang sedang berjalan.
- Satu percobaan dibatasi 8 detik. Kamera yang tidak merespons (biasanya kamera IR Windows Hello) dilewati, lalu pencarian lanjut ke nomor kamera berikutnya.
- Hardware transform Media Foundation dimatikan (`OPENCV_VIDEOIO_MSMF_ENABLE_HW_TRANSFORMS=0`).
- Kalau semuanya gagal, layar menampilkan hasil tiap percobaan (hitam, tidak merespons, tidak bisa dibuka). Laptop dengan Windows Hello punya kamera inframerah yang ikut terdaftar dan hanya menghasilkan gambar hitam di aplikasi biasa.

## Build sendiri

Butuh Windows dan .NET 8 SDK.

```powershell
cd webcam-test
dotnet run
```

Build rilis yang sama dengan GitHub Actions:

```powershell
dotnet publish WebcamTest.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

## Rilis otomatis

Workflow `.github/workflows/webcam-test-release.yml` berjalan setiap ada push yang mengubah folder `webcam-test/` (atau lewat tombol **Run workflow** di tab Actions). Workflow ini membangun exe di runner Windows dan membuat GitHub Release baru dengan tag `webcam-test-v1.0.<nomor run>`.

## Teknologi

- .NET 8 Windows Forms: jendela native Windows, tanpa runtime tambahan karena dipublish self-contained.
- [OpenCvSharp](https://github.com/shimat/opencvsharp) dengan backend DirectShow untuk membaca kamera.
