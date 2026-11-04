using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace WebcamTest;

/// <summary>
/// One running camera, read through the Windows MediaCapture API (the same stack as the Camera app).
/// Frames arrive on a thread-pool thread through the callback given to <see cref="StartAsync"/>.
/// </summary>
internal sealed class CameraSession : IDisposable
{
    private const double BlackMeanThreshold = 2.0;

    private readonly MediaCapture capture;
    private readonly MediaFrameReader reader;
    private readonly Action<Bitmap, bool> onFrame;
    private int converting;
    private bool disposed;

    private CameraSession(MediaCapture capture, MediaFrameReader reader, Action<Bitmap, bool> onFrame)
    {
        this.capture = capture;
        this.reader = reader;
        this.onFrame = onFrame;
    }

    /// <param name="onFrame">Receives a new bitmap (the receiver owns it) and whether it is all black.</param>
    public static async Task<CameraSession> StartAsync(MediaFrameSourceGroup group, Action<Bitmap, bool> onFrame)
    {
        var capture = new MediaCapture();
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                SourceGroup = group,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                // CPU memory gives SoftwareBitmap frames without any GPU surface in between.
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                StreamingCaptureMode = StreamingCaptureMode.Video,
            });

            var source = capture.FrameSources.Values
                .FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
                ?? throw new InvalidOperationException("Kamera ini tidak punya aliran gambar berwarna (kemungkinan kamera inframerah).");

            var reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;

            var session = new CameraSession(capture, reader, onFrame);
            reader.FrameArrived += session.OnFrameArrived;

            var status = await reader.StartAsync();
            if (status != MediaFrameReaderStartStatus.Success)
            {
                session.Dispose();
                throw new InvalidOperationException($"Windows menolak memulai kamera ({status}).");
            }
            return session;
        }
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (disposed)
        {
            return;
        }
        reader.FrameArrived -= OnFrameArrived;
        try
        {
            await reader.StopAsync();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
        {
            // The camera was already gone (unplugged or taken by another app); disposing below is enough.
        }
        Dispose();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        reader.FrameArrived -= OnFrameArrived;
        reader.Dispose();
        capture.Dispose();
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        // Drop frames while the previous one is still being converted.
        if (Interlocked.Exchange(ref converting, 1) == 1)
        {
            return;
        }
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            var software = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (software is null)
            {
                return;
            }
            var bitmap = ToBitmap(software, out var black);
            onFrame(bitmap, black);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
        {
            // Frames can fail while the camera is being stopped; the next frame or the stop handles it.
        }
        finally
        {
            Interlocked.Exchange(ref converting, 0);
        }
    }

    private static Bitmap ToBitmap(SoftwareBitmap source, out bool black)
    {
        using var converted = SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var bytes = new byte[width * height * 4];
        converted.CopyToBuffer(bytes.AsBuffer());

        long sum = 0;
        var samples = 0;
        for (var i = 0; i + 2 < bytes.Length; i += 64)
        {
            sum += bytes[i] + bytes[i + 1] + bytes[i + 2];
            samples += 3;
        }
        black = samples == 0 || sum / (double)samples < BlackMeanThreshold;

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        var data = bitmap.LockBits(
            new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(bytes, y * width * 4, data.Scan0 + y * data.Stride, width * 4);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }
}
