using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace WebcamTest;

/// <summary>Ways to read a camera; some drivers only deliver frames in one of them.</summary>
internal enum CaptureMode
{
    /// <summary>Shared access to the preview stream, the one the Windows Camera app shows.</summary>
    SharedPreview,

    /// <summary>Shared access to the video-record stream.</summary>
    SharedRecord,

    /// <summary>
    /// No video stream: take still photos one after another and show them as the preview.
    /// Slower, but needs neither the preview nor the record stream to deliver frames.
    /// </summary>
    PhotoPolling,

    /// <summary>Exclusive control of the camera, preview stream.</summary>
    ExclusivePreview,

    /// <summary>Shared access to whichever colour stream comes first (last resort).</summary>
    SharedAnyColor,
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
    private CancellationTokenSource? photoCts;
    private Task? photoTask;
    private readonly Action<Bitmap, bool> onFrame;
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

    /// <summary>Format the camera reports it is using, for example "NV12 1920x1080@30".</summary>
    public string FormatInfo { get; }

    /// <summary>Streams and formats the camera offers, for the log.</summary>
    public string SupportedFormats { get; }

    /// <summary>Counters that show where frames get lost, for the log.</summary>
    public string Diagnostics =>
        $"gambar diterima={eventCount}, kosong={emptyCount}, terkonversi={convertedCount}, galat terakhir={lastError ?? "-"}";

    public static string Describe(CaptureMode mode) => mode switch
    {
        CaptureMode.SharedPreview => "bagi pakai, aliran pratinjau",
        CaptureMode.SharedRecord => "bagi pakai, aliran rekam",
        CaptureMode.PhotoPolling => "foto berulang, tanpa aliran video",
        CaptureMode.ExclusivePreview => "kendali penuh, aliran pratinjau",
        CaptureMode.SharedAnyColor => "bagi pakai, aliran berwarna pertama",
        _ => mode.ToString(),
    };

    /// <param name="onFrame">Receives a new bitmap (the receiver owns it) and whether it is all black.</param>
    /// <exception cref="NotSupportedException">This mode does not apply to this camera.</exception>
    public static async Task<CameraSession> StartAsync(
        MediaFrameSourceGroup group, CaptureMode mode, Action<Bitmap, bool> onFrame)
    {
        var capture = new MediaCapture();
        try
        {
            if (mode == CaptureMode.PhotoPolling)
            {
                return await StartPhotoPollingAsync(capture, group, onFrame);
            }

            var exclusive = mode == CaptureMode.ExclusivePreview;
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                SourceGroup = group,
                SharingMode = exclusive ? MediaCaptureSharingMode.ExclusiveControl : MediaCaptureSharingMode.SharedReadOnly,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                StreamingCaptureMode = StreamingCaptureMode.Video,
            });

            // A camera can expose several colour streams (preview, record, photo). Only the preview
            // and record streams deliver frames on their own, so pick the stream on purpose.
            var colorSources = capture.FrameSources.Values
                .Where(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
                .ToList();
            var sources = string.Join("; ", capture.FrameSources.Values
                .Select(s => $"{s.Info.MediaStreamType}/{s.Info.SourceKind}/{s.SupportedFormats.Count} format"));
            var wanted = mode == CaptureMode.SharedRecord ? MediaStreamType.VideoRecord : MediaStreamType.VideoPreview;
            var source = (mode == CaptureMode.SharedAnyColor
                    ? colorSources.FirstOrDefault()
                    : colorSources.FirstOrDefault(s => s.Info.MediaStreamType == wanted))
                ?? throw new NotSupportedException(
                    $"Kamera tidak punya aliran {(mode == CaptureMode.SharedAnyColor ? "berwarna" : wanted.ToString())}. " +
                    $"Aliran yang ada: {sources}.");

            var formats = string.Join(", ", source.SupportedFormats
                .Where(f => f.VideoFormat is not null)
                .Select(f => $"{f.Subtype} {f.VideoFormat!.Width}x{f.VideoFormat.Height}@{FramesPerSecond(f):0}")
                .Distinct()
                .Take(MaxListedFormats));
            var supported = $"aliran yang ada: {sources}; dipakai: {source.Info.MediaStreamType}; format: {formats}";

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
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    private static double FramesPerSecond(MediaFrameFormat format) =>
        format.FrameRate is { Denominator: > 0 } rate ? (double)rate.Numerator / rate.Denominator : 0;

    public async Task StopAsync()
    {
        if (disposed)
        {
            return;
        }

        photoCts?.Cancel();
        try
        {
            if (photoTask is not null)
            {
                // A photo call that is stuck in the driver must not block closing the camera.
                await Task.WhenAny(photoTask, Task.Delay(TimeSpan.FromSeconds(2)));
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
        photoCts?.Cancel();
        if (reader is not null)
        {
            reader.FrameArrived -= OnFrameArrived;
            reader.Dispose();
        }
        capture.Dispose();
    }

    private static async Task<CameraSession> StartPhotoPollingAsync(
        MediaCapture capture, MediaFrameSourceGroup group, Action<Bitmap, bool> onFrame)
    {
        var device = group.SourceInfos
            .FirstOrDefault(i => i.SourceKind == MediaFrameSourceKind.Color)?.DeviceInformation
            ?? throw new NotSupportedException("Kamera ini tidak punya ID perangkat untuk foto berulang.");

        await capture.InitializeAsync(new MediaCaptureInitializationSettings
        {
            VideoDeviceId = device.Id,
            StreamingCaptureMode = StreamingCaptureMode.Video,
        });

        var session = new CameraSession(capture, null, "foto JPEG berulang", "", onFrame);
        session.StartPhotoLoop();
        return session;
    }

    private void StartPhotoLoop()
    {
        var cts = new CancellationTokenSource();
        photoCts = cts;
        photoTask = Task.Run(() => PhotoLoopAsync(cts.Token));
    }

    private async Task PhotoLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var stream = new InMemoryRandomAccessStream();
                    await capture.CapturePhotoToStreamAsync(ImageEncodingProperties.CreateJpeg(), stream);
                    Interlocked.Increment(ref eventCount);

                    stream.Seek(0);
                    using var input = stream.AsStreamForRead();
                    using var buffer = new MemoryStream();
                    await input.CopyToAsync(buffer, token);
                    buffer.Position = 0;
                    using var decoded = new Bitmap(buffer);
                    var bitmap = new Bitmap(decoded);

                    Interlocked.Increment(ref convertedCount);
                    onFrame(bitmap, IsBlack(bitmap));
                    await Task.Delay(30, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Keep trying; the reason is shown in the diagnostics.
                    lastError = $"{ex.GetType().Name}: {ex.Message}";
                    await Task.Delay(500, token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped on purpose.
        }
    }

    private static bool IsBlack(Bitmap bitmap)
    {
        const int size = 16;
        using var small = new Bitmap(bitmap, size, size);
        long sum = 0;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var pixel = small.GetPixel(x, y);
                sum += pixel.R + pixel.G + pixel.B;
            }
        }
        return sum / (double)(size * size * 3) < BlackMeanThreshold;
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
