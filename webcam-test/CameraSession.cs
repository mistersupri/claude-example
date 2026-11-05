using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace WebcamTest;

/// <summary>Ways to read a camera; some drivers only deliver frames in one of them.</summary>
internal enum CaptureMode
{
    /// <summary>Shared access, frame reader, the camera's default format, CPU memory.</summary>
    SharedBgra,

    /// <summary>Same, but lets Windows choose the memory type (GPU surfaces allowed).</summary>
    SharedAutoMemory,

    /// <summary>Exclusive control with the camera switched to a 720p format.</summary>
    Exclusive720,

    /// <summary>Exclusive control with the camera switched to a 480p format.</summary>
    Exclusive480,

    /// <summary>The older preview pipeline, polled with GetPreviewFrameAsync.</summary>
    PreviewPipeline,
}

/// <summary>
/// One running camera, read through the Windows MediaCapture API (the same stack as the Camera app).
/// Frames arrive on a thread-pool thread through the callback given to <see cref="StartAsync"/>.
/// </summary>
internal sealed class CameraSession : IDisposable
{
    private const double BlackMeanThreshold = 2.0;
    private const int MaxListedFormats = 24;

    private readonly MediaCapture capture;
    private readonly MediaFrameReader? reader;
    private readonly Action<Bitmap, bool> onFrame;
    private CancellationTokenSource? previewCts;
    private Task? previewTask;
    private int converting;
    private bool disposed;
    private int eventCount;
    private int emptyCount;
    private int convertedCount;
    private volatile string? lastError;

    private CameraSession(
        MediaCapture capture, MediaFrameReader? reader, string formatInfo, string supportedFormats,
        Action<Bitmap, bool> onFrame)
    {
        this.capture = capture;
        this.reader = reader;
        FormatInfo = formatInfo;
        SupportedFormats = supportedFormats;
        this.onFrame = onFrame;
    }

    /// <summary>Format the camera reports it is using, for example "NV12 1920x1080".</summary>
    public string FormatInfo { get; }

    /// <summary>Formats the camera offers, for the log. Empty for the preview pipeline.</summary>
    public string SupportedFormats { get; }

    /// <summary>Counters that show where frames get lost, for the log.</summary>
    public string Diagnostics =>
        $"gambar diterima={eventCount}, kosong={emptyCount}, terkonversi={convertedCount}, galat terakhir={lastError ?? "-"}";

    public static string Describe(CaptureMode mode) => mode switch
    {
        CaptureMode.SharedBgra => "bagi pakai, format bawaan",
        CaptureMode.SharedAutoMemory => "bagi pakai, memori otomatis",
        CaptureMode.Exclusive720 => "kendali penuh, 720p",
        CaptureMode.Exclusive480 => "kendali penuh, 480p",
        _ => "jalur pratinjau",
    };

    /// <param name="onFrame">Receives a new bitmap (the receiver owns it) and whether it is all black.</param>
    /// <exception cref="NotSupportedException">This mode does not apply to this camera.</exception>
    public static async Task<CameraSession> StartAsync(
        MediaFrameSourceGroup group, CaptureMode mode, Action<Bitmap, bool> onFrame)
    {
        var capture = new MediaCapture();
        try
        {
            return mode == CaptureMode.PreviewPipeline
                ? await StartPreviewAsync(capture, group, onFrame)
                : await StartReaderAsync(capture, group, mode, onFrame);
        }
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    private static async Task<CameraSession> StartReaderAsync(
        MediaCapture capture, MediaFrameSourceGroup group, CaptureMode mode, Action<Bitmap, bool> onFrame)
    {
        var exclusive = mode is CaptureMode.Exclusive720 or CaptureMode.Exclusive480;
        await capture.InitializeAsync(new MediaCaptureInitializationSettings
        {
            SourceGroup = group,
            SharingMode = exclusive ? MediaCaptureSharingMode.ExclusiveControl : MediaCaptureSharingMode.SharedReadOnly,
            MemoryPreference = mode == CaptureMode.SharedAutoMemory
                ? MediaCaptureMemoryPreference.Auto
                : MediaCaptureMemoryPreference.Cpu,
            StreamingCaptureMode = StreamingCaptureMode.Video,
        });

        var source = capture.FrameSources.Values
            .FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
            ?? throw new NotSupportedException("Kamera ini tidak punya aliran gambar berwarna (kemungkinan kamera inframerah).");

        var supported = string.Join(", ", source.SupportedFormats
            .Where(f => f.VideoFormat is not null)
            .Select(f => $"{f.Subtype} {f.VideoFormat!.Width}x{f.VideoFormat.Height}@{FramesPerSecond(f):0}")
            .Distinct()
            .Take(MaxListedFormats));

        if (exclusive)
        {
            var height = mode == CaptureMode.Exclusive720 ? 720u : 480u;
            var wanted = PickFormat(source, height)
                ?? throw new NotSupportedException($"Kamera tidak punya format {height}p.");
            await source.SetFormatAsync(wanted);
        }

        var format = source.CurrentFormat;
        var formatInfo = format?.VideoFormat is null
            ? "format tidak diketahui"
            : $"{format.Subtype} {format.VideoFormat.Width}x{format.VideoFormat.Height}@{FramesPerSecond(format):0}";

        var reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
        reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;

        var session = new CameraSession(capture, reader, formatInfo, supported, onFrame);
        reader.FrameArrived += session.OnFrameArrived;

        var status = await reader.StartAsync();
        if (status != MediaFrameReaderStartStatus.Success)
        {
            session.Dispose();
            throw new InvalidOperationException($"Windows menolak memulai kamera ({status}).");
        }
        return session;
    }

    private static async Task<CameraSession> StartPreviewAsync(
        MediaCapture capture, MediaFrameSourceGroup group, Action<Bitmap, bool> onFrame)
    {
        var device = group.SourceInfos
            .FirstOrDefault(i => i.SourceKind == MediaFrameSourceKind.Color)?.DeviceInformation
            ?? throw new NotSupportedException("Kamera ini tidak punya ID perangkat untuk jalur pratinjau.");

        await capture.InitializeAsync(new MediaCaptureInitializationSettings
        {
            VideoDeviceId = device.Id,
            StreamingCaptureMode = StreamingCaptureMode.Video,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu,
        });
        await capture.StartPreviewAsync();

        var properties = capture.VideoDeviceController
            .GetMediaStreamProperties(MediaStreamType.VideoPreview) as VideoEncodingProperties
            ?? throw new InvalidOperationException("Windows tidak memberi ukuran gambar pratinjau.");
        var width = (int)properties.Width;
        var height = (int)properties.Height;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("Ukuran gambar pratinjau dari Windows tidak valid.");
        }

        var session = new CameraSession(
            capture, null, $"{properties.Subtype} {width}x{height} (jalur pratinjau)", "", onFrame);
        session.StartPreviewLoop(width, height);
        return session;
    }

    private static MediaFrameFormat? PickFormat(MediaFrameSource source, uint height) =>
        source.SupportedFormats
            .Where(f => f.VideoFormat is not null && f.VideoFormat.Height == height)
            .OrderBy(f => FramesPerSecond(f) > 30.5 ? 1 : 0)
            .ThenByDescending(FramesPerSecond)
            .FirstOrDefault();

    private static double FramesPerSecond(MediaFrameFormat format) =>
        format.FrameRate is { Denominator: > 0 } rate ? (double)rate.Numerator / rate.Denominator : 0;

    public async Task StopAsync()
    {
        if (disposed)
        {
            return;
        }

        previewCts?.Cancel();
        try
        {
            if (previewTask is not null)
            {
                await previewTask;
                await capture.StopPreviewAsync();
            }
            if (reader is not null)
            {
                reader.FrameArrived -= OnFrameArrived;
                await reader.StopAsync();
            }
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
        previewCts?.Cancel();
        if (reader is not null)
        {
            reader.FrameArrived -= OnFrameArrived;
            reader.Dispose();
        }
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

    private void StartPreviewLoop(int width, int height)
    {
        var cts = new CancellationTokenSource();
        previewCts = cts;
        previewTask = Task.Run(() => PreviewLoopAsync(width, height, cts.Token));
    }

    private async Task PreviewLoopAsync(int width, int height, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var target = new VideoFrame(BitmapPixelFormat.Bgra8, width, height);
                    using var result = await capture.GetPreviewFrameAsync(target);
                    Interlocked.Increment(ref eventCount);

                    var software = result.SoftwareBitmap;
                    if (software is null)
                    {
                        Interlocked.Increment(ref emptyCount);
                    }
                    else
                    {
                        var bitmap = ToBitmap(software, out var black);
                        Interlocked.Increment(ref convertedCount);
                        onFrame(bitmap, black);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Keep polling; the reason is shown in the diagnostics.
                    lastError = $"{ex.GetType().Name}: {ex.Message}";
                }
                await Task.Delay(33, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped on purpose.
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
