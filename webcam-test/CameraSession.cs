using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace WebcamTest;

/// <summary>Ways to read a camera; some drivers only deliver frames in one of them.</summary>
internal enum CaptureMode
{
    /// <summary>Shared access, Windows converts frames to Bgra8.</summary>
    SharedBgra,

    /// <summary>Exclusive control of the camera, Windows converts frames to Bgra8.</summary>
    ExclusiveBgra,

    /// <summary>Shared access, frames in the camera's own format (converted here).</summary>
    SharedNative,
}

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
    private int eventCount;
    private int emptyCount;
    private int convertedCount;
    private volatile string? lastError;

    private CameraSession(MediaCapture capture, MediaFrameReader reader, string formatInfo, Action<Bitmap, bool> onFrame)
    {
        this.capture = capture;
        this.reader = reader;
        FormatInfo = formatInfo;
        this.onFrame = onFrame;
    }

    /// <summary>Format the camera reports, for example "NV12 1280x720".</summary>
    public string FormatInfo { get; }

    /// <summary>Counters that show where frames get lost, for the log.</summary>
    public string Diagnostics =>
        $"event frame={eventCount}, tanpa gambar={emptyCount}, terkonversi={convertedCount}, galat terakhir={lastError ?? "-"}";

    public static string Describe(CaptureMode mode) => mode switch
    {
        CaptureMode.SharedBgra => "bagi pakai, Bgra8",
        CaptureMode.ExclusiveBgra => "kendali penuh, Bgra8",
        _ => "bagi pakai, format asli",
    };

    /// <param name="onFrame">Receives a new bitmap (the receiver owns it) and whether it is all black.</param>
    public static async Task<CameraSession> StartAsync(
        MediaFrameSourceGroup group, CaptureMode mode, Action<Bitmap, bool> onFrame)
    {
        var capture = new MediaCapture();
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                SourceGroup = group,
                SharingMode = mode == CaptureMode.ExclusiveBgra
                    ? MediaCaptureSharingMode.ExclusiveControl
                    : MediaCaptureSharingMode.SharedReadOnly,
                // CPU memory asks for SoftwareBitmap frames instead of GPU surfaces.
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                StreamingCaptureMode = StreamingCaptureMode.Video,
            });

            var source = capture.FrameSources.Values
                .FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
                ?? throw new InvalidOperationException("Kamera ini tidak punya aliran gambar berwarna (kemungkinan kamera inframerah).");

            var format = source.CurrentFormat;
            var formatInfo = format is null
                ? "format tidak diketahui"
                : $"{format.Subtype} {format.VideoFormat?.Width}x{format.VideoFormat?.Height}";

            var reader = mode == CaptureMode.SharedNative
                ? await capture.CreateFrameReaderAsync(source)
                : await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;

            var session = new CameraSession(capture, reader, formatInfo, onFrame);
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

    private async void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        Interlocked.Increment(ref eventCount);

        // Drop frames while the previous one is still being converted.
        if (Interlocked.Exchange(ref converting, 1) == 1)
        {
            return;
        }
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            var video = frame?.VideoMediaFrame;
            if (video is null)
            {
                Interlocked.Increment(ref emptyCount);
                return;
            }

            var software = video.SoftwareBitmap;
            SoftwareBitmap? copied = null;
            if (software is null && video.Direct3DSurface is { } surface)
            {
                copied = await SoftwareBitmap.CreateCopyFromSurfaceAsync(surface);
                software = copied;
            }
            if (software is null)
            {
                Interlocked.Increment(ref emptyCount);
                return;
            }

            Bitmap bitmap;
            bool black;
            try
            {
                bitmap = ToBitmap(software, out black);
            }
            finally
            {
                copied?.Dispose();
            }
            Interlocked.Increment(ref convertedCount);
            onFrame(bitmap, black);
        }
        catch (Exception ex)
        {
            // This handler runs on a thread-pool thread and cannot pass the error on, so keep the
            // reason for the diagnostics instead of losing it.
            lastError = $"{ex.GetType().Name}: {ex.Message}";
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
